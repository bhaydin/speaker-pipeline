# Speaking pipeline — Table Storage schema

This is the persistence contract for the six business tables. Keep it aligned
with `SpeakerPipeline.Core` records and `SpeakerPipeline.Storage/Mapping.cs`.
The existing estate is the only production environment; historical `dev`
resource names remain in place.

## Ownership and provisioning

Only `SpeakerPipeline.Api` accesses pipeline tables. Agents, Telegram, MCP, and
operator tools use the API; they must not hold direct pipeline Table Storage
permissions. The API uses managed identity in Azure. Local Development uses
isolated loopback Azurite.

Reconcile the existing resources through [infra/README.md](../infra/README.md)
and its Bicep entrypoints, including `infra/data-tables.bicep`. Do not provision
a replacement account or assign Table Data Contributor to agent identities.
Follow the [production runbook](production-rollout.md) and
[deployment plan](../.azure/deployment-plan.md) for release and maintenance.

| Logical table | PartitionKey | RowKey | API record key |
|---|---|---|---|
| `Events` | `events` | Event slug | `Slug` |
| `Submissions` | Parent event slug | Submission ID | `EventSlug`, `SubmissionId` |
| `Talks` | `talks` | Canonical talk slug | `Slug` |
| `Topics` | `topic` | Topic ID | `TopicId` |
| `Blackouts` | `blackout` | Blackout ID | `BlackoutId` |
| `NotificationLog` | UTC month `yyyy-MM` | Notification ID | `Period`, `NotificationId` |

Physical table names are configured by the API and included in maintenance
snapshots. `PartitionKey` and `RowKey` are required strings on every entity.
Azure supplies `Timestamp` and `ETag`; ordinary Core records do not expose them.
Each domain record has an integer `SchemaVersion`, currently defaulting to `1`.
Optional properties may be absent in existing rows.

## Events

One row represents a speaking opportunity. Slugs are URL-safe keys such as the
fictional `northwoods-tech-summit-2027`. The normal partition is `events`.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `Name` | string | Yes | Display name |
| `EventType` | string | Yes | `Conference`, `CodeCamp`, `UserGroup`, `CommunityChapter`, `Workshop`, `Virtual` |
| `Category` | string | Yes | `Delivered`, `Accepted`, `SubmitNow`, `Submitted`, `Outreach`, `Monitor`, `Quarantine`, `Pass`, `Skip` |
| `Priority` | string | Yes | `Committed`, `High`, `MediumHigh`, `Medium`, `Low`, `NA` |
| `FocusFit` | string | No | Comma-separated `AgentOps`, `HybridAgents`, `M365Governance`, `PracticalEnterpriseAI` |
| `StatusDetail` | string | No | Status explanation |
| `EventDateStart` | DateTime | No | UTC start |
| `EventDateEnd` | DateTime | No | UTC end |
| `CfpDeadline` | DateTime | No | UTC submission deadline |
| `CfpUrl` | string | No | Submission page |
| `CfpStatus` | string | No | `Open`, `Closed`, `NotYetOpen`, `Unknown`, `RevalidateLive` |
| `EventUrl` | string | No | Canonical event page |
| `Location` | string | No | Place or virtual location |
| `Format` | string | No | `InPerson`, `Virtual`, `Hybrid` |
| `TravelBurden` | string | No | `None`, `Low`, `Medium`, `High` |
| `NextAction` | string | No | Suggested next step |
| `Notes` | string | No | Operator/source notes |
| `SourceSeenOn` | string | No | `Sessionize`, `PaperCall`, `CfpNinja`, `Meetup`, `GlobalAI`, `ConfsTech`, `Direct`, `Manual` |
| `LastVerifiedUtc` | DateTime | No | Last recorded live-source verification |
| `DoNotResurface` | bool | No | Explicit suppression; absent reads as false |
| `FamilyConflictFlag` | bool | No | Hard blackout overlap, recomputed by tracker; absent reads as false |
| `PrepConflictFlag` | bool | No | Preparation congestion, recomputed by tracker; absent reads as false |
| `DiscoveredByAgent` | string | No | Discovery provenance |
| `DecidedByAgent` | string | No | Scoring/decision provenance |
| `SchemaVersion` | int | No | Defaults to `1` |

