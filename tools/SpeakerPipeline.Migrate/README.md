# SpeakerPipeline.Migrate

Operator CLI for private exports, conservative cleanup manifests, journaled
cleanup/restore, and validated imports. All data access goes through the API;
this project has no Storage dependency.

## Production cleanup

Use the existing production API origin and an operator account with the
`Pipeline.Maintenance` role. Run `az login` for that account first. Remote
commands require HTTPS and an explicit API scope; tokens are acquired with
`AzureCliCredential`, never passed on the command line or logged. Redirects
are disabled. Ordinary import calls also require `Pipeline.Access`.

Set these local PowerShell variables to the actual API, scope, and a private
directory **outside all Git working trees**. The CLI never overwrites output
files. Do not put these values or exported data in commits.

```powershell
$api = 'https://<existing-api-host>/'
$scope = 'api://<api-client-id>/.default'
$private = Join-Path $env:LOCALAPPDATA 'SpeakerPipeline/production-cutover'
```

Freeze business writes at the API, stop Functions/MCP writers and active
invocations, and disable email/Telegram delivery before taking the final
snapshot. The API snapshot records its freeze state. Cleanup apply rejects an
unfrozen snapshot or a changed target/row set. Initial inventory exports can
be taken before the freeze, but must be replaced with a fresh frozen snapshot
and manifest before applying anything.

```powershell
# Read all rows in all six tables, including unexpected properties/partitions.
dotnet run --project tools/SpeakerPipeline.Migrate -- export --api $api --scope $scope --output "$private/snapshot.json"

# Offline: classify the snapshot and write an immutable, checksummed manifest.
dotnet run --project tools/SpeakerPipeline.Migrate -- preview --api $api --scope $scope --source "$private/snapshot.json" --output "$private/manifest.json"

# Review manifest.json privately, then apply exactly that plan.
dotnet run --project tools/SpeakerPipeline.Migrate -- apply --api $api --scope $scope --source "$private/manifest.json" --snapshot "$private/snapshot.json" --output "$private/apply-result.json"

# Verify the result using a new snapshot while writers remain frozen.
dotnet run --project tools/SpeakerPipeline.Migrate -- export --api $api --scope $scope --output "$private/after.json"
```

The generated manifest deletes only exact fingerprints of the five fictional
sample events and three sample submissions embedded from `samples/`. Every
stored property must match. Modified/unknown properties cause retention for
review. A sample event with a retained submission is retained too.

All four canonical talks remain. Only an exact
`<placeholder-sharepoint-deck-url>` value on those talks is removed; other
talk fields, including unknown columns, are preserved. Topics, blackouts,
notification history/deduplication records, real events, and ambiguous records
are retained and listed for review. An automatic preview is deliberately not
permission to delete everything with a familiar name. Do not edit the generated
JSON: its checksum binds the API origin, complete snapshot, operation ID, changes,
and review list. For changed/ambiguous rows, prepare a private JSON array of
explicit decisions with `table`, `partitionKey`, `rowKey`, `disposition`
(`delete`, `retain`, or `repair`), and a concrete `reason`. A `repair` may only
remove the exact placeholder `DeckUrl` and supplies `propertiesToRemove: ["DeckUrl"]`.
The four canonical talks cannot be deleted. Every retained child submission
protects its parent event. Generate a new reviewed manifest before the first apply:

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- review --api $api --source "$private/reviewed-decisions.json" --snapshot "$private/snapshot.json" --output "$private/reviewed-manifest.json"
```

Use that reviewed manifest consistently for apply, retry, and restore. Do not
switch manifests after an operation has started.

The apply command runs the API's read-only plan validation before requesting
mutations; the API revalidates again while holding its operation lock.
The API owns the operation journal and saves all original data before its first
mutation. Deletes and replacements use ETags. If an apply is interrupted, rerun
the **same manifest and snapshot** with a new result filename; it resumes the
same operation ID. Do not regenerate the manifest to retry a partial operation.
Failed or interrupted commands leave a private result receipt when possible.
There is no cross-table transaction: inspect the journal before reopening writes.

## Recompute cleanup effects without running agents

Load counts and committed engagements are computed on every API context read;
there is no load cache to rebuild. Persisted conflict flags and earlier scoring
decisions may still reflect fictional engagements. Do not use the normal tracker
or scoring HTTP triggers for this step: they can change unrelated decisions and
send notifications.

Compare the frozen pre-cleanup and post-cleanup exports at one fixed UTC clock.
Supply the deployed tracker settings explicitly (the code defaults are 28 days
and threshold 2; verify the actual configuration). This command is entirely
offline and calls no models:

```powershell
$at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
dotnet run --project tools/SpeakerPipeline.Migrate -- recompute --api $api --source "$private/snapshot.json" --snapshot "$private/after.json" --at $at --prep-window-days 28 --prep-threshold 2 --output "$private/recompute-report.json" --repairs-output "$private/derived-repairs.json"
```

The report contains before/after context, exact changed scoring prompts and their
hashes, and review dispositions. It uses the same pure context assembler,
conflict evaluator, and scoring rubric as the application. This captures both
nearby commitments and global monthly preparation counts. Retained record changes
beyond the exact placeholder deck removal stop the comparison; a changing
baseline cannot be attributed to cleanup safely.

The separate repairs manifest proposes only boolean conflict fields whose
deterministic result changed because of cleanup. Preexisting unrelated flag drift
is retained for review. Review the manifest before applying it against `after.json`
through the ordinary journaled `apply` command. No category, priority, intent,
suppression, provenance, or notification record is changed. Roll back a derived
repair operation before rolling back the earlier cleanup operation.

Changed scoring prompts are **not** automatic permission to rescore. Skip,
DoNotResurface, committed/submitted/delivered/quarantined events, and events with
retained submissions are protected. Other changed candidates still require an
explicit reviewed allowlist: existing data cannot reliably distinguish a manual
decision from an older agent decision. If none are affected, no scoring is needed.
If review identifies candidates, obtain targeted `ScoreAsync` proposals separately
with the cleaned context; do not run the global scoring trigger or silently apply
recommendations. This CLI step performs no scoring or decision writes.

After reviewing concrete shadow output and resolving any manual intent, prepare a
private JSON array with `eventSlug`, `category`, `priority`, `nextAction`,
`decidedByAgent`, `reason`, and `outputProofSha256`. Compute the proof from the
actual private shadow-output file; the builder validates its format and binds it
to the manifest, but does not independently verify that file. `nextAction` holds
the reviewed rationale (at most 240 characters). Each decision must identify an
agent-marked candidate without suppression or a retained submission.

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- review-scoring --api $api --source "$private/reviewed-scoring-decisions.json" --snapshot "$private/after.json" --output "$private/scoring-repair-manifest.json"
```

