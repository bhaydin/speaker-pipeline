# SpeakerPipeline.Mcp

Remote **MCP server** on Azure Functions (isolated worker, Flex Consumption).
It exposes the pipeline to any MCP-capable surface — Claude, Copilot — so an idea
that surfaces mid-conversation becomes a one-sentence tool call, and pipeline
state is queryable from the same chat.

## Boundary

Every tool is a thin adapter over `ISpeakerPipelineApiClient` (from
`SpeakerPipeline.Client`). This project references `Core` + `Client` and **never**
`SpeakerPipeline.Storage` — all data access is through the API. See
[ADR 0001](../../docs/adr/0001-consumers-reach-data-through-the-api.md).

## The six v1 tools

| Tool | Purpose |
|---|---|
| `add_topic_idea(title, one_liner?, lane?, notes?)` | Inject an idea into the Topics funnel |
| `list_topics(stage?)` | Read the funnel, optionally by stage |
| `list_pipeline(filter?)` | List tracked events, optionally by category |
| `get_upcoming_deadlines(days)` | Events with a CFP deadline inside N days |
| `update_pipeline(entity_id, action, note?)` | Apply `skip` / `monitor` / `intend` / `confirmed` |
| `add_blackout(start, end, reason)` | Add an unavailable date range |

`update_pipeline` accepts only the closed action set — there is no ambiguous
"submit". The caller states `intend` vs. `confirmed` explicitly, which is the
structural half of the submit-intent-vs-confirmation ambiguity guard
(BUILD_PLAN §3.2/§5). The API enforces the rest.

## Auth & transport

- Transport: streamable HTTP / SSE at `/runtime/webhooks/mcp`, secured by the
  Functions `mcp_extension` system key. Entra OAuth can be layered later.
- Outbound calls to the API attach a bearer token: the literal `dev` token
  locally, a managed-identity token in Azure (`SpeakerPipelineApi:Scope`).
- The SSE transport uses `AzureWebJobsStorage` queues; the deployed identity
  needs Storage Queue Data Contributor + Message Processor (granted in
  [`infra/modules/rbac.bicep`](../../infra/modules/rbac.bicep)).

## Run locally

Requires Azure Functions Core Tools ≥ 4.0.7030 and a storage emulator (Azurite).

```bash
cp local.settings.json.example local.settings.json   # then fill in values
func start
```

Point an MCP client at `http://localhost:7071/runtime/webhooks/mcp` (transport:
Streamable HTTP). The pipeline API must be reachable at `SpeakerPipelineApi:BaseUrl`.

## Deploy

The Flex Consumption Function App is provisioned by
[`infra/modules/functions.bicep`](../../infra/modules/functions.bicep) (wired into
`infra/main.bicep`). Deploy code with your preferred Functions tooling (`azd`,
`func azure functionapp publish`, or CI).
