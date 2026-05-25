# Claude Code — Project Context

## What this repo is

This is **Brian Haydin's personal speaking pipeline**, intentionally built in the open as a production-shaped AgentOps case study. It will be referenced in Brian's conference talks (AgentOps lane), Tech Trekker newsletter posts, and Concurrency client demos. Treat every file as something a third party might read — clarity, hygiene, and credibility matter more than they would for a private tool.

The architecture deliberately combines two agent platforms. **Microsoft Agent Framework (MAF)** runs the cloud multi-agent backbone in Azure AI Foundry Agent Service: per-source discovery agents (Sessionize, PaperCall, CFP.ninja, Meetup, Global AI chapters), a verification agent, a scoring agent, and a tracker-maintenance agent. **OpenClaw** runs locally as the personal-AI surface layer (weekly digest, Telegram, Outlook, approval gating). Both share **Azure AI Foundry** as the model fabric. Persistence is **Azure Table Storage** with three tables: `Events`, `Submissions`, `Talks`.

## Architecture summary

- **Discovery → Verification → Scoring → Tracker maintenance** is the MAF agent chain. Each writes through `Azure.Data.Tables` with `DefaultAzureCredential`. Managed identity, no connection strings.
- **OpenClaw** is read-mostly against Tables, plus the only path to outbound action (proposed submission, draft email). All outbound action is human-approved before send.
- **OpenTelemetry → Application Insights** for tracing. Application Insights is the AgentOps observability surface.
- **Azure AI Foundry** is the shared model layer. OpenClaw uses the `@openclaw/microsoft-foundry` plugin or the LiteLLM bridge; MAF uses its native Foundry binding.

## Ground truth: persistence schema

[docs/pipeline_table_storage_schema.md](docs/pipeline_table_storage_schema.md) is the authoritative spec for the `Events`, `Submissions`, and `Talks` tables — fields, types, partition strategy, query patterns, gotchas. **If your work touches the data layer, read it first and align to it.** Don't restate the schema elsewhere; link to that file.

## Hard rules

1. **Terminology — always say "Microsoft Agent Framework" (MAF).** The name "Semantic Kernel" is retired. If you see it anywhere in this repo or in your generated output, treat it as a bug and replace it.
2. **Microsoft-first stack bias.** When two roughly equal options exist, pick the Microsoft / Azure one (Foundry over alternatives, Azure Table Storage over Cosmos for this volume, App Insights over third-party APM, managed identity over connection strings). Justify in an ADR if you deviate.
3. **Production-shaped patterns, not toy demos.** Real auth (managed identity), real observability (OTel + App Insights), real idempotency, real error handling. No `Console.WriteLine` agents.
4. **No real pipeline data in commits.** The samples in [samples/](samples/) are sanitized fictional events ("Northwoods Tech Summit," "Great Lakes Cloud Conf," "Driftless AI Days," etc.) with fictional speakers and organizers. Any real CFP data, real contact, real event lives outside the repo.
5. **No secrets — anywhere, ever.** Not in code, not in tests, not commented out. Not connection strings, not subscription IDs, not tenant IDs, not API keys, not personal email addresses. Use `<placeholder>` syntax when an example is needed.

## Current phase

**Phase 1 — scaffolding (complete).** Repo skeleton, license/legal, README, architecture docs, samples, CI stub, contributor docs.

**Phase 2 — first agent slice (next).** Recommended first cut:

1. Provision real Azure resources via `scripts/provision-azure.sh`.
2. Seed `Talks` with Brian's canonical four lanes via `scripts/seed-tables.ps1`.
3. Build the **tracker maintenance agent** first — it's the smallest, has the clearest contract (read row → re-verify URL → update `LastVerifiedUtc` / `StatusDetail`), and exercises the auth + observability path end-to-end.
4. Then the **Sessionize discovery agent** as the first writer into `Events`.
5. Wire OTel → App Insights from day one. Observability is not a Phase 3 problem.

Don't skip ahead to scoring or OpenClaw integration until those two agents are running cleanly.

## Style

- README and docs are exec-ready — concise, no marketing fluff.
- Brian uses outdoor / fishing analogies in his talks (Great Lakes salmon angler, hunter, forager). They're welcome in docs **only when they clarify something real** — never forced or decorative.
- Markdown must render cleanly on GitHub. Mermaid diagrams should be sanity-checked.
- Dense over fluffy. Cut adjectives. Keep nouns and verbs.

## Folder map

- [src/](src/) — .NET solution lands here in Phase 2. Empty placeholder for now.
- [docs/](docs/) — architecture, schema, ADRs.
- [scripts/](scripts/) — provisioning and seeding helpers.
- [samples/](samples/) — sanitized JSON that validates against the schema.
- [.github/](.github/) — CI, issue and PR templates.
