# Production promotion deployment plan

Status: Validated

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
- [ ] Manual production release from successful CI artifacts
- [ ] Authenticated data reads, negative auth, function registration, and telemetry
- [ ] Final live backup restored and logically compared through a local API

## Rollback

Retain previous packages/configuration and private data backup/journal for at least
30 days. Keep maintenance enabled on acceptance failure; restore only affected
data and redeploy previous artifacts when required. Never replay notifications.

## 7. Validation Proof

Validation performed on 2026-10-16 (UTC), using the explicitly discovered existing
subscription rather than the CLI default:

- `dotnet test SpeakerPipeline.slnx -c Release --verbosity quiet`: 326 passed,
  zero failed, one opt-in Azurite test skipped. Both deterministic eval suites
  ran, including the final lock-loss, derived-repair and explicit-review checks.
  A subsequent migration-only run passed 35 tests including the added offline
  recompute check (327 tests across the solution).
- The explicit Azurite SDK integration run passed, including all eight property
  types, unknown fields, full create-only restoration, replacement cleanup,
  idempotent retry and selective rollback. Live-backup rehearsal remains a
  cutover gate after the maintenance-capable API is deployed.
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
  release identity have Pipeline.Access assignments; runtime checks remain a
  release gate.

Exact Azure output, resource IDs, configuration backups and token-free validation
evidence are retained outside Git under the operator's private production-cutover
directory. No live business data was changed during validation.
