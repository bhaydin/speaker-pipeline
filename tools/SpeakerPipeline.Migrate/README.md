# SpeakerPipeline.Migrate

One-off seeding / migration console tool. It reads JSON in the **storage-entity
shape** (the same shape as [`samples/`](../../samples/)), maps each row to the
Core record shape, and upserts it through the pipeline **API** — the tool never
touches Storage directly (see [ADR 0001](../../docs/adr/0001-consumers-reach-data-through-the-api.md)).

Converting an external export into API calls is the tool's whole job, so the
storage-shape → Core mapping lives here in [`SeedModels.cs`](SeedModels.cs).

## Run

Start the API locally, then:

```bash
# dry run against the shipped fictional samples
dotnet run --project tools/SpeakerPipeline.Migrate -- --source samples --api http://localhost:5080/ --dry-run

# actually seed
dotnet run --project tools/SpeakerPipeline.Migrate -- --source samples --api http://localhost:5080/
```

| Flag | Default | Meaning |
|---|---|---|
| `--source` | `samples` | Directory holding `sample-talks.json`, `sample-events.json`, `sample-submissions.json` |
| `--api` | `http://localhost:5080/` | Base URL of the pipeline API |
| `--token` | `dev` | Bearer token (the dev API accepts `dev`) |
| `--dry-run` | off | Log what would happen without writing |

Talks are seeded first, then events, then submissions (submissions reference
both).

## Real data

Per the repo's hard rules, **no real pipeline data lives here.** To migrate the
actual tracker, export it to the storage-entity JSON shape locally and point
`--source` at that folder — outside the repo. The committed `samples/` are
fictional (Northwoods Tech Summit, Great Lakes Cloud Conf, …).
