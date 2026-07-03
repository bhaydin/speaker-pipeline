# Speaker Pipeline — Phase 2 Build Plan
_Operationalizing Brian's speaking pipeline: event-driven discovery, persistent state, topic injection, calendar-aware conflicts, proactive notifications._
_Version 1.0 — 2026-07-03. Drop this into the repo as `docs/BUILD_PLAN.md`._

---

## 1. Executive summary

Extend the existing **bhaydin/speaker-pipeline** scaffold into a running system on Brian's **personal Azure tenant** (~$150/mo credits). Microsoft Agent Framework agents on **Azure AI Foundry Agent Service** do the thinking; **Logic Apps Consumption** does the scheduling; a **storage account** (Table + Blob) is the portable system of record; a lightweight **MCP server** lets Claude, Copilot, or any MCP-capable surface inject topic ideas and drive pipeline commands; **Telegram** is the launch notification lane with **plain email as fallback** and Teams (Haydin.ai) deferred. The agent never submits to CFP platforms — it recommends, short-drafts, and tracks; Brian submits. Every component emits OpenTelemetry from day one, because this system is itself MVP-submission and talk material.

**Five build milestones, each independently demoable. Milestone 1 is a weekend.**

---

## 2. Architecture overview

```
                        ┌─────────────────────────────────────────┐
                        │        Personal Azure Tenant            │
                        │                                         │
  Sessionize / Meetup   │  ┌───────────┐     ┌─────────────────┐  │
  GAIC / official pages─┼─▶│  Scout    │────▶│                 │  │
  PaperCall / CFP.ninja │  │  (MAF)    │     │  Azure Table    │  │
                        │  └───────────┘     │  Storage        │  │
  Logic Apps ── cron ───┼─▶┌───────────┐     │  (system of     │  │
  (daily light /        │  │ Evaluator │────▶│   record)       │  │
   weekly deep /        │  │  (MAF)    │     │                 │  │
   monthly reminders)   │  └───────────┘     │  + Blob for     │  │
                        │  ┌───────────┐     │    artifacts    │  │
  Google Calendar ──────┼─▶│ Conflict  │────▶│                 │  │
  Concurrency M365 ─────┼─▶│ Checker   │     └───────▲─────────┘  │
  (delegated Graph)     │  │  (MAF)    │             │            │
                        │  └───────────┘     ┌───────┴─────────┐  │
  Telegram  ◀───────────┼───┌───────────┐    │   MCP Server    │◀─┼── Claude / Copilot /
  Email (Graph) ◀───────┼───│ Steward + │    │ (Functions Flex)│  │   any MCP client
  Teams (later) ◀───────┼───│ Notifier  │    └─────────────────┘  │
                        │   │  (MAF)    │                         │
                        │   └───────────┘                         │
                        │   OpenTelemetry → Azure Monitor / App   │
                        │   Insights on everything                │
                        └─────────────────────────────────────────┘
```

### Components and where they live

| # | Component | Runs on | Purpose |
|---|---|---|---|
| 1 | **Scout agent** | Foundry Agent Service (MAF, .NET 10) | Daily light scan / weekly deep scan of CFP sources per the trust-order playbook. Graceful degradation on scrape failures — fragility is accepted, never a hard dependency. |
| 2 | **Evaluator agent** | Foundry Agent Service | Scores candidates against topic lanes, decision rubric, skip lists, cadence rules. Emits decision recommendation + short rationale. Token-frugal model (small/mini class). |
| 3 | **Conflict Checker agent** | Foundry Agent Service | Checks candidate dates against Google Calendar, Concurrency calendar (delegated Graph), Blackouts table, and prep-congestion rules. |
| 4 | **Steward agent** | Foundry Agent Service | Conversational front door (Telegram webhook + MCP passthrough). Handles state commands, enforces the **submit-intent vs. submitted-confirmed clarification rule**, writes state. |
| 5 | **Notifier** | Thin .NET service or Functions, called by Steward/Logic Apps | Channel abstraction: `INotificationLane` with Telegram + Email implementations; Teams lane added later without touching callers. Urgent vs. digest routing, dedupe via NotificationLog. |
| 6 | **MCP server** | Azure Functions Flex Consumption | Remote MCP over Table Storage: topic injection + pipeline queries from any AI surface. |
| 7 | **Logic Apps (Consumption)** | Personal tenant | All cron: daily light scan, weekly deep scan, weekly digest assembly, monthly "refresh topic ideas from your AI tools" reminder, calendar-auth health check. |
| 8 | **Storage account** | Personal tenant | Table Storage = entities; Blob = decks, abstracts, exports, seed-data archive. Portable, employer-independent. |
| 9 | **Observability** | App Insights / Azure Monitor | OTel traces on every agent run, tool call, and notification. Evals harness in repo (AgentOps dogfood + MVP evidence). |

