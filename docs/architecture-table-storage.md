# Speaking Pipeline — Azure Table Storage Schema

**Purpose:** Persistence layer for Brian's speaking pipeline. Replaces the CSV tracker. Designed to back a Microsoft Agent Framework multi-agent system with clean read/write contracts per agent.

**Storage account profile:** Standard general-purpose v2, LRS, Hot tier. Cool/Archive not needed at this volume.

---

## 1. Provisioning (Azure CLI)

```bash
# Variables
RG="rg-pipeline-prod"
LOC="centralus"
SA="sapipelinebh$(date +%s | tail -c 5)"   # storage account names must be globally unique

# Resource group
az group create --name $RG --location $LOC

# Storage account
az storage account create \
  --name $SA \
  --resource-group $RG \
  --location $LOC \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false \
  --min-tls-version TLS1_2

# Tables
az storage table create --name Events       --account-name $SA --auth-mode login
az storage table create --name Submissions  --account-name $SA --auth-mode login
az storage table create --name Talks        --account-name $SA --auth-mode login
```

**Auth:** Use Managed Identity for the MAF agents in Foundry Agent Service. Assign the agents the `Storage Table Data Contributor` role on the storage account. No connection strings in code.

```bash
# Grant role to the MAF managed identity (replace OBJECT_ID)
az role assignment create \
  --assignee <managed-identity-object-id> \
  --role "Storage Table Data Contributor" \
  --scope $(az storage account show --name $SA --resource-group $RG --query id -o tsv)
```

---

## 2. Table: `Events`

**PartitionKey strategy:** Single partition (`"events"`). At your volume (<200 lifetime rows), this is the simplest choice and supports batch operations across the whole table. If you ever exceed ~10k rows, repartition by year.

**RowKey strategy:** URL-safe slug — `lowercased-name-with-year`. Example: `data-driven-wi-2026`, `wisconsin-net-ug-2026-07-14`. **Avoid** `/`, `\`, `#`, `?` (forbidden in Table Storage keys).

| Property | Type | Required | Notes / values |
|---|---|---|---|
| `PartitionKey` | string | ✅ | Always `"events"` |
| `RowKey` | string | ✅ | Slug |
| `Name` | string | ✅ | Display name |
| `EventType` | string | ✅ | `Conference` / `CodeCamp` / `UserGroup` / `CommunityChapter` / `Workshop` / `Virtual` |
| `Category` | string | ✅ | `Delivered` / `Accepted` / `SubmitNow` / `Submitted` / `Outreach` / `Monitor` / `Pass` / `Skip` |
| `Priority` | string | ✅ | `Committed` / `High` / `MediumHigh` / `Medium` / `Low` / `NA` |
| `FocusFit` | string |  | Comma-separated lane tags: `AgentOps`, `HybridAgents`, `M365Governance`, `PracticalEnterpriseAI`, `DataFabric` |
| `StatusDetail` | string |  | Free text status nuance |
| `EventDateStart` | DateTime |  | UTC. Null for ongoing user groups. |
| `EventDateEnd` | DateTime |  | UTC. Null for single-day. |
| `CfpDeadline` | DateTime |  | UTC. The thing the scoring agent sorts urgency on. |
| `CfpUrl` | string |  | Direct submission link |
| `CfpStatus` | string |  | `Open` / `Closed` / `NotYetOpen` / `Unknown` / `RevalidateLive` — set by the discovery agent from the fetched page; `RevalidateLive` when sources conflict |
| `EventUrl` | string |  | Canonical event page |
| `Location` | string |  | City, State or `Virtual` |
| `Format` | string |  | `InPerson` / `Virtual` / `Hybrid` |
| `TravelBurden` | string |  | `None` / `Low` / `Medium` / `High` |
| `NextAction` | string |  | Verb-first imperative — what gets done next |
| `Notes` | string |  | Free text |
| `SourceSeenOn` | string |  | `Sessionize` / `PaperCall` / `CfpNinja` / `Meetup` / `GlobalAI` / `Direct` / `Manual` |
| `LastVerifiedUtc` | DateTime |  | Set by verification agent on each live-page check |
| `DoNotResurface` | bool |  | `true` for the explicit skip list |
| `DiscoveredByAgent` | string |  | Agent name (e.g. `sessionize-scanner`) for provenance |
| `DecidedByAgent` | string |  | Agent name (e.g. `scoring-agent-v1`) |
| `SchemaVersion` | int |  | `1` initially. Lets you migrate later without rewriting all rows. |

