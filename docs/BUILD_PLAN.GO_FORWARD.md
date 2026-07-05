# Speaker Pipeline — Go-Forward Plan (v2)

_Post-audit revision, 2026-07-05. Supersedes the milestone ordering in `docs/BUILD_PLAN.md`; architecture and constraints in that document still apply._
_This document is currently `docs/BUILD_PLAN.GO_FORWARD.md` (rename/move to `docs/GO_FORWARD.md` if you want it to be canonical)._

---

## 1. Audit verdict

Milestones 1–2 of BUILD_PLAN.md are **complete**. Milestone 3 is **built but underperforming**. Milestone 5 assets (evals, goldens, OTel, ADRs, CI/CD) are **ahead of schedule**. Milestone 4 (calendars) is **not started, as planned**.

The observed symptoms — "not picking up new engagements" and "recommendations don't make sense" — trace to three code-level root causes, not missing capability:

| # | Root cause | Where | Effect |
|---|---|---|---|
| RC1 | Watchlist unconfigured; all discovery flows through 10 Google PSE snippet queries biased to `site:sessionize.com` | `infra/functions.settings.json`, `DiscoveryOptions.Watchlist` | Stale/duplicate/closed CFPs; violates the project's own "never trust snippets" rule |
| RC2 | Whole-page regex strip + 12K char cap starves the extractor; sub-floor candidates dropped silently | `WatchlistSource.Normalize()`, `MinConfidence` | Real events discarded invisibly; system looks dead |
| RC3 | Scoring prompt receives event + talks only — no committed engagements, blackouts, or load | `ScoringRubric.BuildUserPrompt()` | Rubric demands calendar/congestion awareness the model cannot have; verdicts feel arbitrary |

**Strategic conclusion:** do not broaden the agent surface. Fix discovery fuel (Phase A) and scoring context (Phase B), then resume the original Milestone 4 (Phase C).

---

## 2. Phase A — Fix the fuel line  _(priority 1, ~3–4 evenings)_

### A1. Structured-feed adapter: confs.tech  _(highest signal, zero fragility)_

- New `ConfsTechSource : ISourceAdapter` in `SpeakerPipeline.Agents.Discovery`.
- Data: raw JSON from GitHub `tech-conferences/conference-data`, path pattern
  `conferences/{year}/{topic}.json`. **Verified live 2026-07-05.** Fields include
  name, url, startDate/endDate, city, country, online, and (on many entries)
  `cfpUrl` + `cfpEndDate`.
- Topics to pull: `dotnet`, `ai` (or `data` + `devops` as configured), current year + next year.
- Client-side filters (config-driven):
  - Geo bias: US default; weight Midwest metros up; exclude non-US unless `online: true`.
  - Drop events with `startDate` in the past or CFP already closed.
- Because the payload is structured, **bypass LLM extraction entirely** — map JSON → `ExtractedEvent` with confidence 9. Zero tokens, zero scraping risk.
- Add `SourceSeenOn.ConfsTech` enum value; trust level between Sessionize and official page.
- Same reconcile path as today (idempotent, DoNotResurface honored).

### A2. Manual ingestion lane  _(answers "can I feed it external searches?")_

- **MCP tool** `ingest_event(url, note?)` in a new `EventTools.cs`:
  1. Fetch URL via existing `WatchlistSource` fetch/normalize path.
  2. Run existing LLM extraction with a **lower floor (2)** — a human vouched for it.
  3. Reconcile into tracker; queue for scoring; return the extracted summary so the calling AI session can confirm.
- **Telegram command** `/track <url> [note]` → same path via the API.
- **API endpoint** `POST /events/ingest { url, note }` — both surfaces call this; logic lives once.
- Usage pattern this unlocks: any Claude/ChatGPT research session ("find me AI CFPs closing this month") ends with the assistant calling `ingest_event` per keeper. External research becomes pipeline state instead of chat scrollback.

### A3. Funnel visibility  _(kill the silent drops)_

- Extend `DiscoveryResult`/digest to a per-run funnel: `targets → fetched → extracted → passedFloor → new/updated`, with per-drop reason codes (`fetch_failed`, `not_event`, `low_confidence:N`, `do_not_resurface`, `unchanged`).
- **Quarantine tier:** candidates scoring `floor-2 .. floor-1` are written with `DecisionCategory=Quarantine` instead of dropped. Weekly digest lists them; Telegram `/promote <slug>` re-runs extraction at low floor and sends to scoring.
- Digest gains one line: token + search-query spend for the run (cost lever visibility).

### A4. Search & extraction tuning