---

## 3. Data model (Table Storage)

Extends the repo's existing Events/Submissions/Talks schema. PartitionKey strategies noted per table; all entities carry `CreatedUtc`, `UpdatedUtc`, `Source`.

### 3.1 `Events`
PK: `event` (single partition is fine at this scale) · RK: `eventId` (slug)

| Field | Notes |
|---|---|
| Name, Type | conference / code camp / user group / chapter |
| Region, City, Format | in-person / remote / hybrid |
| StartDate, EndDate | |
| CfpStatus | Open / Closed / Unknown / RevalidateLive |
| CfpDeadline, CfpTimezone | |
| SourceUrl, SourceTrust | 1–8 per the trust-order playbook |
| LastVerifiedUtc, StaleFlag | Evaluator sets StaleFlag when sources conflict |
| TopicFitScore, TravelBurden, StrategicValue | rubric outputs |
| DecisionLabel | SubmitNow / Monitor / Outreach / PassForNow / **DoNotResurface** |
| DecisionRationale | one-liner |
| FamilyConflictFlag, PrepConflictFlag | set by Conflict Checker |

**DoNotResurface is enforced in code**: Scout drops matches against this label before they ever reach the Evaluator. Skip lists live in state, not in prompts.

### 3.2 `Submissions`
PK: `eventId` · RK: `submissionId`

| Field | Notes |
|---|---|
| TalkId or TopicId | what was/will be pitched |
| Status | state machine below |
| AbstractBlobUrl | short draft artifact |
| IntentDeclaredUtc, SubmittedConfirmedUtc, ResultUtc | audit trail |

**Submission state machine:**

```
Candidate → Drafting → IntendToSubmit → SubmittedConfirmed → Accepted | Rejected | Waitlisted
                                                     Accepted → Delivered → AssetsCaptured
        (any state) → Skipped / Monitoring
```

**Ambiguity guard (hard rule for the Steward):** "submit" from Brian is ambiguous. The Steward must classify the utterance as *intent* ("I'm going to submit this") vs. *confirmation* ("I submitted it") and **ask one clarifying question when confidence is low** before writing `IntendToSubmit` vs. `SubmittedConfirmed`. Skip/monitor commands write immediately, no confirmation needed.

### 3.3 `Talks` (reusable assets)
PK: `talk` · RK: `talkId`

Title, Abstract, Lane (AgentOps / HybridAgents / M365Governance / EnterpriseAIData), DeckBlobUrl, LastDeliveredDate, DeliveryCount, Variants (audience-tuned versions).

### 3.4 `Topics` (new — the idea funnel)
PK: `topic` · RK: `topicId`

| Field | Notes |
|---|---|
| Title, OneLiner | |
| Stage | Idea → Validated → Built (Built promotes into Talks) |
| Source | claude / copilot / manual / telegram / tech-trekker-post |
| Lane | which portfolio lane it feeds |
| EffortClass | **NewTopic** (~1 week: angles, demos, new deck) vs. **DeckAdapt** (light) |
| Notes, RelatedContentUrls | |

### 3.5 `Blackouts` (new — portability fix)
PK: `blackout` · RK: `blackoutId`

StartDate, EndDate, Reason, HardOrSoft. **This table — not the Concurrency calendar — is the system of record for family/blackout dates.** The Concurrency calendar remains a read-only *signal*; the Conflict Checker suggests importing detected vacation blocks into Blackouts, Brian approves via Telegram. Fixes the "sloppy after 10 years at one employer" problem structurally.

### 3.6 `NotificationLog`
PK: `yyyy-MM` · RK: `notificationId`

Channel, Urgency, EventId/TopicId ref, SentUtc, DedupeKey. Prevents re-pinging the same CFP and feeds the weekly digest assembler.

### 3.7 Migration
One-off .NET console tool in the repo: parses `01_conference_tracker.csv/md` → seeds Events, Submissions, Talks. Sections 5/6 of the tracker land as `PassForNow` / `DoNotResurface` rows. Topic families in section 7 seed the Talks lanes. Archive the originals to Blob.

---

## 4. Agents — behavior specs

### 4.1 Scout
- **Trigger:** Logic Apps — daily light scan (Sessionize, official pages of tracked targets, Meetup, GAIC chapters); weekly deep scan (adds PaperCall, CFP.ninja).
- **Behavior:** fetch → extract candidates → filter against DoNotResurface + already-tracked → write/update Events with `SourceTrust` and `LastVerifiedUtc`. On fetch failure: log, mark source degraded in the run summary, continue. No retries beyond one; no brittle per-site parsers where an RSS/API exists.
- **Token discipline:** extraction via small model; only net-new or changed events go to the Evaluator.

