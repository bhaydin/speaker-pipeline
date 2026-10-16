# Production promotion deployment plan

Status: Deployed

The production release, reviewed cleanup, approved scoring repairs and return to
normal operation have succeeded. All four failure/missed-run alerts are enabled
with verified routing. First scheduled-run observations remain pending.

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

## 7. Validation Proof

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
