# Actionable speaker pipeline deployment plan

Status: Validated

The user authorized completion of the actionable pipeline rollout on 2026-10-16.
The earlier production promotion is complete and retained below as history; it
does not establish readiness or completion of this release.

## Current rollout

Recipe: cicd, using successful main CI artifacts through the existing manual
production release workflow. Additive infrastructure uses the Bicep recipe for
`infra/data-tables.bicep` only. Keep the existing hosting subscription, resource
groups, identities, locations, hosting plans, URLs, and notification destinations.
The subscription is explicitly selected on every cloud operation. No azd
conversion, resource replacement, source-repository writes, or external CFP
submission is part of this rollout.

The owner explicitly confirmed that hosting remains in place while dashboard
sign-in uses the owner's personal tenant. The earlier description of the hosting
subscription and Key Vault as personal was incorrect. This correction authorizes
the dashboard identity configuration only; it does not relocate resources or
change machine API authentication. The personal dashboard identity configuration
is corrected and the account-chooser release succeeded. Interactive owner
sign-in and retirement of the superseded dashboard registration remain pending.

All validation checks must pass:

- [x] Core Azure CLI authentication, table-template build, ARM validation, what-if
- [x] Applicable Azure Policy validation
- [x] Build, complete unit/integration/evaluation suites, and CI source provenance
- [x] Static role verification and existing identity dependencies
- [x] Validation proof recorded and failures resolved

Activation gates:

- [x] Dedicated GitHub App reads only the two intended source repositories
- [x] Private key stored in the existing Key Vault; Functions reference resolves
- [x] Confirmed personal dashboard identity configured with owner restriction
- [x] Three additive tables provisioned and included in the API inventory
- [x] Successful main CI artifacts deployed API first, then Functions
- [x] Telegram credential replacement and resolved Key Vault reference
- [x] Telegram command and digest delivery with controlled runtime redaction check
- [x] Fresh frozen private snapshot, isolated restore and migration rehearsal
- [x] Production migration, retained-data verification, and authority switch
- [x] Validated catalog and catalog-aware scoring enabled
- [x] GitHub App and installation Push subscription, webhook activation, and actual ping delivery verified
- [ ] Future observation: actual source push and resulting catalog reconciliation
- [x] Controlled catalog, scoring, tracker, discovery, email, and operational health
- [ ] Interactive owner dashboard sign-in
- [ ] Superseded dashboard registration and credential retired after owner acceptance
- [x] Telegram Drop/Cancel picker and message editing verified without business-state changes
- [x] Normal writers and delivery restored, except explicitly gated channels
- [ ] First scheduled behavior observed

Use `docs/actionable-pipeline-rollout.md` for sequencing and rollback constraints.
After new-schema writes begin, do not switch back to the frozen Events baseline.
Keep private configuration, data, identity, and telemetry evidence outside Git.

## 7. Validation Proof

Current rollout validation on 2026-10-16 UTC:

- The Azure validation helper completed CLI authentication, compilation, ARM
  validation, and what-if for `infra/data-tables.bicep` on the explicitly pinned
  hosting subscription. Detailed preview adds Catalog, Operations, and
  Opportunities, retains the existing six tables, and deletes or replaces
  nothing. The table-only deployment leaves hosting and alerts untouched.
- Applicable resource-group and inherited policy query returned no assignments.
- [Review-fix CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38980390452)
  passed at commit `59176febbe0864d8413f69081696de176ae0330f`: 542 tests passed,
  zero failed, and one opt-in live-model test skipped. Real Azurite conditional
  storage tests and both deterministic evaluation suites ran. Build, Markdown,
  sample validation, and all infrastructure compilation checks passed.
- The two review findings were fixed and regression-tested: full-catalog repeat
  outcomes invalidate scoring cache entries, and repeated notification identities
  honor retry backoff and active worker leases. The reviewed threads were resolved
  before merging PR 39. Application deployment still requires successful main CI
  artifacts and the workflow's checksum/provenance checks; it never rebuilds.
- Static infrastructure review preserves existing identities and their roles.
  Live preflight confirmed API Table Data Contributor on the existing data
  account, Blob Data Contributor on the private artifacts container, and Key
  Vault Secrets User. Functions has Key Vault access and no business-table role.
  Existing subscription, resource groups, and eastus2 hosting
  are already authorized; no environment, region, or subscription choice changes.
- Both current host configurations and the prior successful release packages
  were retained privately. Every package checksum matched its manifest. The
  dedicated GitHub App's exact two-repository read access and Key Vault reference
  resolution were verified. Dashboard identity and webhook credentials are staged
  privately with feature activation still gated.