### 4.2 Evaluator
- **Input:** new/changed Events. **Output:** rubric scores, DecisionLabel, one-line rationale, StaleFlag on contradictory data ("revalidate live").
- Encodes the decision rubric: topic fit with proven lanes, audience quality, travel burden, deadline confidence, asset reusability, relationship value. Midwest/regional and Microsoft-ecosystem fit weighted up; short-notice Europe weighted down.
- For `SubmitNow` candidates only: generate **one short draft** (title + 2–3 sentence abstract) from the closest existing Talk asset. Nothing fully baked unless Brian asks.

### 4.3 Conflict Checker
- **Inputs:** candidate event dates, Google Calendar (OAuth), Concurrency calendar (delegated Graph), Blackouts table, current pipeline load.
- **Rules:**
  - Hard conflict: overlaps a Blackout or existing committed engagement.
  - Prep congestion: >2 talks in a month → warn; **>1 NewTopic-class effort in a month → flag loudly**. DeckAdapt engagements count light.
  - Prep window: a NewTopic engagement blocks the ~1 week before it.
- **Health check:** if the Concurrency delegated token fails, emit an urgent "re-auth needed" notification (Logic Apps pings a probe endpoint daily). Known trade-off of delegated auth; swap to IT-consented application permissions later without redesign — same Graph calls, different token acquisition.

### 4.4 Steward (conversational front door)
- Surfaces: Telegram webhook (primary), MCP tools (secondary), email replies parsed later if wanted.
- Commands (natural language, not slash-syntax): skip X, monitor X, I submitted X, I'm going to submit X, add topic idea, what's closing soon, show pipeline, add blackout.
- Enforces the ambiguity guard (§3.2). All writes are audit-logged.

