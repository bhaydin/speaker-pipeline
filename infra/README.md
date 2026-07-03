# Infrastructure (`infra/`)

Bicep for the speaker pipeline's Azure footprint, deployed to Brian's personal
tenant. Milestone 1 provisions the **core resources**; the Functions-hosted MCP
server adds its own module later.

## What Milestone 1 creates

| Resource | Module | Notes |
|---|---|---|
| Resource group | `main.bicep` | `rg-<prefix>-<env>` |
| User-assigned managed identity | `modules/identity.bicep` | Shared by API + MCP; holds all data-plane roles |
| Storage account (Tables + Blob) | `modules/storage.bicep` | 6 tables + `artifacts` container; managed-identity only |
| Key Vault (standard, RBAC) | `modules/keyvault.bicep` | Secrets added out-of-band per milestone |
| Log Analytics + App Insights | `modules/appinsights.bicep` | Workspace-based; OTel export target |
| Role assignments | `modules/rbac.bicep` | Table/Blob Data Contributor + Key Vault Secrets User |

The **Function App** (Flex Consumption) that hosts the MCP server ships as
`modules/functions.bicep` with the MCP server commit — it belongs with the thing
it hosts.

## Deploy

Requires the Azure CLI with the Bicep tooling and a signed-in session on the
target subscription.

```bash
az deployment sub create \
  --location centralus \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam
```

Override any parameter inline, e.g. `--parameters namePrefix=speakerpipe environment=dev`.

## Design notes

- **No secrets, no IDs in source.** `main.bicepparam` carries only names and a
  location. Subscription/tenant IDs come from the signed-in `az` context.
- **Managed identity, not keys.** `allowSharedKeyAccess` is `false` on the
  storage account; every consumer authenticates with the shared identity via
  `DefaultAzureCredential`. This matches the app's `Storage:TableEndpoint`
  configuration (endpoint URI only, no connection string).
- **Names are generated for global uniqueness.** Storage account and Key Vault
  names append `uniqueString(...)`; read the actual names from the deployment
  outputs (`storageAccountName`, `keyVaultName`, `tableEndpoint`, ...).
- **Table schema** lives in [../docs/architecture-table-storage.md](../docs/architecture-table-storage.md).
  The three net-new tables (`Topics`, `Blackouts`, `NotificationLog`) are
  provisioned here and modeled in `SpeakerPipeline.Core`.