Historical proof below is not evidence for changed binaries or the new schema.

Personal dashboard correction predeployment proof on 2026-10-17 UTC:

- Existing hosting, infrastructure, and resource RBAC remain unchanged. The
  authorized correction changes dashboard identity configuration; machine API
  authentication and operator controls were verified preserved.
- Graph checks confirmed the personal dashboard app, exact-owner assignment,
  required user assignment, and owner-scoped consent for `openid` and `profile`
  only. The credential reference resolves from the existing Key Vault. Both API
  and Functions authenticated readiness checks returned HTTP 200.
- [Account-chooser main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38997527523)
  passed for commit `66a021e03783a4b6098fb934ad58781e36ea9d05`: 558 tests passed,
  zero failed, and one opt-in live-model test skipped. This includes 88 API
  tests, 81 storage tests with real Azurite, and both deterministic evaluation
  suites. The successful deployment of these tested artifacts is recorded below;
  interactive owner sign-in remains unverified.

## Current release evidence

The [actionable release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38981000712)
succeeded on its second attempt at 2026-10-16 23:36 UTC, using main CI artifacts
from commit `9ba92ddc25e3ea29934ad9a863c99f6b1d58227f`. Both hosts passed
authenticated readiness and the workflow preserved every operator control.
The first attempt stopped before Functions deployment: Oryx found the server
and Blazor client runtime configurations and started its placeholder host.
An explicit API startup command restored service without changing the package.
[PR 40](https://github.com/bhaydin/speaker-pipeline/pull/40) persists that command
in infrastructure and the release workflow; its complete CI passed before merge.

The three additive tables were provisioned without changing existing tables or
hosting resources. The initial dashboard registration was single-tenant and its
secret reference resolved, but the interactive check exposed that it targeted
the hosting tenant instead of the confirmed personal owner identity. The
personal identity configuration is now corrected, pending interactive verification. Authority,
catalog, scoring, and the dashboard are enabled. Telegram credential replacement
and GitHub webhook activation have since passed the checks below; interactive
owner sign-in remains open.
The configured Google search project currently returns access-denied 403 and
must remain visible as degraded discovery rather than a healthy empty result.

The fresh frozen nine-table snapshot was restored locally and migrated, with
retry safety and both old/new schema recovery verified. Production migration
copied all 38 opportunities; retry created no duplicates. Every original typed
property, ETag, and timestamp remained unchanged in the other eight tables,
including all four legacy talks and 458 notification records. All cards retain
migration-pinned placement and independent suppression. API authority is now
enabled, authenticated reads expose 38 records with ETags, and maintenance
administration is closed. Never return authority to the stale Events baseline.

The owner dashboard shell is live; anonymous data reads return 401, signed-out
session checks expose no request token, and the Entra challenge uses the exact
HTTPS callback with PKCE. Interactive owner sign-in reached an administrator
approval screen in the wrong tenant. The personal identity configuration and
account-chooser release are now verified; interactive owner sign-in remains
pending.

The initial catalog generation contains 41 talks, 42 versions, 53 engagements,
and eight public artifacts from the two read-only repositories. An unchanged
second reconciliation preserved that generation. Both runs succeeded with zero
model extraction calls; uncertain deliveries and unlinked artifacts remain in
review. The initial receiver check accepted a valid signed ping and rejected an
invalid one without enqueueing work. The later GitHub-side activation and real
delivery evidence is recorded below.
Catalog worker and nightly fallback are enabled. Scoring is configured for the
validated catalog with explicit candidate, prompt, and completion ceilings.

Two bounded scoring batches completed 36 decisions with no errors. All matched
talk/version references, decision history, and input provenance passed review;
the original 38 cards retained their placement, suppression, and event facts.
The second batch reused the first 20 cached decisions. A subsequent snapshot
reused all 36 decisions with zero model tokens, and its email delivery and outbox
completion were confirmed. Model usage across the two scoring batches was
111,955 input and 21,974 completion tokens; pricing remains explicitly unknown.

Bounded discovery persisted two new opportunities and a degraded run, including
the search provider's 403. Both the incident and discovery change emails have
confirmed delivery receipts. The manual HTTP response failed after that work;
the same invocation was reconciled without retrying its writes or notifications.
The Functions serializer treated count fields and result arrays as duplicate
names. [PR 41](https://github.com/bhaydin/speaker-pipeline/pull/41) gives the counts
distinct names and adds three regression cases using the actual worker serializer.

The [response-fix release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38985592586)
succeeded at 2026-10-17 00:26 UTC, deploying commit
`fca8faf3dfd641e09b71d9c555217c0ad5e5e71d` from
[successful main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38985360245).
All 545 tests passed, with one opt-in live-model test skipped. Both hosts passed
authenticated readiness and all operational switches were preserved. The bounded
post-release discovery check returned HTTP 200, changed no data, and created no
new delivery or outbox records. Its repeated provider failures retained the
existing degraded incident without resending the alert.

The subsequent tracker run examined 40 opportunities, changed none, and sent
nothing. Its one actionable deadline already had a matching email notification
in the retained ledger, so the channel was correctly skipped before creating an
outbox entry. The two newly discovered records follow the pipeline, with no
fabricated user placement, submission, decision, or human action history.

At 2026-10-17 00:28 UTC, after response-fix verification, the 12 non-Telegram
functions and email delivery were restored, including normal discovery limits,
health/retry timers, ingestion,
and catalog reconciliation. API authority and catalog-aware scoring remain
enabled; maintenance remains closed. Telegram delivery and its receiver were
kept paused for credential replacement at that stage. Subsequent credential and
webhook checks are recorded below. Scheduled-run observation remains open.

Telemetry from the initial actionable release through 2026-10-17 00:07 UTC contained 9,868
rows and zero matches for the checked credential patterns. This interval contains
real API, model, and email activity but no Telegram traffic; it does not replace
the required post-rotation Telegram send/edit/callback check.

Verification after the response-fix release confirmed both deployed commit
identities, authenticated readiness, and maintenance returning 404. There were
no running operations, pending outbox items, or failed/uncertain deliveries.
The post-release telemetry sample contained 592 rows, zero credential-pattern
matches, zero exceptions, and one successful discovery completion. Telegram
traffic was absent from that sample while the channel was paused.

At 2026-10-17 01:12 UTC, the dedicated GitHub App and its installation both
subscribed to Push and retained selected access to exactly the two intended
repositories, with Contents and Metadata read-only permissions. The configured
webhook uses the catalog receiver, JSON content, and SSL verification. After
synchronizing its staged secret, an actual GitHub ping redelivery returned HTTP
202; a subsequent real installation event also returned 202. No source files or
commits were written, and no real push delivery has yet been observed. These
checks prove GitHub delivery and authentication, while push-triggered catalog
reconciliation remains an observation gate.

Telegram credentials were rotated for the same bot. The replacement token is
valid, the previous token returns 401, and the host's Key Vault reference
resolves. The receiver rejected an unauthenticated request with 401 and accepted
an authenticated empty update with 200. A real `/status` command reached the
receiver but Telegram rejected its response with HTTP 400 because the request
included `reply_markup: null`.

[PR 43](https://github.com/bhaydin/speaker-pipeline/pull/43) omits null reply markup
for command responses. It merged as commit
`cf53749c679e12f2793285119deeb67ee7b238e3`; its
[main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38990275380)
passed with 549 tests and one opt-in live-model test skipped. The
[Telegram response-fix release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38990416430)
succeeded at 2026-10-17 01:29 UTC. Both hosts passed authenticated readiness, and
the release verified that operator controls were preserved. Independent API and
Functions readiness checks also returned HTTP 200.

The controlled Telegram scoring run returned HTTP 200 with one successful run:
36 cached decisions, two new decisions, no deferred candidates, and no errors.
It used 6,406 input and 1,368 completion tokens; pricing remains unknown. Durable
outbox and delivery receipts confirmed three Telegram message parts and one
email, all Sent/Completed, with no failed, uncertain, or still-sending delivery.
The five temporary controls were restored at 2026-10-17 01:32:34 UTC; all other
configuration was preserved.

Two queued real `/status` handlers succeeded, and Telegram reported zero pending
updates. One handler cancellation occurred before readiness was established;
no handler failures occurred after readiness through 01:32:49 UTC. The telemetry
sample contained 2,680 rows with zero matches for the checked credential patterns.
The owner then verified that Drop opened the reason picker and Cancel restored
the buttons. Read-only comparison confirmed all 40 opportunities and 76 history
records remained unchanged; the message version changed, the picker cleared,
and the message lock was released. No real Drop was committed. Mutation and
replay behavior are covered by regression tests, including failed-edit recovery;
the live Drop/Cancel check does not claim a mutating-action replay.

At 2026-10-17 03:06:43 UTC, the dedicated personal-tenant dashboard app was
configured with the exact owner, required user assignment, and an owner-scoped
grant for `openid` and `profile` only. Its credential reference resolves from
the existing Key Vault. Only four dashboard settings changed; all unrelated
settings, machine API authentication, and operator controls were verified
unchanged. API and Functions readiness both returned HTTP 200.

[PR 45](https://github.com/bhaydin/speaker-pipeline/pull/45) adds an account
chooser for dashboard sign-in. It merged as
`66a021e03783a4b6098fb934ad58781e36ea9d05`; its
[main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38997527523)
passed with 558 tests, zero failures, and one opt-in live-model test skipped.
The [account-chooser release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38997747723)
succeeded using those tested artifacts. Both API and Functions authenticated
readiness returned HTTP 200, and all operational controls were independently
verified preserved. At 2026-10-17 03:19:36 UTC, the public login redirect targeted
the confirmed personal tenant and client with PKCE and the account chooser.
Transient API startup HTTP 500 responses and probe timeouts recovered before
final verification; their underlying cause has not been established.

Interactive owner sign-in remains unverified. Retirement of the superseded
dashboard registration and credential is prepared but remains pending owner
acceptance; the current hosts no longer reference them. Actual source push and
first scheduled-run observations also remain open. The GitHub configuration and
authenticated delivery checks have passed.

## Historical production promotion

## Intent and authorization

The user approved implementation of the production promotion plan on 2026-10-16.
Promote the existing estate in place, preserve its physical names and URLs, retain
real data and all four talks, and require manual releases of CI-tested artifacts.
No new hosted dev environment, region change, or resource replacement is approved.

## Recipe

Type: cicd (GitHub Actions and existing Azure CLI/Bicep deployment).
This is an existing non-azd application; no azure.yaml or azd conversion is needed.
The subscription is discovered from the existing API resource and pinned in the
production GitHub environment. The CLI default subscription must never be used.
Compute location remains eastus2; shared resources retain their existing locations.

## Changes

One production environment and OIDC federation; reconciled existing-resource
infrastructure; role-protected API and maintenance tools; full CI; selective data
cleanup with private backup, journal, local restore rehearsal, and recovery runbook.

## Cutover

Validate code, infrastructure preview, identity assignments, and CI first. Prepare
the concrete data manifest before deletion. Pause all writers and notification
lanes, export all six business tables, revalidate, and apply exact operations.
Verify references and content before restoring normal operation. Preserve existing
notification destinations and schedules. Disable maintenance access afterward.

## Validation checklist

- [x] All predeployment validation checks pass
  - [x] Complete solution build, unit tests, and both deterministic eval suites
  - [x] NuGet vulnerability audit and markdown/workflow validation
  - [x] Bicep build and live preview: no replacement or unintended stack
  - [x] Azure CLI authentication and ARM template validation on the pinned subscription
  - [x] Applicable Azure Policy review
  - [x] API roles, OIDC federation, and resource-scoped identity permissions
  - [x] Maintenance preview, concurrency, resume, and restore tests
  - [x] Local Azurite restore rehearsal with matching logical content
- [x] Manual production release from successful CI artifacts
- [x] Authenticated data reads, negative auth, and function registration/readiness
- [x] Final live backup restored and logically compared through a local API
- [x] Reviewed cleanup, exact retained-data verification, and no-op retry
- [x] Resolve the two private scoring proposals against possible manual intent
- [x] Apply and verify the two approved scoring repairs after their supporting release
- [x] Retire obsolete deployment resources and unnecessary permissions
- [x] Close maintenance administration and restore original writer/delivery controls
- [x] Enable all four failure/missed-run alerts with verified routing
- [ ] Observe the first daily and weekly scheduled runs and expected delivery

## Rollback

Retain previous packages/configuration and private data backup/journal through at
least 2026-11-16 and until acceptance observations are resolved. On an acceptance
failure, pause writers and delivery before reopening operator-only maintenance.
Restore only affected data and redeploy previous artifacts when required. Never
replay notifications. All 23 retained recovery artifacts passed hash verification.

## Historical validation proof

Validation performed on 2026-10-16 (UTC), using the explicitly discovered existing
subscription rather than the CLI default:

- Latest full solution verification: 378 tests passed, zero failed, and one
  opt-in Azurite test was skipped. Both deterministic eval suites ran (15 and
  six cases); maintenance tests covered lock-loss, derived repairs and explicit
  review. The tested application packages came from
  [successful main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38937181101).
- The explicit Azurite SDK integration run passed, including all eight property
  types, unknown fields, full create-only restoration, replacement cleanup,
  idempotent retry and selective rollback. The subsequent live-backup rehearsal
  also passed, as recorded below.
- `dotnet list SpeakerPipeline.slnx package --vulnerable --include-transitive
  --format json`: zero vulnerable packages. The unused identity dependency was
  removed; there is no blanket NU1903 warning exemption.
- Repository Markdown lint, actionlint, workflow YAML validation, PowerShell
  parser checks and `git diff --check` passed.
- `validate-deployment.ps1 -Scope group` completed Bicep compilation, ARM
  validation and live what-if successfully. Reviewed resource changes: six
  creates (four disabled alerts, private artifacts container, scoped API Blob
  role), four existing compute modifications, six unchanged tables, four ignored
  existing dependencies. No deletions, replacements, app-setting changes or
  deployment-container changes. Nested deployment records are not new stacks.
- Applicable inherited/resource-group policy assignment query returned no
  assignments. Existing Azure roles were audited, including all subscription
  identity attachments. Agents have no pipeline Table Storage role; only the
  API gains container-scoped artifact access.
- Production GitHub environment is main-only with pinned configuration. Entra
  roles and production federation were created; the refreshed owner token was
  verified to carry Pipeline.Access and Pipeline.Maintenance. Functions and the
  release identity have Pipeline.Access assignments. The subsequent successful
  release passed the authenticated runtime checks.

Exact Azure output, resource IDs, configuration backups and token-free validation
evidence are retained outside Git under the operator's private production-cutover
directory. The predeployment validation above did not change live business data.

## Completed cutover gates — 2026-10-16

- The [final manual production release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38943407144)
  succeeded using the tested application packages. API and Functions readiness,
  ordinary reads, anonymous/dev-token rejection and release-identity denial of
  maintenance access passed. The write freeze was verified before the backup.
- The frozen backup contained 502 rows across all six business tables. A full
  restore through an isolated local API matched every row logically. The local
  rehearsal applied all 12 cleanup changes and verified their journaled rollback
  against the original data before the live apply.
- Live cleanup deleted five confirmed fictional events and their three confirmed
  fixture submissions, and removed four exact placeholder deck properties.
  All four adopted talks were retained. The resulting 494 rows comprise 38
  events, four talks and 452 notification records; the other three tables are
  empty. Retained content and references passed verification, and retrying the
  completed operation made no further changes.
- Cleanup recomputation found no conflict-flag repairs. The user identified two
  prior scoring decisions as tests, resolving the intent review. The two approved
  scoring repairs were applied to retained event rows: eight field assignments
  across the four reviewed scoring fields per row. Verification covered all 494
  rows, including all 452 unchanged notification records. Retry made no changes.
- Controlled tracker maintenance examined 38 events, and bounded discovery
  examined three candidates. Neither run changed pipeline records.
- Five obsolete items were retired after dependency checks across nine accessible
  subscriptions: the legacy GitHub deployment environment and its federation,
  two unused managed identities, and the API identity's unnecessary Foundry role.
  Active identities, production and main-branch federations, and required access
  remained intact. The legacy Functions API role was already absent.
- Functions API access recovered after changing only its requested scope from
  `api://<API-application-id>/.default` to `<API-application-id>/.default`.
  The audience, managed identity and role assignment stayed the same. Protected
  readiness and ordinary API reads passed; the underlying token-cache cause was
  not proved. Release checks now tolerate bounded startup transitions while
  failing unexpected authorization results immediately.
- Normal operation was verified at 18:07:21 UTC. Maintenance administration
  returned 404, anonymous/dev-token requests returned 401, and API and Functions
  readiness returned 200. The ten original writer/delivery controls were restored,
  unrelated settings were unchanged, and a replacement Functions startup with
  matching readiness telemetry was verified.
- The final telemetry gate passed at 18:18:05 UTC. All four alert queries reported
  zero breaches; readiness and four consumer paths returned 200. Ten successful
  Table dependencies and a successful Functions-to-API span were observed, with
  no new failures or exceptions. The maintenance interval from 16:33:31 to
  18:04:50 UTC had no recorded notification calls or notification-log writes.

All four failure/missed-run alerts were enabled and reread at 18:19:00 UTC,
each with the verified action group. The first scheduled-run observations are
Discovery on 2026-10-17 at 05:00 UTC, Tracker at 05:30 UTC, and Scoring on
2026-10-19 at 06:00 UTC. Their successful completion telemetry and any expected
deduplicated delivery remain to be observed.
