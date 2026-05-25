# src/

Placeholder for the .NET solution that lands in **Phase 2**.

Planned shape:

```text
src/
  SpeakerPipeline.sln
  SpeakerPipeline.Agents/                  — MAF agent host
    SpeakerPipeline.Agents.csproj
  SpeakerPipeline.Discovery.Sessionize/    — first discovery agent
  SpeakerPipeline.Maintenance/             — tracker maintenance agent
  SpeakerPipeline.Domain/                  — shared TableEntity contracts
  SpeakerPipeline.Tests/                   — xUnit tests
```

See [../docs/architecture-overview.md](../docs/architecture-overview.md) for
the Phase 2 build order and [../docs/pipeline_table_storage_schema.md](../docs/pipeline_table_storage_schema.md)
for the persistence contract.