**Sample entity (JSON):**

```json
{
  "PartitionKey": "events",
  "RowKey": "data-driven-wi-2026",
  "Name": "Data-Driven Wisconsin 2026",
  "EventType": "Conference",
  "Category": "Accepted",
  "Priority": "Committed",
  "FocusFit": "PracticalEnterpriseAI,DataFabric",
  "EventDateStart": "2026-08-12T00:00:00Z",
  "EventDateEnd": "2026-08-13T00:00:00Z",
  "EventUrl": "https://datadrivenwi.org/",
  "Location": "Milwaukee, WI (MSOE)",
  "Format": "InPerson",
  "TravelBurden": "None",
  "NextAction": "Confirm session title and abstract",
  "Notes": "2026 theme: Augmented Intelligence — Empowering People",
  "SourceSeenOn": "Direct",
  "LastVerifiedUtc": "2026-05-24T00:00:00Z",
  "DoNotResurface": false,
  "SchemaVersion": 1
}
```

---

## 3. Table: `Submissions`

**PartitionKey strategy:** Event slug. All submissions for one event group together — natural batch unit (e.g. "show me all 4 PPCC submissions and their statuses").

**RowKey strategy:** `{talk-slug}-{yyyy-MM-dd}` — talk + submission date. Allows the same talk to be submitted to the same event in different years.

| Property | Type | Required | Notes / values |
|---|---|---|---|
| `PartitionKey` | string | ✅ | Event slug (must match an `Events.RowKey`) |
| `RowKey` | string | ✅ | `{talk-slug}-{yyyy-MM-dd}` |
| `EventName` | string | ✅ | Denormalized for readability |
| `TalkSlug` | string | ✅ | Reference to `Talks.RowKey` |
| `TalkTitleUsed` | string | ✅ | Actual title submitted (may differ from canonical for venue tailoring) |
| `AbstractUsed` | string | ✅ | Actual abstract submitted |
| `SubmittedOnUtc` | DateTime | ✅ | When you hit Submit |
| `Status` | string | ✅ | `Submitted` / `InReview` / `Accepted` / `Rejected` / `Withdrawn` |
| `DecisionReceivedUtc` | DateTime |  | Null until decision returns |
| `ContactPerson` | string |  | Organizer name for follow-up |
| `ContactEmail` | string |  | |
| `FollowUpNeededBy` | DateTime |  | For nagging logic in OpenClaw |
| `Notes` | string |  | |
| `SchemaVersion` | int |  | `1` |

**Sample (one of the four PPCC submissions):**

```json
{
  "PartitionKey": "ppcc-2026",
  "RowKey": "hybrid-agents-unleashed-2026-01-28",
  "EventName": "Power Platform Community Conference 2026",
  "TalkSlug": "hybrid-agents-unleashed",
  "TalkTitleUsed": "Hybrid Agents Unleashed: Copilot Studio Meets Pro-Code",
  "AbstractUsed": "...",
  "SubmittedOnUtc": "2026-01-28T15:30:00Z",
  "Status": "InReview",
  "ContactPerson": "Lyman (PPCC content team)",
  "ContactEmail": "Lyman@powerplatformconf.com",
  "FollowUpNeededBy": "2026-06-01T00:00:00Z",
  "SchemaVersion": 1
}
```

---

## 4. Table: `Talks`

**PartitionKey strategy:** Single partition (`"talks"`). You'll have ~10–20 canonical talks over a career, not thousands.

**RowKey strategy:** Slug. Example: `agentops-real-world`, `hybrid-agents-sweet-spot`, `m365-request-to-retirement`.