`Quarantine` is retained for review and excluded from scoring until promoted.
`Delivered` and `Skip` are terminal/manual categories respected by tracker
reconciliation. Source enumeration values identify provenance; they do not
imply that a separate scanner for every source is deployed.

Fictional examples: [sample-events.json](../samples/sample-events.json).

## Submissions

The partition is the parent event slug, referencing `Events.RowKey`. A submission
ID convention is `{talk-slug}-{yyyy-MM-dd}`. New imports must resolve both the
event and talk references; Azure Table Storage does not enforce foreign keys.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `EventName` | string | Yes | Denormalized event name |
| `TalkSlug` | string | Yes | Reference to `Talks.RowKey` |
| `TalkTitleUsed` | string | Yes | Actual submitted title |
| `AbstractUsed` | string | Yes | Actual submitted abstract |
| `SubmittedOnUtc` | DateTime | Yes | UTC submission timestamp |
| `Status` | string | Yes | `Submitted`, `InReview`, `Accepted`, `Rejected`, `Withdrawn` |
| `DecisionReceivedUtc` | DateTime | No | UTC decision timestamp |
| `ContactPerson` | string | No | Organizer contact; private production data |
| `ContactEmail` | string | No | Organizer address; private production data |
| `FollowUpNeededBy` | DateTime | No | Recorded follow-up date; no OpenClaw follow-up runtime is deployed |
| `Notes` | string | No | Submission notes |
| `SchemaVersion` | int | No | Defaults to `1` |

Submissions represent recorded submissions, not Telegram `/approve` intent.
Tracker derives event categories from submission statuses. There is no
`Delivered` submission status and no automatic talk-delivery increment today.

Fictional examples: [sample-submissions.json](../samples/sample-submissions.json).
Use placeholders instead of real contact details in documentation.

## Talks

The normal partition is `talks`; the row key is the canonical talk slug. These
records are the speaker's reusable repertoire. All four existing canonical talks
are retained during production cleanup.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `CanonicalTitle` | string | Yes | Default title |
| `Lane` | string | Yes | `AgentOps`, `HybridAgents`, `M365Governance`, `PracticalEnterpriseAI` |
| `AbstractShort` | string | No | Short abstract |
| `AbstractLong` | string | No | Full abstract |
| `BioVariant` | string | No | Bio reference |
| `LengthMinutes` | int | No | Session duration |
| `Format` | string | No | `Talk`, `Workshop`, `Lightning`, `Panel` |
| `DeckUrl` | string | No | Actual deck URL; remove exact placeholder values during cleanup |
| `LastDeliveredUtc` | DateTime | No | Recorded last delivery; not automatically maintained today |
| `DeliveryCount` | int | No | Recorded count; absent reads as `0` |
| `ReusabilityScore` | int | No | Intended 1–5 input to scoring effort |
| `Retired` | bool | No | Retirement flag; absent reads as false |
| `SchemaVersion` | int | No | Defaults to `1` |

Examples: [sample-talks.json](../samples/sample-talks.json). Fixture deck URLs are
placeholders, not production configuration.

## Topics

The normal partition is `topic`; `RowKey` maps to `TopicId`. Topic capture is
available through the API and Telegram; MCP adapters also exist in source.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `Title` | string | Yes | Topic idea |
| `Stage` | string | Yes | `Idea`, `Validated`, `Built` |
| `Source` | string | Yes | `Claude`, `Copilot`, `Manual`, `Telegram`, `TechTrekkerPost` |
| `OneLiner` | string | No | Short premise |
| `Lane` | string | No | One of the four talk lanes |
| `EffortClass` | string | No | `NewTopic`, `DeckAdapt` |
| `Notes` | string | No | Topic notes |
| `RelatedContentUrls` | string | No | Newline-separated URLs; maps to an API list |
| `CreatedUtc` | DateTime | No | UTC creation time |
| `UpdatedUtc` | DateTime | No | UTC last update |
| `SchemaVersion` | int | No | Defaults to `1` |

