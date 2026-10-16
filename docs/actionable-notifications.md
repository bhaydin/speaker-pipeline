# Actionable notifications and operational health

The Functions host uses the API for operational records, delivery receipts, callback references, and opportunity changes. It never opens pipeline Tables directly. `PipelineRuns` is the shared execution path for scheduled and manual discovery, tracker, and scoring runs.

## Runtime behavior

- Healthy discovery and tracker runs with no changes send no digest, including manual HTTP runs. The HTTP response still reports the result. `Notifications:SuppressEmptyScheduledRuns` is retained only for configuration compatibility and is ignored.
- Source failures, unexpected empty configured sources, all-zero discovery candidates, scoring failures, and run failures produce an incident notification. The incident identity uses agent, status, and stable source/error codes. An unchanged incident is suppressed, and a subsequent healthy run reports recovery. A missing future-year conference feed is expected; a failed current feed is not.
- Scoring sends the full current actionable snapshot: SubmitNow, then Outreach, each sorted by CFP deadline with unknown deadlines last. Monitor is a count. Names link only to HTTP(S) CFP/event URLs. Items include CFP close date and days remaining, a flag within 14 days, event dates, location/virtual status, matched talks, and rationale. A changed verdict appears inline once for that scoring run. A snapshot has a Chicago ISO-week and content identity, so the same snapshot can be sent again in a later week.
- Event dates retain their stored calendar date. Operational timestamps, the week boundary, and quiet hours use `America/Chicago`.
- Telegram sends during 22:00–07:00 Chicago with `disable_notification=true`; delivery is immediate and silent, including deadline reminders and command replies. It is not a deferred queue.
- `WeeklyHealthTimerTrigger` wakes every 15 minutes and sends during Monday 08:00–08:59 Chicago, deduplicated for the week. This follows daylight-saving changes without changing the UTC timer. `/status` also includes health.
- Health shows the latest discovery, tracker, scoring, and catalog outcomes; stale/missing runs; seven-day funnel, dropped-reason, source-coverage, search and token totals; recurring incident counts by run and occurrence; actual versus reserved model usage; and queued/incomplete/uncertain deliveries. Completion counts already include reasoning tokens. Estimated cost remains null/unknown unless the run's exact deployment has both explicit configured prices and complete actual token usage.

## Opportunity actions

`Pipeline:OpportunitiesEnabled=true` is required for current opportunity reads and inline actions. Leave it false until the API opportunity authority and migration have been validated.

Each actionable Telegram item has a numbered row with Submitted, Snooze 7d, and Drop. Drop first opens a reason picker: Wrong audience, Too far, Bad timing, Already spoke there, Not my topic, or Other. Cancel returns to the action rows. Submitted records the user's placement; it does not invent a talk submission record.

Snooze is a notification overlay. Scoring continues and the snooze expiry is computed once by the API when the idempotent action is accepted. Snoozed actionable items are omitted from routine snapshots. A still-actionable, not-dropped opportunity gets one rescue reminder when its CFP is in the next 48 hours. The rescue receipt is stable for the event/deadline date across repeated runs and month boundaries. Submitted, dropped, do-not-resurface, closed, and nonactionable opportunities do not qualify.

Callbacks carry only an opaque reference and action code. The API stores the reference binding to the opportunity, displayed ETag/decision ID, chat, original message, and 30-day expiry. The webhook requires `X-Telegram-Bot-Api-Secret-Token`; callbacks additionally require the configured private chat and sender user. Actions use that original revision and a stable idempotency key. A stale button cannot overwrite a later dashboard placement or target an unseen decision; it edits the item to request a refresh. A message-level optimistic lease serializes edits. Accepted actions survive a failed Telegram edit; replay repairs the original message without applying the action again. Resolved rows disappear and the item's original text shows its result.

Messages split at group boundaries, then whole item boundaries. Oversized details and names continue across messages without truncation, preserving escaped HTML and HTTP(S) links within Telegram's 4096-character limit. The action row belongs to the final continuation of an item.

## Durable delivery

`Operations` stores document kinds `runs`, `incidents`, `deliveries`, `messages`, `callbacks`, `outbox`, and `outboxchunks`. ETag compare-and-swap protects updates. The API validates the allowed kind, safe identifier, and bounded JSON payload. All operations routes require `Pipeline.Access`.

Delivery claims and receipts are per notification identity, channel, and part. A known successful Email part does not suppress an unsent Telegram part, and successful Telegram parts are not resent after another part fails. Original parts enter the durable outbox before any external send. `NotificationRetryTimerTrigger` runs every five minutes, claims at most 20 due parts, and retries confirmed failures under their original identities. Each part is bounded to 256 chunks of 8,000 serialized characters. Retries use backoff, at most five attempts and a seven-day eligibility window; disabled channels remain paused. Unchanged incidents replay their original transition identity through the same receipts. Timeouts, canceled sends, unreadable successful Telegram responses, and abandoned send claims are uncertain; the outbox freezes them instead of resending. `/status` and weekly health expose queued and review-required deliveries.

Receipt writes occur after provider acceptance. If that write fails, the existing Sending claim intentionally prevents an automatic duplicate. If Telegram accepted a message and its durable success receipt exists but the callback binding failed, the outbox repairs that binding from the known message ID without resending. A missing success receipt stays uncertain and requires review.

## Settings and deployment order

Use double underscores instead of colons for Azure application-setting names.

