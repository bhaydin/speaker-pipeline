# Claude Code — Project Context

## What this repo is

This is **Brian Haydin's personal speaking pipeline**, intentionally built in the open as a production-shaped AgentOps case study. It will be referenced in Brian's conference talks (AgentOps lane), Tech Trekker newsletter posts, and Concurrency client demos. Treat every file as something a third party might read — clarity, hygiene, and credibility matter more than they would for a private tool.

The architecture deliberately combines two agent platforms. **Microsoft Agent Framework (MAF)** runs the cloud multi-agent backbone in Azure AI Foundry Agent Service: per-source discovery agents (Sessionize, PaperCall, CFP.ninja, Meetup, Global AI chapters), a verification agent, a scoring agent, and a tracker-maintenance agent. **OpenClaw** runs locally as the personal-AI surface layer (weekly digest, Telegram, Outlook, approval gating). Both share **Azure AI Foundry** as the model fabric. Persistence is **Azure Table Storage** with three tables: `Events`, `Submissions`, `Talks`.

## Platform versions and SDKs (as of Phase 2)

- **.NET 10 (LTS).** Released 2025-11-11, supported through 2028-11-14. All projects target `net10.0`. Earlier references to .NET 9 were wrong — fix them on sight.
- **Microsoft Agent Framework 1.0 GA** (released 2026-04-03). NuGet package: `Microsoft.Agents.AI` (production-ready, not preview). Provider bindings via `Microsoft.Agents.AI.OpenAI` for Foundry / Azure OpenAI / OpenAI-compatible endpoints.
- **`Microsoft.Extensions.AI`** is the provider-agnostic abstraction layer. `IChatClient` is the entry point for chat completions; MAF builds on top of it.
- **Visual Studio 2026** is the supported IDE. **VS Code + C# Dev Kit** is also supported.

## Architecture summary

- **Discovery → Verification → Scoring → Tracker maintenance** is the MAF agent chain. Agents talk to the **API**, not Storage. Each writes through the API's REST surface.
- **OpenClaw** is read-mostly against the API, plus the only path to outbound action (proposed submission, draft email). All outbound action is human-approved before send.
- **OpenTelemetry → Application Insights** for tracing. Exporters are config-driven (Console in dev, AzureMonitor when `ApplicationInsights:ConnectionString` is set). Application Insights is the AgentOps observability surface.
- **Azure AI Foundry** is the shared model layer. OpenClaw uses the `@openclaw/microsoft-foundry` plugin or the LiteLLM bridge; MAF agents use `Microsoft.Agents.AI.OpenAI` against the Foundry endpoint.

### API as the only data boundary

- **All data access goes through `SpeakerPipeline.Api`.** REST + OpenAPI, URL-versioned starting at `/v1`.
- **`SpeakerPipeline.Storage` is internal to the API.** Agents, the Functions host, OpenClaw, and any external consumer **never** reference Storage directly. The project reference graph enforces this at the compiler level — don't add a back-channel reference, even temporarily.
- Auth in deployed environments: **Entra ID** (bearer token validation on the API) plus **Managed Identity** for service-to-service calls.
- Hosting: **Azure Functions** for agents (timer + http triggers). **App Service or Container Apps** for the API — decision deferred to Phase 3.
- Observability: OpenTelemetry from the first commit, never bolted on later.

## Ground truth: persistence schema

[docs/architecture-table-storage.md](docs/architecture-table-storage.md) is the authoritative spec for the `Events`, `Submissions`, and `Talks` tables — fields, types, partition strategy, query patterns, gotchas. **If your work touches the data layer, read it first and align to it.** Don't restate the schema elsewhere; link to that file.

## Hard rules

1. **Terminology — always say "Microsoft Agent Framework" (MAF).** The name "Semantic Kernel" is retired. If you see it anywhere in this repo or in your generated output, treat it as a bug and replace it.
2. **Microsoft-first stack bias.** When two roughly equal options exist, pick the Microsoft / Azure one (Foundry over alternatives, Azure Table Storage over Cosmos for this volume, App Insights over third-party APM, managed identity over connection strings). Justify in an ADR if you deviate.
3. **Production-shaped patterns, not toy demos.** Real auth (managed identity), real observability (OTel + App Insights), real idempotency, real error handling. No `Console.WriteLine` agents.
4. **No real pipeline data in commits.** The samples in [samples/](samples/) are sanitized fictional events ("Northwoods Tech Summit," "Great Lakes Cloud Conf," "Driftless AI Days," etc.) with fictional speakers and organizers. Any real CFP data, real contact, real event lives outside the repo.
5. **No secrets — anywhere, ever.** Not in code, not in tests, not commented out. Not connection strings, not subscription IDs, not tenant IDs, not API keys, not personal email addresses. Use `<placeholder>` syntax when an example is needed.
6. **Storage is API-only.** `SpeakerPipeline.Storage` is referenced **only** by `SpeakerPipeline.Api` and its tests. Adding any other reference breaks the architecture and should be reverted in review.

## The eval rule

> **Every new feature must review whether new or updated evals are needed before the feature is considered complete. PRs that add agent capability without corresponding eval updates should be flagged in review.**

Concretely:

- Agent behavior change → add or update a golden in the agent's `*.Evals` project.
- New decision branch (new `Recommendation` value, new rubric factor) → add a golden that exercises it.
- Prompt edit → re-run the eval suite locally and attach the report to the PR.
- Pure refactor or non-agent code → eval changes optional, but the PR description should call out that you checked.

The eval suite is built to surface drift: a previously-passing case that now fails is a regression, not noise. Treat it as a build failure even if CI is configured to warn instead of fail (initial Phase 2 state).

## Current phase

**Phase 1 — scaffolding (complete).** Repo skeleton, license/legal, README, architecture docs, samples, CI stub, contributor docs.

**Phase 2 — scoring foundation (in progress).** Solution scaffolded with `Core`, `Storage`, `Api`, `Agents.Scoring`, and `Hosting.Functions` projects (plus their tests). Scoring agent built on `Microsoft.Agents.AI` + `IChatClient`. Eval harness in `SpeakerPipeline.Agents.Scoring.Evals`. OpenTelemetry wired from day one.

**Phase 3 — discovery and verification (next).** Per-source discovery agents (Sessionize first), verification agent, and the tracker-maintenance agent that flips `Events.Category` based on `Submissions.Status` changes. OpenClaw integration deferred to Phase 4.

## Style

- README and docs are exec-ready — concise, no marketing fluff.
- Brian uses outdoor / fishing analogies in his talks (Great Lakes salmon angler, hunter, forager). They're welcome in docs **only when they clarify something real** — never forced or decorative.
- Markdown must render cleanly on GitHub. Mermaid diagrams should be sanity-checked.
- Dense over fluffy. Cut adjectives. Keep nouns and verbs.

## Folder map

- [src/](src/) — .NET 10 solution (`SpeakerPipeline.sln`) with `Core`, `Storage`, `Api`, `Agents.Scoring`, `Hosting.Functions`.
- [tests/](tests/) — xUnit test projects mirroring `src/`, plus the `Agents.Scoring.Evals` eval suite.
- [docs/](docs/) — architecture, schema, ADRs.
- [scripts/](scripts/) — provisioning and seeding helpers.
- [samples/](samples/) — sanitized JSON that validates against the schema and `SpeakerPipeline.Core` models.
- [.github/](.github/) — CI, issue and PR templates.