This offline command creates a proposal for only the four approved fields:
`Category`, `Priority`, `NextAction`, and `DecidedByAgent`. It makes no model or API
calls. Supported outcomes are SubmitNow, Outreach, Monitor, and Pass; suppression
and committed decisions remain protected. Review the resulting manifest, then
use the existing `apply` command with that same frozen snapshot. The maintenance
journal supports retry and selective rollback without triggering notifications.

## Restore the touched rows

Keep writers frozen. Restore reads the original snapshot and completion state
from the API's journal; it does not overwrite unrelated rows. Changes made after
cleanup cause a conflict instead of being overwritten. Azure assigns new ETags
and timestamps; property values and types are restored from the snapshot.

```powershell
# Preview restore first; no business writes.
dotnet run --project tools/SpeakerPipeline.Migrate -- restore --api $api --scope $scope --source "$private/manifest.json" --snapshot "$private/snapshot.json" --output "$private/restore-preview.json"

# Apply that operation's restore. The same command is resumable with a new output.
dotnet run --project tools/SpeakerPipeline.Migrate -- restore --api $api --scope $scope --source "$private/manifest.json" --snapshot "$private/snapshot.json" --output "$private/restore-result.json" --apply
```

## Rehearse full backup recovery locally

Before production cleanup, restore the full snapshot into an isolated local API
backed by loopback Azurite, with business writes frozen. This is the only permitted
destination change. The API also verifies that its storage endpoint is actually
loopback; pointing a local API at cloud storage does not bypass target binding.
Use a fresh emulator data directory. A full restore creates missing entities only;
already restored matching entities allow a retry, while conflicting or extra rows
stop the restore. It never upserts over current data. A production recovery requires
the exact original API and storage target and an empty or matching destination.

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- restore-snapshot --api http://localhost:5080/ --source "$private/snapshot.json" --output "$private/rehearsal-preview.json"
dotnet run --project tools/SpeakerPipeline.Migrate -- restore-snapshot --api http://localhost:5080/ --source "$private/snapshot.json" --output "$private/rehearsal-result.json" --apply
dotnet run --project tools/SpeakerPipeline.Migrate -- export --api http://localhost:5080/ --output "$private/rehearsal-after.json"
```

Compare all six tables' keys and typed properties, including orphan submissions
and unknown columns, to the original snapshot. ETags, timestamps, snapshot IDs,
and the local destination intentionally differ. Retain the private rehearsal
receipt alongside the snapshot. `restore-snapshot` also previews by default;
`--apply` is required to write. It does not use the selective cleanup journal.

## Imports and local samples

`samples` and `import` both default to preview. Explicit `--source` and `--api`
are mandatory. All three files must exist; empty arrays are valid. JSON uses the
storage-entity shape in `SeedModels.cs`, with the existing filenames
`sample-talks.json`, `sample-events.json`, and `sample-submissions.json`.
Unknown fields, numeric/unknown enum values, invalid keys, duplicate keys,
invalid dates, and missing event/talk references fail before the first write.
Imports never silently default invalid enum values. References may resolve to
either the input batch or records already in the API.

The `samples` command is limited to an HTTP(S) loopback origin and is for a local
API/data store only. A missing scope uses the local `dev` bearer for imports and
the local `maintenance` bearer for admin commands. Local development
does not create another Azure environment.

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- samples --source samples --api http://localhost:5080/
dotnet run --project tools/SpeakerPipeline.Migrate -- samples --source samples --api http://localhost:5080/ --apply

# Real imports must be outside Git and cannot include known sample events,
# placeholder contacts, or placeholder deck/event URLs.
dotnet run --project tools/SpeakerPipeline.Migrate -- import --source "$private/import" --api $api --scope $scope
dotnet run --project tools/SpeakerPipeline.Migrate -- import --source "$private/import" --api $api --scope $scope --apply
```

Validated imports upsert talks, events, then submissions and stop on the first
HTTP failure. They are not the cleanup/restore mechanism and do not have the
cleanup journal; a failed import may be partial. Review input collisions before
applying, since upserts can update existing rows. Repeating the same input uses
the same keys. Disable discovery and notifications for a controlled initial import.

Exit codes: `0` success/preview, `1` invalid input or target, `2` request,
authentication, or filesystem failure. Output reports counts and operation IDs;
it does not print credentials, API response bodies, or entity content.