### 4.5 Notifier
- `INotificationLane` abstraction; implementations: `TelegramLane` (launch), `EmailLane` (Graph sendMail from the Haydin.ai mailbox — zero-maintenance fallback), `TeamsLane` (deferred).
- **Routing:** Urgent = high-fit CFP closing <7 days, calendar-auth failure, hard conflict on a committed event → immediate. Everything else → weekly digest (exec summary, ranked list, deadline urgency, next actions — matching Brian's preferred output format).

---

## 5. MCP server (Milestone 1 centerpiece)

**Host:** Azure Functions Flex Consumption, remote MCP (streamable HTTP). **Auth:** function key initially; Entra-based OAuth later if a client requires it.

**Tools (v1):**

| Tool | Purpose |
|---|---|
| `add_topic_idea(title, one_liner, lane?, notes?)` | Inject an idea from any AI conversation mid-flight |
| `list_topics(stage?)` | See the idea funnel |
| `list_pipeline(filter?)` | Events + submissions, filterable by DecisionLabel/status |
| `get_upcoming_deadlines(days)` | What's closing soon |
| `update_pipeline(entity_id, action, note?)` | skip / monitor / intend-to-submit / submitted-confirmed — server enforces the ambiguity guard by requiring explicit `intend` vs. `confirmed` action values |
| `add_blackout(start, end, reason)` | Feed the conflict engine |

This is the "AI memories become a talk pipeline" story: any Claude or Copilot session where an idea surfaces becomes a one-sentence tool call. The **monthly Logic Apps reminder** ("review recent AI conversations and published content for talk-worthy ideas — inject via MCP") covers the memory-synthesis requirement without fragile automated memory export.

---

## 6. Auth model

| Connection | Method | Notes |
|---|---|---|
| Agents ↔ Storage | Managed identity, RBAC (Table/Blob Data Contributor) | No connection strings in code |
| Logic Apps ↔ agents | Managed identity → Foundry agent endpoints | |
| Google Calendar | OAuth app in Google Cloud Console (Calendar read-only scope), refresh token in **Key Vault** | Brian sets up once; ~30 min. Note: the planned Google Workspace → M365 migration eventually deletes this connector entirely — build it thin. |
| Concurrency M365 | **Delegated** Graph `Calendars.Read`, refresh token in Key Vault, daily health probe | Escalate to application permissions via IT consent only if re-auth nagging becomes a pattern |
| Haydin.ai M365 | App registration in the tenant Brian administers; `Mail.Send` for EmailLane | Already licensed. Calendar connector deferred until the domain migration lands |
| Telegram | Bot token (BotFather) in Key Vault; webhook → Steward | Free |
| MCP clients | Function key → later OAuth | |

Key Vault (standard tier) holds every secret; agents read via managed identity.

---

## 7. Build order — five demoable milestones

### Milestone 1 — State + MCP (a weekend)
1. Resource group, storage account, Key Vault, App Insights in personal tenant (Bicep in repo, `infra/`).
2. Tables + entity classes (extend repo's existing schema code); migration console tool; seed from tracker CSV/MD.
3. MCP server on Functions Flex with the six v1 tools.
4. **Demo:** from a Claude chat — "add a topic idea about model-access supply-chain risk" → row appears in Topics; "what's closing soon" → answers from live state.

### Milestone 2 — Steward + Telegram (1–2 evenings)
1. BotFather bot; webhook → Steward agent (MAF on Foundry Agent Service — first agent deployed).
2. State commands + ambiguity guard + audit logging.
3. EmailLane via Haydin.ai Graph `Mail.Send` as fallback.
4. **Demo:** text the bot "I submitted the Data-Driven WI talk" → Steward asks nothing (clear confirmation), state moves to SubmittedConfirmed; "submit the St. Louis one" → Steward asks intent-vs-done.

> **Carried forward from Milestone 1:** the `NotificationLog` entity, repository,
> and table exist as of M1, but its **API endpoints were intentionally deferred to
> M2** — nothing wrote to it until the Notifier existed. Add `/v1/notifications`
> (write + dedupe-key query) alongside the Notifier here. See ADR 0001 and the M1
> entity work.

### Milestone 3 — Scout + Evaluator + scheduling (the core loop, ~1 week of evenings)
1. Scout with source adapters (Sessionize first, then Meetup/GAIC, then PaperCall/CFP.ninja) — graceful degradation baked in.
2. Evaluator with rubric + short-draft generation for SubmitNow only.
3. Logic Apps: daily light scan, weekly deep scan, weekly digest, monthly topic-refresh reminder.
4. **Demo:** the system finds a CFP overnight and Telegram pings before Brian's coffee. This is the moment the "constantly pinging it myself" problem dies.

### Milestone 4 — Conflict engine (~2–3 evenings)
1. Google OAuth connector; Concurrency delegated Graph connector; Blackouts table + import-suggestion flow.
2. Prep-congestion rules; health probe + urgent re-auth alert.
3. **Demo:** a high-fit CFP lands on a blackout week → notification arrives pre-flagged "conflicts with family dates — pass?"

### Milestone 5 — AgentOps polish + optional lanes (ongoing)
1. Evals harness in repo (golden-set candidates → expected DecisionLabels; run in CI).
2. Azure Monitor workbook: scan health, token spend, decision distribution.
3. TeamsLane (Haydin.ai) when the domain migration lands; OpenClaw/local-hardware surface as the Phase-2 personal-AI demo.
4. **This milestone is the MVP-submission and conference-talk evidence**: ADRs, OTel traces, evals, cost data — "AgentOps for Real," dogfooded.

---

## 8. Cost estimate (monthly, personal tenant)

| Item | Est. |
|---|---|
| Storage account (Tables + Blob, trivial volume) | < $1 |
| Logic Apps Consumption (~40 runs/mo + connectors) | < $1 |
| Functions Flex (MCP + Notifier, low traffic) | ~$0–3 |
| Key Vault standard | < $1 |
| App Insights (sampled) | $2–5 |
| Foundry model calls — daily scan extraction + eval on small/mini-class models, short drafts only | $10–30 |
| **Total** | **~$15–40/mo** — comfortably inside $150 credits |

Cost lever if needed: drop daily → 3×/week scans; cache source pages and only re-extract on content hash change (worth doing in Milestone 3 regardless).

---

## 9. Risks and maintenance posture

| Risk | Posture |
|---|---|
| Source scraping breaks (Sessionize markup changes, etc.) | Accepted by design. Degrade gracefully, report source health in digest, fix adapters lazily. Never a hard dependency; Brian's manual awareness is the backstop. |
| Delegated Concurrency token silently revoked | Daily health probe + urgent alert; documented upgrade path to application permissions. |
| Google Workspace → M365 migration in flight | Google connector built thin/disposable; Blackouts table (not any calendar) is the conflict system of record. |
| Token cost creep | Small models for extraction/eval, changed-content gating, monthly cost line in the digest. |
| Notification fatigue | Urgent-vs-digest split + NotificationLog dedupe; digest capped at ranked top items, not exhaustive lists. |
| Employer portability | Everything (state, compute, secrets, identity for the system) lives in the personal tenant. Concurrency is a read-only calendar signal, nothing more. |

---

## 10. Immediate next actions

1. Create the resource group + Bicep scaffold (Milestone 1, step 1).
2. Confirm the existing repo schema code matches §3 or adjust entities (Topics, Blackouts, NotificationLog are net-new).
3. Create the Telegram bot via BotFather now (2 minutes) so the token exists when Milestone 2 starts.
4. Decide the MCP server repo location: same repo `/src/McpServer` (recommended — one deploy story) vs. separate repo.
