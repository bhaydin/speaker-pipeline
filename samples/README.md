# Fictional samples

These fixtures exercise the persistence contract and local import tooling:

- `sample-events.json`: five fictional event records.
- `sample-submissions.json`: three fictional submissions referencing those events.
- `sample-talks.json`: four canonical talk examples with placeholder deck links.

The arrays use **storage-entity shape** (`PartitionKey`, `RowKey`, and stored
property names), documented in the [schema](../docs/architecture-table-storage.md).
The API uses Core record names such as `Slug` and `EventSlug`; the
[migration CLI](../tools/SpeakerPipeline.Migrate/README.md) validates and maps the
fixture shape before calling the API. OpenAPI is available on local Development
hosts at `/openapi/v1.json`.

## Local use only

Use a local API backed by isolated loopback Azurite. The CLI previews by default;
add `--apply` only when intentionally populating that local store.

```powershell
dotnet run --project tools/SpeakerPipeline.Migrate -- samples --source samples --api http://localhost:5080/
```

`samples` refuses remote destinations. The old `scripts/seed-tables.ps1` entrypoint
is retired. Never import fixtures into production or remove useful fixtures from
the repository to clean deployed data. Use the [production runbook](../docs/production-rollout.md)
and [deployment plan](../.azure/deployment-plan.md) for that operation.

## Fixture rules

- Keep events and contacts fictional, using `example.org` and placeholders.
- Keep enum names aligned with `SpeakerPipeline.Core`. CI parses fixture JSON;
  migration tests also exercise strict mappings and cleanup fingerprints.
- Keep real CFP data, contacts, exports, snapshots, and manifests outside Git.
- Treat fixture changes as cleanup-classifier changes: the CLI embeds these
  samples as exact fingerprints. Modified live rows are retained for review.
- Production cleanup preserves the four talks and removes only their exact
  placeholder deck URLs; fictional event/submission records are separate.