## Blackouts

The normal partition is `blackout`; `RowKey` maps to `BlackoutId`. These records
are the portable availability source used by tracker conflict evaluation.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `StartDate` | DateTime | Yes | UTC interval start |
| `EndDate` | DateTime | Yes | UTC interval end, on/after start |
| `Reason` | string | Yes | Unavailability reason; private production data |
| `Hardness` | string | Yes | `Hard`, `Soft` |
| `Source` | string | No | Free-form provenance |
| `CreatedUtc` | DateTime | No | UTC creation time |
| `UpdatedUtc` | DateTime | No | UTC last update |
| `SchemaVersion` | int | No | Defaults to `1` |

No deployed calendar connector automatically populates this table. Preserve real
blackouts during cleanup, including rows that were entered before this rollout.

## NotificationLog

The partition is the UTC send month, `yyyy-MM`; `RowKey` maps to `NotificationId`.
This is delivery history and deduplication state, not disposable test output.

| Property | Stored type | Required | Meaning / values |
|---|---|---|---|
| `Channel` | string | Yes | `Telegram`, `Email`, `Teams`; Teams is a schema value, not a deployed lane |
| `Urgency` | string | Yes | `Urgent`, `Digest` |
| `SentUtc` | DateTime | Yes | UTC successful-delivery timestamp |
| `DedupeKey` | string | Yes | Trigger key; empty when not deduplicated |
| `EntityRef` | string | No | Related event/topic slug |
| `Summary` | string | No | Notification summary |
| `SchemaVersion` | int | No | Defaults to `1` |

Notifier reads the current month's keys before sending and records each
successful lane. This is not a transactional exactly-once delivery guarantee:
delivery/logging failures or concurrent runs require operational review. Keep
real notification records to avoid replaying alerts after cleanup.

## Access, updates, and backup

- Ordinary consumers use the typed API and `ISpeakerPipelineApiClient`; only
  repositories in `SpeakerPipeline.Storage` use `Azure.Data.Tables`.
- Normal writes are keyed upserts using merge semantics. Null/omitted properties
  do not reliably remove stored columns. Explicit placeholder removal uses the
  maintenance API's conditional replacement while preserving all other columns.
- Keys cannot contain `/`, `\`, `#`, `?`, or control characters. Store dates in
  UTC; Core uses `DateTimeOffset`, and Storage maps dates to UTC.
- There are no joins or cross-table transactions. The API assembles context;
  callers must preserve references and handle partial writes. A normal event
  delete does not imply cascading deletion of submissions or notifications.
- Non-key queries scan their partition. `SchemaVersion` supports explicit
  migrations; adding a property does not backfill old rows automatically.
- Domain projections omit unknown properties and queries may exclude unexpected
  partitions. Maintenance export therefore scans every partition of all six
  tables and preserves raw typed properties, ETags, and timestamps, including
  orphan submissions and future columns.
- Private Blob artifacts hold cleanup snapshots/journals; they are operational
  artifacts, not a seventh business table. They must not enter Git or public logs.
- Cleanup checks the target, complete snapshot, expected ETags, and operation
  journal. Restore recovers affected properties/rows with conflict checks; new
  storage ETags/timestamps are expected. Full backup rehearsal uses an isolated
  loopback destination and compares logical typed content.

Use [SpeakerPipeline.Migrate](../tools/SpeakerPipeline.Migrate/README.md) for
validated private imports and maintenance. Fictional fixtures stay in the repo
for local tests; deployed cleanup does not remove them from source control.