| Property | Type | Required | Notes / values |
|---|---|---|---|
| `PartitionKey` | string | ✅ | Always `"talks"` |
| `RowKey` | string | ✅ | Slug |
| `CanonicalTitle` | string | ✅ | Default title (often gets tweaked per venue) |
| `Lane` | string | ✅ | `AgentOps` / `HybridAgents` / `M365Governance` / `PracticalEnterpriseAI` |
| `AbstractShort` | string |  | ≤500 chars, for CFPs that want it tight |
| `AbstractLong` | string |  | Full version |
| `BioVariant` | string |  | Which bio version pairs best (link to your bio library if you build one) |
| `LengthMinutes` | int |  | Typical: 45 / 60 / 180 (workshop) |
| `Format` | string |  | `Talk` / `Workshop` / `Lightning` / `Panel` |
| `DeckUrl` | string |  | Link to canonical deck (SharePoint / OneDrive) |
| `LastDeliveredUtc` | DateTime |  | Updated by maintenance agent when Submission status flips to delivered |
| `DeliveryCount` | int |  | Increment on each delivery |
| `ReusabilityScore` | int |  | 1–5. Drives the scoring agent's "effort" axis. |
| `Retired` | bool |  | True when a talk shouldn't be recommended anymore |
| `SchemaVersion` | int |  | `1` |

---

## 5. Read/write patterns (C# — `Azure.Data.Tables`)

```csharp
using Azure.Data.Tables;
using Azure.Identity;

var serviceClient = new TableServiceClient(
    new Uri($"https://{storageAccount}.table.core.windows.net"),
    new DefaultAzureCredential());

var events = serviceClient.GetTableClient("Events");

// Scoring agent: pull all "SubmitNow" or "Monitor" candidates with deadlines in next 60 days
var cutoff = DateTime.UtcNow.AddDays(60);
var query = events.QueryAsync<TableEntity>(filter:
    $"(Category eq 'SubmitNow' or Category eq 'Monitor') and CfpDeadline lt datetime'{cutoff:o}'");

await foreach (var entity in query)
{
    // hand off to scoring logic
}

// Maintenance agent: upsert after live verification
var updated = new TableEntity("events", "data-driven-wi-2026")
{
    ["LastVerifiedUtc"] = DateTime.UtcNow,
    ["StatusDetail"] = "CFP confirmed open via official form"
};
await events.UpdateEntityAsync(updated, ETag.All, TableUpdateMode.Merge);
```

For Python (if any agents land there): `azure-data-tables` with `DefaultAzureCredential`. Same shape.

---

## 6. Gotchas worth knowing up front

1. **Forbidden chars in keys:** `/`, `\`, `#`, `?`, control chars. Slug-sanitize before writing.
2. **Datetime must be UTC.** Table Storage stores DateTimeOffset but expects ISO 8601 with `Z`. Don't push local Wisconsin time.
3. **No server-side joins.** If the scoring agent needs Events + Talks + Submissions context, it makes three queries and joins in code. That's fine at this scale.
4. **Batch operations require same PartitionKey.** Updating multiple events at once → fine (all in `"events"`). Updating events across partitions → multiple round trips.
5. **No secondary indexes.** Filtering on non-key properties (Category, CfpDeadline) does a partition scan. At <200 rows, this is fine. If you ever scale, project hot-query views into separate tables.
6. **Schema evolution:** Table Storage is schemaless per row. Adding a property later means new rows have it, old rows don't. The `SchemaVersion` field lets the maintenance agent know whether to backfill.
7. **No transactions across tables.** Updating an Event's status and inserting a Submission row in one atomic op → can't. Make the agent idempotent so retry-on-failure is safe.

---

## 7. Cost expectation

At your volume, this is rounding error:

- Storage: ≤1 MB of data = <$0.01/month
- Transactions: ~thousands/month from agents = <$0.05/month
- Egress: nil (all in-region)

**Realistic total: well under $1/month** even with aggressive agent activity.

---

## 8. Migration from CSV (one-time)

A short PowerShell or C# script that reads `01_conference_tracker.csv`, maps each row to an `Events` entity, and bulk-inserts. The new categories (`Delivered`, `SubmitNow`) map 1:1. Submissions table starts empty and gets populated as you log past submissions (PPCC's 4, Merge's outstanding ones). Talks table seeded with the four lanes' canonical entries.

This is a one-evening task. Want me to write the seeding script?
