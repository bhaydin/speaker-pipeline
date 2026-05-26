# Samples

Sanitized JSON that exercises the persistence schema. Two audiences:

1. **The `scripts/seed-tables.ps1` seeding flow** (Phase 2+). The arrays in
   `sample-events.json`, `sample-submissions.json`, and `sample-talks.json`
   are shaped as Azure Table Storage entities — `PartitionKey`, `RowKey`,
   and the property names defined in [docs/architecture-table-storage.md](../docs/architecture-table-storage.md).
2. **Anyone reading the repo to understand the data.** They cross-reference
   the schema doc one-to-one.

The shape is the **storage entity**, not the API request body. The Core
model field names (e.g. `Slug`, `EventSlug`) map onto Table Storage keys
(`RowKey`, `PartitionKey`) inside `SpeakerPipeline.Storage.Mapping`. When
you POST to the API, send the Core record shape — see the API endpoints in
[../src/SpeakerPipeline.Api/](../src/SpeakerPipeline.Api/) or the
generated OpenAPI spec at `/openapi/v1.json`.

## Rules

- **Fictional only.** Event names, organizers, speakers — all invented.
- **`example.org` for any hostname.** RFC 2606 reserved for documentation.
- **`<placeholder-…>` for anything that would otherwise be a real value.**
- **Enum values match `SpeakerPipeline.Core`.** Keep them in sync — CI
  validates that the JSON parses and that enum string values are within the
  allowed sets.
- **No real CFP data.** Even if it's public. The repo is a public reference;
  treat every commit as if a competitor will read it.