| Setting | Owner | Meaning |
| --- | --- | --- |
| `Storage:OperationsTableName` | API | Defaults to `Operations`; table must exist before either new host is released. |
| `Storage:OperationsProvisioned` | API | Set true after provisioning so maintenance snapshots include this table. This is not a runtime health/receipt kill switch. |
| `Storage:OpportunitiesEnabled` | API | Authoritative opportunity storage gate; enable only after migration and validation. |
| `Pipeline:OpportunitiesEnabled` | Functions | Defaults false; enable only after API authority is active. Controls current-state reads and Telegram callback actions. |
| `SpeakerPipelineApi:BaseUrl` | Functions | HTTPS API endpoint; operational records use this existing authenticated client boundary. |
| `SpeakerPipelineApi:Scope` | Functions | API Entra scope used by the Functions managed identity. |
| `Telegram:Enabled` | Functions | Outbound Telegram and webhook kill switch. |
| `Telegram:BotToken` | Functions | Key Vault reference; never a checked-in value. |
| `Telegram:ChatId` | Functions | Authorized private chat and outbound destination. |
| `Telegram:UserId` | Functions | Authorized callback sender. Zero falls back to ChatId for the private-DM case; configure it explicitly when known. |
| `Telegram:WebhookSecret` | Functions | Key Vault reference matching the secret registered with Telegram. |
| `Notifications:Email:Enabled` | Functions | Email channel switch; its existing tenant/client/secret/sender/recipient configuration still applies. |
| `Notifications:SuppressEmptyScheduledRuns` | Functions | Compatibility-only setting, ignored by the new no-op policy. |
| `Notifications:ModelPricing:<deployment>:InputUsdPerMillionTokens` | Functions | Optional operator-supplied USD input price for the exact configured model deployment. No default price. |
| `Notifications:ModelPricing:<deployment>:CompletionUsdPerMillionTokens` | Functions | Optional operator-supplied USD completion price. Both rates and complete actual usage are required for a cost estimate. |

1. Provision `Operations` and its existing API identity permissions before deploying code. The repository never creates the table at runtime. Include it in the maintenance inventory with `Storage:OperationsProvisioned=true`. Keep operator-owned gates out of settings files that deployment scripts overwrite.
2. Deploy API operations endpoints and then Functions with `Pipeline:OpportunitiesEnabled=false`. Durable delivery and run health are active immediately; the additive table is a prerequisite, not an optional fallback. Validate authenticated read/write/CAS access with synthetic operational records.
3. Complete the opportunity-authority migration and API verification described in the overall rollout guide. Enable `Storage:OpportunitiesEnabled` on the API first. Then enable `Pipeline:OpportunitiesEnabled` on Functions. Confirm configured ChatId/UserId and the private-chat webhook binding before an explicitly authorized notification test.
4. Check a synthetic actionable item, Submitted replay, canceled/confirmed Drop picker, Snooze replay, failed-edit replay, wrong-user rejection, and a multipart digest. Verify the original message edits and each successful part has a receipt. Confirm a non-change manual discovery/tracker run remains quiet while an injected source failure is visible.
5. For rollback, disable the consumer opportunity gate before disabling API opportunity authority. `Telegram:Enabled=false` stops Telegram delivery and inbound actions. Roll back the hosts if operational endpoints are unavailable; preserve the additive Operations table and its receipts. Do not delete receipts to clear an uncertain send.

Quiet hours, health schedule, callback expiry, and the 48-hour rescue are currently explicit policy constants, not additional deployment settings.

## Credential telemetry and rotation validation

The previous default `IHttpClientFactory` Telegram handlers logged credential-bearing request URLs. Search-provider URLs can likewise contain a query-string API key. Both typed clients now remove the default HTTP loggers. Telegram logs only operation and response status; it does not log provider response bodies, request URLs, or transport exception messages/inner exceptions. HTTP dependency enrichment removes query strings, redacts Telegram token paths, and records only exception type rather than raw exception events or status text. New code cannot remove secrets already stored in historical telemetry.

After deploying the logging fix, an authorized operator should rotate exposed Telegram/search credentials through the relevant provider and Key Vault. Keep replacement values out of shell history, command arguments, transcripts, and test output. Refresh the application's Key Vault reference and verify configuration resolution without returning the secret. Register/verify the webhook using its secret header without printing it. Confirm the superseded credential is rejected and perform a narrowly scoped delivery/search validation only when authorized.

Inspect telemetry starting after the deployment/rotation boundary. Return aggregate counts only for matches of a Telegram `bot<digits>:<secret>` path, query parameters named key/token/secret, or known synthetic test markers. Inspect traces, dependencies, exception message/stack fields, and custom dimensions. The expected count for new credential material is zero. Historical matches require the organization's retention/purge procedure; do not copy their content into an issue or chat.

Local regression tests cover default HTTP factory logging, transport exceptions, provider response-body echoes, request/exception span enrichment, pinned callback revisions and replay, Drop reasons, multipart text preservation, outbox restart/concurrency/partial failure/binding repair, known/ambiguous delivery outcomes, persisted incident/recovery behavior, weekly aggregation and explicit pricing, Chicago DST boundaries, weekly identity, and the 48-hour rescue. These are synthetic tests and do not contact Telegram or Azure. A host build confirms the OpenTelemetry options wiring; production export and credential rotation still need the release-time validation above.