- PSE queries: add `dateRestrict=m2` via `SearchOptions`; retire the three pure-snippet (non-`site:`) queries; add 2–3 aggregator-targeted queries (e.g. `site:sessionize.com inurl:cfs "2026"`).
- `Normalize()`: prefer `<main>`/`<article>` content when present before whole-page strip; raise cap 12K → 24K chars (still cheap on a nano-class model).
- Keep one-attempt/no-retry degradation posture — unchanged.

**Phase A exit test:** a scheduled run produces ≥5 genuinely new, currently-open, topic-relevant candidates in the digest, with a funnel report showing where the rest went; `/track` on a pasted Sessionize URL lands a scored event in under a minute.

---

## 3. Phase B — Make recommendations make sense  _(~2 evenings)_

### B1. Pipeline context in the scoring prompt

- Extend `BuildUserPrompt(event, talks, context)` with a `PipelineContext` block:
  - Committed engagements within ±8 weeks of the candidate (name, dates, EffortClass).
  - Blackout ranges within ±4 weeks.
  - Count of NewTopic-class preps currently in flight this month and next.
  - Existing DecisionLabel if the event was scored before, with date.
- API: `GET /pipeline/context?window=...` assembles this server-side (one query set, cached per run).
- **Update goldens** in `SpeakerPipeline.Agents.Scoring.Evals` to include context blocks — the eval suite is the safety net for this prompt change; treat a goldens diff as part of the PR.

### B2. Re-score triggers

- Tracker-maintenance timer re-queues scoring when: CFP deadline enters 14-day window; event fields materially change; a new Blackout overlaps a `SubmitNow`/`Monitor` event.
- Scoring digest distinguishes **new verdicts** from **changed verdicts** (verdict flips are the interesting signal).

### B3. Deadline urgency wiring

- Urgent notification path (already present in `NotificationPolicy`) fires on: `SubmitNow` + deadline <7 days. Confirm policy covers verdict-flip-to-SubmitNow.

**Phase B exit test:** a synthetic event colliding with a committed engagement scores `Pass/Monitor` with a rationale naming the conflict; the same event with a clear calendar scores `SubmitNow`. Both as eval goldens.

---

## 4. Phase C — Conflict engine  _(original Milestone 4, resequenced)_

1. **C1 Blackouts-first conflict check** (no new auth): Conflict evaluation inside tracker maintenance using the existing Blackouts table + committed engagements. Sets `FamilyConflictFlag`/`PrepConflictFlag` consumed by B1's context block.
2. **C2 Google Calendar** read-only OAuth connector (thin/disposable — the Workspace→M365 migration will delete it).
3. **C3 Concurrency M365** delegated Graph `Calendars.Read` + daily health probe + urgent re-auth alert. Documented upgrade path to app permissions.
4. **C4 Import-suggestion flow:** detected vacation blocks in work calendar → Telegram suggestion → approved entries written to Blackouts (portable system of record).

---

## 5. Phase D — Polish & optional lanes

- Azure Monitor workbook: scan health, funnel trends, token/search spend, decision distribution.
- Quarantine review UX improvements based on real use.
- TeamsLane (Haydin.ai) after domain migration; OpenClaw/local surface as Phase-2 demo.
- MVP evidence capture: screenshot funnel dashboards, eval diffs, and cost data as you go — this phase writes the Foundry MVP submission for you.

---

## 6. Feature-set lock

**In scope (locked):** structured feeds (confs.tech), manual URL ingestion (MCP + Telegram), funnel/quarantine visibility, context-aware scoring, re-score triggers, blackout/calendar conflict engine, digest + urgent notifications, evals as change-gate.

**Explicitly out (unchanged from v1):** CFP submission automation; WhatsApp; automated AI-memory export (monthly reminder + MCP injection instead); per-site scraping frameworks; anything that couples state or identity to the Concurrency tenant.

---

## 7. Claude Code kickoff prompts (one per phase)

Phase A:

```text
Read docs/GO_FORWARD.md Phase A and docs/BUILD_PLAN.md constraints. We are
building A1–A4 only. Before code: give me the commit plan, the new
ISourceAdapter surface for ConfsTechSource, the ingest endpoint contract, and
the funnel/quarantine model changes. Note: confs.tech JSON maps straight to
ExtractedEvent — no LLM call for that source. Stop after the plan.
```

Phase B:

```text
Read docs/GO_FORWARD.md Phase B. We are adding PipelineContext to scoring.
Before code: show the PipelineContext record, the BuildUserPrompt change, the
/pipeline/context endpoint, and the goldens you will add/modify in
SpeakerPipeline.Agents.Scoring.Evals. Goldens diff ships in the same PR.
Stop after the plan.
```
