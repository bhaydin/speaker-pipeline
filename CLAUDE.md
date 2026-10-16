# Claude Code — Project Context

## Purpose and current state

This is Brian Haydin's personal speaking pipeline and public AgentOps reference.
Keep committed material useful to a third-party reader and free of real pipeline
data, contacts, credentials, and private deployment identifiers.

There is **one logical production environment**. The existing API App Service and
agent Functions app retain historical `dev` physical names and URLs. Do not create
a second hosted environment or rename resources as part of production cleanup.
Local development uses isolated loopback Azurite. Read
[docs/production-rollout.md](docs/production-rollout.md) and
[.azure/deployment-plan.md](.azure/deployment-plan.md) before rollout work.

Discovery, scoring, tracker maintenance, Telegram, notifications, and the data API
are implemented. Azure Functions hosts the agent workload; Azure AI Foundry
provides model inference. The Microsoft Agent Framework ecosystem and
`Microsoft.Extensions.AI.IChatClient` provide the .NET model abstractions. This is
not a deployment to Foundry Agent Service. MCP is implemented but not deployed;
OpenClaw and external calendar integrations remain future work. Do not describe
planned capabilities as active production behavior.

## Architecture boundaries

- All consumer data access goes through `SpeakerPipeline.Api`, under `/v1`.
  `SpeakerPipeline.Client` supplies the ordinary HTTP client.
- `SpeakerPipeline.Storage` is internal to the API and its tests. Functions,
  MCP, migration tools, and future OpenClaw clients must never reference it or
  access pipeline tables directly.
- The App Service API uses managed identity for six business tables: `Events`,
  `Submissions`, `Talks`, `Topics`, `Blackouts`, and `NotificationLog`. The
  [schema](docs/architecture-table-storage.md) is authoritative; read it before
  changing persistence, and keep it aligned with Core models and Storage mapping.
- Deployed hosts use `Production`. API JWT validation and `Pipeline.Access`
  restrict ordinary callers; the operator-only `Pipeline.Maintenance` role and
  maintenance enable/freeze controls protect administrative operations.
- Agents use managed identity for the API and Foundry. Do not grant them pipeline
  Table Storage permissions. Private cleanup snapshots/journals belong to the
  API-owned maintenance path and operator tooling.
- Functions send configured operator notifications directly through email and
  Telegram. These are not organizer outreach or speaker submissions. Outbound
  submissions and an OpenClaw approval workflow are not implemented.
- OpenTelemetry and Application Insights provide observability. Verify deployed
  telemetry and authenticated readiness; a successful package upload is not a
  completed production rollout.

## Data and release rules

1. Use **Microsoft Agent Framework (MAF)** terminology and the Microsoft/Azure
   stack unless an ADR explains a different choice.
2. Keep fictional fixtures in `samples/` and tests. Real records, exports,
   manifests, journals, contacts, tokens, and deployment credentials stay outside
   Git, public logs, and CI artifacts. Use placeholders in examples.
3. Never run sample imports against production. Use the migration CLI's explicit
   `samples` command locally; its `import` command previews by default. The old
   direct-table seeding script is retired.
4. Cleanup requires a frozen consistent snapshot, reviewed exact operations,
   private backup, restore rehearsal, and ETag checks. Preserve real records and
   all four canonical talks. Treat ambiguous rows as retain/review, and preserve
   notification deduplication history unless a reviewed operation identifies it.
5. Deploy only successful CI artifacts through the manual production workflow.
   Code releases preserve writer/notification pause controls. Maintain the prior
   successful packages/configuration and data recovery material separately.
6. Keep `.NET` versions in `global.json`/`Directory.Build.props` and NuGet versions
   in `Directory.Packages.props`; do not duplicate version-release calendars here.

## Evaluation and testing

Every agent behavior change must review whether new or updated evals are needed.
Add a golden for a new decision branch; rerun the relevant eval suite for prompt
changes. Pure refactors and operational tooling may need no agent eval changes,
but record that assessment. Both deterministic eval suites and all unit tests
are blocking CI checks; do not waive a regression as drift noise.

Run checks appropriate to the change. Maintenance changes need meaningful tests
for target binding, complete export, concurrency, partial failure/resume, and
restore. Keep tests and rehearsals on synthetic data and isolated local stores.

## Repository map

- `src/`: Core, API, Storage, Client, Discovery/Scoring/TrackerMaintenance,
  Functions hosting, Notifications, Telegram, and the undeployed MCP host.
- `tests/`: corresponding xUnit suites, both agent eval suites, and migration tests.
- `tools/SpeakerPipeline.Migrate/`: API-only import/export/cleanup/recovery CLI.
- `infra/`: reconciliation of the existing estate; follow its README and inspect
  what-if before applying infrastructure changes.
- `docs/`: architecture, schema, decisions, and the production runbook. Historical
  build-plan documents are roadmap history, not proof of deployed state.
- `scripts/`: explicit operational helpers; `.github/workflows/`: CI and manual
  production release. `samples/`: fictional fixtures that stay available locally.

## Style

Use concise, factual prose. Explain implemented behavior and actual verification;
avoid phase placeholders, marketing language, unsupported deployment claims, and
real-world examples that leak pipeline data. Keep Markdown and Mermaid valid.
