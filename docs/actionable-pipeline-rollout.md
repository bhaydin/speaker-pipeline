# Actionable pipeline rollout

This release adds shared opportunity state, actionable Telegram notifications,
a read-only talk catalog, feedback-aware scoring, and an owner-only Blazor
dashboard. Source code and local verification do not activate these features in
production. Follow the existing [production runbook](production-rollout.md) and
use successful main CI artifacts for each deployment.

## Boundaries and configuration

The existing App Service hosts both `/v1` and `/dashboard/`. The dashboard is a
Blazor WebAssembly client using same-origin owner cookies. It has no storage
credential or client secret. Entra authentication checks the exact tenant and
owner object ID; cookies are revalidated, and browser mutations require the
antiforgery token returned by `/v1/dashboard/session`. Machine callers continue
using the existing bearer-token policy. The static shell contains no private
records; every data endpoint requires authentication.

| Host | Setting | Activation |
|---|---|---|
| API | `Storage__OperationsProvisioned=true` | After creating Operations; includes it in snapshots/readiness |
| API | `Storage__CatalogProvisioned=true` | After creating Catalog; includes it in snapshots/readiness |
| API | `Storage__OpportunitiesProvisioned=true` | After creating Opportunities; includes it in snapshots/readiness |
| API | `Storage__OpportunitiesEnabled=true` | Only after frozen copy and restore rehearsal pass |
| Functions | `Pipeline__OpportunitiesEnabled=true` | After the API authority switch; enables shared-state scoring and callbacks |
| API | `Catalog__Enabled=true` | With catalog activation; requires validated catalog matches for actionable decisions |
| Functions | `Catalog__Enabled=true` | After the read-only GitHub App is installed and configured |
| Functions | `ScoringAgent__CatalogEnabled=true` | After first validated generation and model evaluation |
| API | `Dashboard__Enabled=true` | After authority switch and owner authentication setup |

Provision all nine tables through `infra/data-tables.bicep`, including the three
additions. Existing table data is untouched. Operations must exist before the
new notification hosts run, even while opportunity actions are disabled. The
ordinary code release must preserve maintenance, writer, notification, catalog,
and authority controls. These operator-owned flags are deliberately absent from
the automatically reapplied Functions settings file.

See [notification operations](actionable-notifications.md) for delivery,
quiet-hour, health, credential rotation, and incident settings. See
[catalog operations](talk-catalog.md) for GitHub App installation, webhook,
model limits, and reviewed source corrections. App credentials use references
to the existing hosting environment's Key Vault. Do not use a repository PAT as
the production catalog credential.

## Owner dashboard setup

The owner authorized retaining the existing hosting and machine API
authentication while using the owner's personal tenant for dashboard sign-in.
Hosting and dashboard identity are separate; the hosting tenant must not be
assumed to be the owner's personal tenant.

Create a single-tenant Entra web application in the confirmed personal tenant.
Register `https://<existing-api-host>/signin-dashboard` as its web redirect URI.
Configure only the basic `openid` and `profile` sign-in scopes; the dashboard
does not call Microsoft Graph. Require user assignment, assign the exact owner,
and grant consent for those scopes to that owner only. Store the app credential
in the existing hosting environment's Key Vault. Configure the API with:

- `Dashboard__TenantId`: the confirmed personal tenant ID.
- `Dashboard__OwnerObjectId`: the exact owner's object ID in that tenant.
- `Dashboard__ClientId`: the web application client ID.
- `Dashboard__ClientSecret`: a Key Vault reference to its client credential.
- `Dashboard__Enabled=true` after setup and state migration.

