# Talk catalog and scoring

The catalog reads the private talks repository and public presentation artifacts.
It preserves one identity per reusable talk, its substantive versions, and its
separate engagements. It never writes to either source repository. Existing
tracker talk slugs and submission references remain unchanged.

## Configuration and rollout

The API owns the `Catalog` Azure Table (`Storage:CatalogTableName` overrides the
name). Agents and Functions use `ICatalogApiClient`; they have no Table access.
Provision the table before enabling catalog endpoints for consumers. Include it
in maintenance exports and recovery snapshots alongside the opportunity tables.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Catalog:Enabled` | `false` | Enables the GitHub webhook receiver and sync worker. |
| `ScoringAgent:CatalogEnabled` | `false` | Requires a published catalog for scoring. Empty catalog produces a run issue instead of unsupported decisions. |
| `Storage:OpportunitiesEnabled` | `false` | API authority switch, after opportunity migration and validation. |
| `Pipeline:OpportunitiesEnabled` | `false` | Shared consumer switch; scoring posts revision/ETag decisions and retrieves feedback through the opportunity API. Enable after the API authority switch. |
| `Catalog:ExtractionModel` | empty | Foundry deployment for missing metadata extraction; empty disables model extraction. |
| `Catalog:MaxExtractionCallsPerSync` | `5` | Maximum new extraction requests per sync. |
| `Catalog:MaxExtractionInputCharacters` | `12000` | Maximum source characters per extraction request. |
| `ScoringAgent:PrimaryTravelOrigin` | `Milwaukee, WI` | Primary travel origin. |
| `ScoringAgent:SecondaryTravelOrigin` | `Chicago, IL` | Secondary travel origin. |
| `ScoringAgent:ModelRevision` | empty | Set/change this when the underlying model behind a deployment name changes, so affected decisions are rescored. |

In environment variables, replace each colon with `__`. Enable flags are
operator-owned settings and should not be overwritten by routine code releases.
The `UseOpportunityState` scoring property is bound from the shared
`Pipeline:OpportunitiesEnabled` switch; do not configure it independently.

Enable sync first, inspect the resulting catalog and review queue, then enable
catalog-aware scoring. Opportunity authority and consumer cutover remain a
separate staged migration. Rolling back catalog-aware scoring requires only
disabling its scoring flag; it does not delete imported history or legacy talks.

## GitHub connection

Create a personal GitHub App installed on **only** `bhaydin/bhaydin-talks` and
`bhaydin/Presentations`. Grant repository Contents **read-only** and Metadata
read-only, subscribe to push events, and grant no repository write permissions.
Configure `Catalog:GitHub:AppId`, `InstallationId`, and `PrivateKeyPem` on the
Functions host. Store the private key in the existing hosting environment's Key
Vault and use an app-setting Key Vault reference; the host's managed identity needs only
secret retrieval for that secret. Catalog synchronization always uses the
repository-scoped App installation token; local tests use synthetic sources.

The source client obtains short-lived installation tokens restricted to these
repositories and read permissions. Repository operations are commit/tree/blob
GET requests. The token exchange is the only GitHub POST; it does not modify
source content. Private source text, authorization headers, and key material
are not logged.

Point the App webhook at the Functions route `/api/catalog/github`. Store its
random webhook secret in Key Vault and configure the API host's
`Catalog:GitHub:WebhookSecret`. The Function forwards the raw payload and
signature through the authenticated pipeline API; the API verifies
`X-Hub-Signature-256` with a constant-time comparison before enqueueing work.
Only pushes to the repository's signed default branch in the two allowlisted repositories enqueue a
sync. Delivery IDs deduplicate redeliveries. No source repository edits or
workflow additions are needed.

`CatalogSyncWorker` polls the durable API-owned inbox every two minutes.
`CatalogNightlySync` checks every fifteen minutes and enqueues a fallback
reconciliation during the **03:00 America/Chicago** hour. Its local-date delivery
ID deduplicates the checks; the schedule follows daylight-saving changes.
Failed jobs retry with a five-minute delay, up to three attempts. Worker leases
expire after fifteen minutes. A completed sync reads both current repository
heads, rather than assuming a push payload lists every changed file.

Only claimed sync jobs create operational run records; idle worker polls do
not. Health includes the last catalog outcome, source failures, extraction
calls/cache hits, and actual and reserved input/completion usage. Missing
provider usage stays explicitly incomplete; cost is unknown unless complete
usage and configured deployment pricing are both available.

## Sources, versions, and review

Ingestion parses `talks/<id>/talk.yaml` and engagement YAML first. Explicit
Markdown Abstract sections and engagement-specific session abstracts supply
substantive versions; model extraction fills missing descriptions only when
there is sufficient source text. Existing explicit descriptions are not
replaced by model output. Stubs remain metadata-only; no abstract, delivery,
date, or city is invented. Draft talks remain eligible with preparation effort;
retired talks are excluded.

Versions use a title/abstract content hash. A timestamp-only metadata change
does not make identical content a newer version. Substantive abstracts are
preferred to generic descriptions, then ordered by their effective dates.
Older versions and prior titles remain available. Current scoring retrieves
at most five eligible talks by deterministic topic/title/abstract overlap and
provides their current versions and relevant delivery history. There is no
embedding service or separate vector index.

Engagement IDs, `previous_ids`, date precision, planned/completed state, and
source provenance are retained. Passing an event date never marks a planned
engagement completed. Public PDFs are inventoried as artifact links; existing
explicit source links associate them with engagements. Unlinked public
artifacts and uncertain identities go to review, without inventing or
double-counting deliveries.

The dashboard uses `POST /v1/catalog/review` with `ExpectedGenerationId`.
Supported reviews map an existing legacy talk slug to a catalog talk, change
talk eligibility, correct a delivery's date/precision/city/status/event-series
identity, or mark one delivery as an alias of another. Unknown legacy slugs
are rejected. Retired talks stay excluded even if an eligibility override is
set. Reviews are stored in pipeline state, survive source syncs, and never
change the source repositories.

Underlying `SourceDeliveries` remain available after an alias review. Setting
that delivery's `AliasOfDeliveryId` back to null restores it; alias chains and
cycles are rejected. Selecting unknown date precision clears its displayed
date rather than presenting a placeholder as an exact date. A concurrent
review or sync returns HTTP 412; refresh and review the current generation
before retrying.

## Reliability, calibration, and budgets

Sync stages immutable generation chunks, then conditionally changes a single
published pointer. Readers never see a partially imported generation. Fetch
failures leave the previous generation active. Invalid existing talk metadata
retains the last valid talk with a review flag; removed source talks remain in
history but are excluded from scoring. A malformed initial catalog is not
published. Explicitly reviewed aliases and corrections carry forward.

Extraction results are cached by source content hash. YAML is parsed before
eligibility is decided, so folded/literal descriptions remain authoritative.
Existing descriptions and explicit Abstract sections do not trigger model
extraction. Cached topics and audience level fill only missing fields; source
metadata always takes precedence. Only already-fetched allowlisted content
is considered; unsupported source paths are not fetched speculatively.

Work beyond the per-sync extraction cap resumes on subsequent scheduled syncs,
even when repository heads are unchanged. Completed hashes make no further
model calls, and a normalized content comparison prevents no-op generations.
Empty or malformed extraction results are cached as review-needed outcomes;
they do not cause nightly retries on unchanged source content. Correcting the
source metadata or changing its content allows the catalog to advance. Health
records pending extraction counts. Each extraction is bounded to
1,024 total completion tokens, including reasoning. The default five calls
therefore permit at most 5,120 completion tokens per changed-source sync.

Scoring defaults and maximum configured ceilings are:

| Limit | Value |
| --- | --- |
| Candidates per run | 20 |
| Catalog talks per candidate | 5 |
| Feedback examples per candidate | 8 |
| Input budget per run | 120,000 tokens |
| Total completion budget per run, including reasoning | 81,920 tokens |
| Completion limit per request | 4,096 tokens |

Before each request, the run reserves a conservative UTF-8 input-byte bound
plus message framing and the full completion limit. Actual provider usage
replaces reservations when available; failed calls or missing usage retain
their reservations. A request that cannot fit the remaining budget is not
sent. Context is reduced by removing older feedback and lower-ranked talks
before rejecting an oversized prompt. Run health records both reported usage
and conservative budget consumption. Completion usage already includes
reasoning, so reasoning tokens are not added again.

With opportunity authority enabled, scoring computes a substantive input
fingerprint before applying the 20-candidate cap. Unchanged decisions are
reused without model calls. Never-scored opportunities go first, followed by
the oldest changed decisions; the next run advances through candidates left
by a cap or token budget. Catalog generation IDs, source commit timestamps,
run timestamps, and the scorer's previous verdict do not invalidate this
cache. Relevant event facts, CFP urgency bands, windowed calendar content,
selected talk/version evidence, relevant feedback, model revision, or rubric
changes do. CFP urgency changes at 60 days, 14 days, 48 hours, and closure.
The legacy authority path retains its prior behavior until consumer cutover.

Rationale is the primary display; numeric scores remain in decision detail.
Fit distinguishes unrelated, partial, direct, and exceptional topic/audience
matches. **Higher effort means more work**, from unchanged deck reuse (1–2)
to a mostly new talk/workshop (9–10). Strong matches remain eligible worldwide;
unknown airfare and lodging costs are explicitly unknown. Current-version
catalog IDs are validated before a recommendation is persisted. Unsupported
positive recommendations become Monitor. Repeating a completed talk at the
same event series requires an explicit opportunity repeat override.

Feedback is stored against the original event and decision. The scorer selects
up to eight relevant examples and records their IDs in its decision. It does
not fill unused slots with unrelated recent feedback.
Examples do not rewrite the rubric, become global geography bans, or execute
instructions embedded in free text. Decisions include the input revision,
catalog generation, matched versions, rubric/model versions, event/calendar/
catalog/feedback context hashes, and token usage. Verdict flips retain a
separate bounded explanation grounded in the prior decision and current
evidence. The prior decision is comparison context, not a cache invalidator. ETag
and input-revision conflicts prevent stale decisions overwriting new facts or
feedback.

## Validation

On October 16, 2026, a five-request **real Foundry gpt-5-mini** evaluation of
the final v5 calibration rubric used
synthetic fixtures and the explicitly selected deployment tenant. It never
posted decisions to the tracker. Results:

| Synthetic case | Fit | Effort |
| --- | --- | --- |
| Exact topic/audience; reuse an existing deck | 10 | 2 |
| Shared AI topic but wrong audience | 5 | 6 |
| Unrelated topic | 1 | 1 |
| Strong fit requiring a new full-day workshop | 9 | 8 |
| Strong international match; existing deck | 10 | 2 |

All acceptance assertions passed. Usage was **7,767 input tokens and 2,483
completion tokens**, including **1,024 reasoning tokens**. These five fixtures
demonstrate useful separation; they are not a broad quality guarantee.
Cache coverage, urgency transitions, verdict-change explanations, and the
deterministic pre-post CFP/calendar guard also have focused tests.

Ordinary CI skips billed provider evaluation. To rerun explicitly, follow the
scoring evaluation project's README. Its report is a build artifact containing
the model/rubric version and actual usage. Deterministic tests additionally
cover source precedence, stubs, date uncertainty, alias review/undo, retired
eligibility, source deletion, capped extraction continuation, negative-cache
behavior, folded YAML precedence, repeat blocking/override, hallucinated match
rejection, feedback references, and budget reservation. API tests cover signed
webhooks, tampering, deduplication, authentication, and fail-closed missing
secrets. A local Azurite integration test verifies chunked generations,
conditional publication, orphan invisibility, inbox deduplication, and leases.
