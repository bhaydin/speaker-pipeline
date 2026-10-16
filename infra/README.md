# Production infrastructure

This repository operates **one production environment**. Existing Azure names
ending in `dev` are retained to preserve resource IDs, URLs and data. They do not
select a development deployment. Local development uses isolated emulators and
test fixtures.

## Existing estate

| Component | Resource | Management |
|---|---|---|
| Compute/data resource group | `rg-speakerpipeline-dev` | Existing deployment target |
| API / B1 plan | `app-speakerpipeline-api-dev` / `asp-speakerpipeline-dev` | Reconciled in place |
| Agents / Flex plan | `func-speakerpipeline-dev` / `ASP-rgspeakerpipelinedev-902b` | Reconciled in place |
| Pipeline data | `stspeakerpipelinedev` | Existing; six tables and private `artifacts` container |
| Functions host/deployment storage | `stspeakerpipelinefuncdev` | Existing; never a cleanup target |
| Shared services group | `rg-speakerpipeline-shared` | Existing |
| API / agents managed identities | `mi-api-dev` / `mi-functions-dev` | Existing; separate access boundaries |
| Key Vault / telemetry | `kv-speakerpipeline` / `appi-speakerpipeline` / `log-speakerpipeline` | Existing shared services |
| Foundry | `foundry-speakerpipeline-dev` | Existing model deployments retained |

The old greenfield templates and direct-storage provisioning path have been
retired. There is no deployed MCP server in this estate; the MCP project remains
available for local use. Unused `mi-api-prod` and `mi-functions-prod` identities
are retirement candidates only after their assignments and dependencies have
been inventoried.

## Preview and reconcile

`main.bicep` is **resource-group scoped**, unlike the retired subscription-scoped
template. Use the already-selected production subscription, or pass an explicit
subscription from private operator configuration. Always inspect what-if first:

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az deployment group what-if \
  --resource-group rg-speakerpipeline-dev \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam
```

Accept only in-place compute configuration/tag changes, existing table
declarations, the private artifacts container and its API-only Blob Data
Contributor assignment, and operational alerts. Stop for resource replacement,
new compute/storage/Foundry resources, deletions, or a change to the host-storage
deployment container. Apply the reviewed template incrementally during the
rollout window; never use complete mode.

The templates **omit app settings** to avoid replacing authentication, runtime
credentials, maintenance switches or notification controls. The release workflow
merges application settings and explicitly pins `ASPNETCORE_ENVIRONMENT`,
`DOTNET_ENVIRONMENT` and `AZURE_FUNCTIONS_ENVIRONMENT` to `Production` on their
respective hosts. Operator-owned Key Vault references and identity settings must
already be configured and are checked through authenticated readiness probes.

Only the API identity may access pipeline Tables or backup artifacts. Existing
data roles are validated rather than recreated under new role-assignment IDs.
The agents identity keeps its separate host-storage roles, Foundry access, Key
Vault access and `Pipeline.Access` application role on the API.

## Operational monitoring

The alert module defines API/Functions failures and missing successful scheduled
invocations. Expected schedules are Discovery daily 05:00 UTC, Tracker daily
05:30 UTC and Scoring Monday 06:00 UTC, each with a two-hour grace period.
Queries use workspace `AppRequests`, including the Functions host role and the
worker's OpenTelemetry role. Request sampling is excluded in `host.json`.

Alerts default disabled during cleanup. Supply an existing
`alertActionGroupResourceId` privately and enable `alertsEnabled` after validating
the role names, successful invocation telemetry and recipient delivery. An empty
action group keeps rules disabled. Disable missed-run rules during planned
maintenance and re-enable them on resume. Weekly scoring absence is checked on
Monday after 08:00 UTC; its resolved state on Tuesday does not imply a recovered
run, so inspect the incident and invocation history.

See [the production rollout runbook](../docs/production-rollout.md) for release,
maintenance, rollback and GitHub setup. API data schemas remain in the
[storage specification](../docs/architecture-table-storage.md).