The release workflow also sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on
the API. App Service terminates TLS before forwarding to Kestrel; the dashboard
consumes only the final `X-Forwarded-Proto` hop before authentication, so its
OIDC callback remains HTTPS. It does not trust `X-Forwarded-Host` or forwarded
client IPs. This cloud forwarding switch assumes App Service's controlled
ingress; do not copy it to a directly exposed Kestrel host. Azure dashboard
startup fails if the switch is missing. See Microsoft's
[Linux proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer#forward-the-scheme-for-linux-and-non-iis-reverse-proxies).

Do not enable `Dashboard__AllowLocalSignIn` in production. That option only works
in Development on loopback, with the existing runtime guard rejecting Development
on Azure hosts. For local fictional-data checks, configure two synthetic GUIDs
for the tenant and owner, enable local sign-in, and use isolated Azurite tables.

Verify signed-out data requests return 401; another tenant/object cannot sign
in; the owner can read data; cookie writes without CSRF tokens return 400; and
machine bearer requests still work. On a phone, every card action is available
through the detail drawer and status selector. Drag-and-drop is optional.

## One authoritative migration

1. Capture existing configuration and tested packages privately. Provision the
   additive tables and include them in the API's snapshot inventory. Deploy the
   compatibility-capable release with opportunity authority and consumers off.
2. Pause and drain every writer: discovery, tracker, scoring, ingestion,
   Telegram, catalog worker/webhook, health delivery, notification retries, and local clients. Set the
   API write freeze and enable the operator-only maintenance interface. Verify
   business writes fail while authenticated reads remain available.
3. Export a new complete frozen snapshot. Do not reuse the earlier cleanup
   snapshot: scoring and notifications have continued since that operation.
4. Restore the snapshot into isolated local storage with the migration CLI.
   Export a new local frozen snapshot bound to that local target, preview and
   apply opportunity migration, and verify source counts, unknown properties,
   IDs, submissions, talks, suppression, and history. Rehearse full restoration
   for both schemas and repeat the migration to prove idempotency.
5. Preview and apply production migration using the production-bound frozen
   snapshot. The API archives it privately, checks every source ETag/checksum,
   creates each current/history pair atomically, and refuses unrelated target
   writes. The final readiness marker is written only after every source row is
   copied. An interruption resumes with the same immutable snapshot.
6. Verify the copy and retained references, then switch API authority once with
   `Storage__OpportunitiesEnabled=true`. Check authenticated readiness before
   enabling Functions `Pipeline__OpportunitiesEnabled`. Keep Events frozen as
   the recovery baseline; there are no dual writes.
7. Enable catalog sync, verify its validated generation, then enable catalog
   scoring and owner dashboard access. Review conservative legacy aliases and
   uncertain engagements through the catalog UI; they do not block activation.
8. Close maintenance administration and restore writers and notifications in
   dependency order. Verify a controlled real cycle, callback state/edit,
   `/status`, telemetry redaction, and the next scheduled notifications.

Example commands use an explicitly selected API scope and private paths:

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- export --api https://<api-host>/ --scope <api-scope> --output <private-snapshot.json>
dotnet run --project tools/SpeakerPipeline.Migrate -- migrate-opportunities --api https://<api-host>/ --scope <api-scope> --source <private-snapshot.json> --output <private-preview.json>
dotnet run --project tools/SpeakerPipeline.Migrate -- migrate-opportunities --api https://<api-host>/ --scope <api-scope> --source <private-snapshot.json> --output <private-apply.json> --apply
```

After new-schema writes begin, turning the authority flag off exposes a stale
baseline and is not a rollback. Recover with a migration-compatible package or
reviewed replay of the current snapshot/history. Preserve both old and new
schema recovery material outside Git.

## State and review behavior

New opportunities follow system verdicts until moved. User placement stays
pinned; later verdicts appear separately. Existing cards are migration-pinned
without pretending a person moved them. Legacy Pass becomes Not pursuing and
Skip becomes Dropped, with suppression retained independently. Automated
recommendations cannot establish permanent suppression.

Submitted records the owner's confirmation only. Real per-talk submission
records can separately supply submission evidence; the tracker never fabricates
them. Snooze preserves placement and scoring. Explicit Drop requires a reason
and sets permanent suppression until Restore. Feedback correction/retirement
preserves audit history while removing the superseded example from scoring.
Repeat-talk exceptions are explicit per-opportunity preferences.

Mutations use an exact ETag and idempotency key. Missing preconditions return
428, stale state returns 412, and callback replay returns its original result.
Scores carry the observed input revision; concurrent user placement survives,
while changed scoring inputs reject a stale score. Catalog generations and
calendar/context revisions are recorded separately from the opportunity's
transaction. See the [storage contract](architecture-table-storage.md).

## Verification

CI builds the entire solution, runs both deterministic agent eval suites and
unit/integration tests, starts isolated Azurite for real conditional Table
transactions and snapshot restoration, and compiles Bicep. Live model evaluation
is explicitly opt-in and uses synthetic examples without tracker writes.

The final v5 real-model calibration exercised five cases: direct match, wrong
audience, unrelated topic, new workshop, and international strong fit. It used
7,767 input tokens and 2,483 completion tokens (including 1,024 reasoning tokens).
Observed fit anchors were 10, 5, 1, 9, and 10 respectively; preparation effort was
2 for reuse and 8 for new workshop material. This is evidence for those cases,
not a claim that all future decisions are calibrated.

The current production cutover completed the isolated restore rehearsal,
opportunity migration, authority switch, and read-only catalog publication.
Two bounded scoring batches produced 36 validated catalog-backed decisions;
an unchanged snapshot reused all decisions with no model calls and confirmed
email delivery. See the [deployment record](../.azure/deployment-plan.md) for
release provenance and current gates.

The dedicated GitHub App and its installation subscribe to Push and retain
read-only access to exactly the two intended repositories. GitHub-delivered ping
and installation events returned HTTP 202 with SSL verification enabled. A real
push has not yet exercised the catalog reconciliation path.

The Telegram credential was rotated for the same bot: the replacement works,
the previous token returns 401, and the Key Vault reference resolves. Inbound
authentication checks passed. A real `/status` command exposed Telegram's
rejection of a null reply markup value. The fix passed main CI and deployed
successfully, with both hosts passing authenticated readiness. Two queued real
`/status` commands then succeeded. A controlled scoring run reused 36 decisions,
scored two new opportunities, and delivered three Telegram parts and one email
with completed durable receipts. Post-readiness telemetry showed no handler
failures or matches for the checked credential patterns. The owner verified
that Drop opened the reason picker and Cancel restored the buttons. Read-only
verification confirmed all 40 opportunities and 76 history records were
unchanged, the message version changed, the picker cleared, and its lock was
released. No real Drop was committed. Mutation and replay behavior are covered
by regression tests; this live check verified the picker and Cancel flow only.

Owner dashboard sign-in reached an administrator approval screen in the wrong
tenant. The owner confirmed personal-tenant authentication on unchanged hosting.
That configuration is now corrected, its Key Vault reference resolves, and both
hosts pass readiness; unrelated settings and machine API authentication were
preserved. The account-chooser release succeeded, and the public login redirect
uses the confirmed personal tenant and client with PKCE and an account chooser.
Transient API startup errors and probe timeouts recovered; their cause is not
established. Interactive owner sign-in remains unverified. Retirement of the
superseded dashboard registration and credential remains pending owner
acceptance. An actual source push and the first scheduled runs remain future
observations; GitHub configuration and authenticated delivery are verified.
Preserve any explicit pause until its own acceptance gate passes. No
organizer outreach, CFP submission, publishing, or source-repository writing is
part of this release.
