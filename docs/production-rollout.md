# Single-environment production rollout

The existing Azure estate is the production system. Physical `dev` suffixes are
historical; there is no hosted development environment or promotion pipeline.
Real records, private exports, tokens and deployment credentials never enter
Git, CI artifacts or public logs. Repository samples remain fictional test data.

## Recorded cutover — 2026-10-16

The [final manual production release](https://github.com/bhaydin/speaker-pipeline/actions/runs/38943407144)
succeeded using application packages from
[successful main CI](https://github.com/bhaydin/speaker-pipeline/actions/runs/38937181101).
Latest solution validation passed 378 tests and both deterministic eval suites;
the opt-in Azurite integration test passed separately. Authenticated API and
Functions readiness, ordinary reads, negative authentication and release-identity
denial of maintenance access passed.

The final frozen backup contained 502 rows across all six business tables. An
isolated local API restored it with exact logical equality, then rehearsed all
12 cleanup changes and their journaled rollback before the live apply.

The reviewed live cleanup removed five fictional events, their three fixture
submissions and four placeholder deck properties. It retained 38 events, all four
adopted talks and all 452 notification records: 494 rows total. Submissions,
Topics and Blackouts are empty. Retained content and reference checks passed;
retrying the completed cleanup operation made no changes.

Recomputation found zero conflict-flag repairs. The user confirmed that two prior
scoring decisions were tests. The approved repairs updated eight reviewed field
values across those two retained event rows. All 494 rows were verified, all
452 notification records remained unchanged, and retry made no further changes.
Controlled tracker maintenance examined 38 events, and bounded discovery examined
three candidates; neither run wrote records.

Five obsolete items were retired after checks across nine accessible
subscriptions: the legacy deployment environment and its federation, two unused
managed identities, and the API identity's unnecessary Foundry permission.
Production identities, required access and the retained release federations were
preserved. Functions API access recovered after changing only its requested scope
to `<API-application-id>/.default`, using the same audience, identity and role.
The observed recovery does not establish the underlying token-cache cause.

Normal operation was verified at 18:07:21 UTC. Maintenance administration is
closed, the ten original writer/delivery controls are restored, and unrelated
settings are unchanged. API and Functions readiness returned 200 after a verified
replacement Functions startup; maintenance administration returned 404 and
anonymous/dev-token calls returned 401. All four failure/missed-run alerts were
enabled and reread at 18:19:00 UTC, each with verified action-group routing.

The final telemetry gate passed at 18:18:05 UTC: all four alert queries reported
zero breaches, readiness and consumer paths returned 200, and successful Table
dependencies and a Functions-to-API span were observed without new failures or
exceptions. No notification calls or notification-log writes were recorded during
the maintenance interval from 16:33:31 to 18:04:50 UTC.

The first scheduled-run observations remain open: Discovery on 2026-10-17 at
05:00 UTC, Tracker at 05:30 UTC, and Scoring on 2026-10-19 at 06:00 UTC. Verify
successful completion telemetry and any expected deduplicated notifications.
Hashes were verified for 23 private recovery artifacts. Retain the packages,
configuration, snapshots, manifests and journals through at least 2026-11-16 and
until these acceptance observations are resolved.

## Prepare the release boundary

Configure one GitHub environment named **production**, restricted to `main`,
with the following values. Copy values from the verified existing estate; do not
create replacement resources by changing suffixes.

| Kind | Name | Value / purpose |
|---|---|---|
| Variable | `AZURE_RESOURCE_GROUP` | `rg-speakerpipeline-dev` |
| Variable | `AZURE_WEBAPP_NAME` | `app-speakerpipeline-api-dev` |
| Variable | `AZURE_FUNCTIONAPP_NAME` | `func-speakerpipeline-dev` |
| Variable | `API_SCOPE` | Existing API App ID URI followed by `/.default` |
| Secret | `AZURE_CLIENT_ID` | Production release identity client ID |
| Secret | `AZURE_TENANT_ID` | Existing tenant ID |
| Secret | `AZURE_SUBSCRIPTION_ID` | Existing production subscription ID |

The release identity's federated credential uses issuer
`https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`
and subject `repo:bhaydin/speaker-pipeline:environment:production`. It needs
deployment/configuration and host-key read access on the two existing apps, plus
the API's **Pipeline.Access** app role for authenticated smoke checks. It does
not need **Pipeline.Maintenance** or direct pipeline data-plane access. The legacy
`dev` GitHub environment and its federation were retired after dependency checks.
The production and main-branch federations remain.

Branch protection should require CI's Markdown, sample JSON and full solution
test jobs. CI builds the entire solution, runs all unit tests and both eval
suites as blocking checks, and validates Bicep. Main CI publishes one immutable
artifact containing the tested API and Functions ZIPs, application settings,
commit/run manifest and checksums. Retention is 90 days. Preserve the last good
package privately before expiry if it must remain a rollback target.

## Establish the maintenance window

During the initial rollout, the legacy API had no maintenance endpoints or
write-freeze enforcement. Consumers were paused and drained before deploying the
maintenance-capable API, and ordinary-write rejection was verified before the
final snapshot. Future maintenance windows can use the sequence below directly.
A configuration flag on a retained legacy binary is not a write barrier.

1. Record the exact subscription, resource names, data counts, current code
   package/version, identity assignments and app settings in private storage.
   Preserve pre-cutover packages separately: older workflows did not retain
   rollback artifacts. Verify the restore procedure before any deletion.
2. Stop concurrent deployments and pause each writer: the three timer triggers,
   discovery/scoring/tracker HTTP triggers, ingestion, Telegram and any local
   MCP/API clients. Use `AzureWebJobs.<FunctionName>.Disabled=true` for writer
   functions; keep `ReadinessHttpTrigger` enabled. Disable email and Telegram
   delivery with their existing enable flags. Wait for in-flight runs to finish.
3. Set API `Maintenance__FreezeWrites=true`. Enable
   `Maintenance__Enabled=true` only for the operator's maintenance session;
   that operator additionally requires **Pipeline.Maintenance**. Verify ordinary
   writes are rejected while reads/readiness remain available. Disable missed-run
   alerts for the planned window.
4. Export all six data tables and required artifact metadata using the maintenance
   API. Review the cleanup manifest against exact row keys, fingerprints and
   relationships. Preserve real submissions, ideas, blackouts and notification
   history unless the approved manifest explicitly identifies them as fixtures.
   Keep backup, manifest and operation journal outside the repository.
5. Review the infrastructure what-if described in
   [infra/README.md](../infra/README.md). Reconcile the existing resources
   incrementally. Ensure the API identity has container-scoped Blob Data
   Contributor for private maintenance artifacts; agent identities must not gain
   pipeline data access.

The application release deliberately preserves maintenance, timer and
notification controls. A code deployment never resumes writers or notifications.

## Release and verify

Merge the reviewed change, wait for the **entire main CI run** to succeed, then
manually dispatch **Release production** with its run ID and full commit SHA:

```bash
gh workflow run deploy-production.yml --ref main \
  -f ci_run_id=<successful-main-ci-run> \
  -f commit_sha=<full-tested-commit-sha>
```

The workflow rejects mismatched commits, failed runs, pull-request artifacts and
runs from another repository. Releases are serialized. It deploys both tested
packages without rebuilding, pins the runtime to Production, and applies only
additive application configuration. Identity/endpoints/Key Vault references must
already be present; see the existing configuration snapshot.

Before deploying consumers it explicitly restarts the API, then verifies `/health`,
protected `/health/ready`, authenticated reads, anonymous/dev-token rejection and
403 denial of maintenance access to the release identity. Each HTTP assertion
uses bounded retries for transport and transient service failures; unexpected
authorization results fail immediately. This covers startup transitions between
checks as well as initial warmup, within a 25-minute job limit.

After the Functions deploy it verifies all expected functions and calls the
key-protected `/api/readiness` without logging the key, then rechecks authenticated
API readiness. These checks send no notifications and write no pipeline records.
The workflow compares operational switches before and after deployment and fails
if they changed. A green release is required; app-setting release markers alone
are not evidence of success.

Complete the reviewed cleanup through the maintenance API while writers remain
paused. Verify retained records, cross-table references and fixture absence;
rerun the dry run to prove no further approved changes remain. Keep the cleanup
journal and before/after counts alongside the backup.

Recompute cleanup-related conflict flags from frozen before/after snapshots with
the migration CLI. Its report preserves deliberate decisions and lists only
candidates whose scoring inputs changed for separate review. Do not run the
normal scoring/tracker triggers as a substitute: they can change unrelated
categories and emit notifications. Apply the separately reviewed flag manifest
through the same conditional journal before reopening writes.

Resume normal operation deliberately: disable maintenance administration, remove
the write freeze, restore writer functions in dependency order, verify one real
discovery/maintenance/scoring cycle, and restore delivery to the configured
operator channels. Enable the four alert rules with a verified action group.
Review exception rates, failed dependencies, successful scheduled runs and
notification deduplication before declaring the cutover complete.

## Recovery

Keep writers and notifications paused on any failed validation. The release
record artifact contains the last successful release workflow reference,
previous API/Functions CI markers, requested SHA and run ID; it contains no
runtime secrets or pipeline records. A partially completed
release can leave API and Functions at different versions, so inspect both and
use the last **successful workflow** as the recovery reference.

Redeploy the prior successful CI artifact with the same manual workflow and its
original run ID/SHA. This restores both packages and their non-sensitive
application settings while preserving the current pause. Before rollback, check
that the target understands the current auth and maintenance controls. For the
first production cutover, use the privately captured pre-cutover package only
under the reviewed operator procedure; pre-production packages cannot satisfy
the new workflow's readiness/auth gates.

The legacy API also predates `Maintenance__FreezeWrites` and ignores that setting.
Complete and verify data recovery through the maintenance-capable API before
restoring the legacy binary. Keep all consumers paused and restrict external API
access to the recovery operator until recovery is complete. Do not treat a
retained maintenance setting as enforcement by the old binary, or roll back to
it while maintenance API restore work is still required.

Code rollback does not restore deleted data. Use the maintenance restore path
with its reviewed backup and conflict checks before reopening writes. Stop on
changed rows rather than overwriting concurrent work. Retain this cutover's backup
and journal through at least 2026-11-16 and until the acceptance window is complete;
close the maintenance interface, remove
temporary operator assignments and verify scheduled-run alerts after recovery.

References: [GitHub artifact retention and download](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)
and [Azure scheduled query alert resource schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.insights/2023-12-01/scheduledqueryrules).
