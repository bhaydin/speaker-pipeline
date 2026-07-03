# 0001. Consumers reach data through the API, not the Storage account

- **Status:** Accepted
- **Date:** 2026-07-03
- **Deciders:** Brian Haydin

## Context

Milestone 1 of the [build plan](../BUILD_PLAN.md) adds an MCP server so any
AI surface (Claude, Copilot) can inject topic ideas and drive pipeline
commands. Section 5 of that plan describes the MCP server as "remote MCP over
Table Storage," which reads as if the MCP host should bind the storage account
directly.

That phrasing collides with the standing architecture rule in
[CLAUDE.md](../../CLAUDE.md): `SpeakerPipeline.Storage` is internal to
`SpeakerPipeline.Api`, and every other consumer reaches data through an
abstraction layer (the REST API), never the storage account directly. The rule
exists so the data contract has exactly one owner — versioning, validation,
auth, and observability live in one place instead of being re-implemented by
each consumer.

The MCP server is a genuine test of the rule: it is small, it could be
co-located with the API, and binding the tables directly would shave a network
hop. The question a reviewer would reasonably ask is "why not just bind Storage
here?"

## Decision

The MCP server — and every future consumer — reaches pipeline data through
`ISpeakerPipelineApiClient` over HTTP against `SpeakerPipeline.Api`. No consumer
other than the API references `SpeakerPipeline.Storage`. The tables that the six
v1 MCP tools need (`Topics`, `Blackouts`, and the pipeline-update transitions)
are exposed as `/v1` API endpoints; the MCP tools are thin adapters over the API
client.

## Consequences

- **Easier:** one owner for the data contract. Validation (FluentValidation),
  auth (Entra + managed identity), idempotency, and OpenTelemetry are enforced
  once in the API and inherited by every consumer. The MCP host stays a thin,
  swappable adapter.
- **Easier:** the reference graph enforces the rule at compile time. The MCP
  project references `Core` + the extracted `SpeakerPipeline.Client`, never
  `Storage`.
- **Harder:** one extra network hop and the operational cost of keeping the API
  reachable from the MCP host. At this volume the latency is irrelevant.
- **Locked in:** new data surfaces must land as API endpoints first, then get a
  tool/agent adapter. That ordering is intentional.
- **Reversing** would mean giving the MCP host a `Storage` reference and a
  managed-identity role assignment on the tables — a deliberate exception that
  would supersede this ADR, not a quiet refactor.

## Alternatives considered

- **MCP host binds Table Storage directly.** Fewer hops, but it forks the data
  contract: validation and auth would have to be duplicated in the MCP host, and
  the compiler-enforced boundary would be broken. Rejected.
- **Co-locate MCP tools inside the API process** (same deployable, in-process
  calls). Collapses the hop but couples the MCP transport lifecycle to the API's
  hosting decision (still deferred to Phase 3) and muddies the "API is a plain
  REST surface" story. Rejected for now; revisit if latency ever matters.

## References

- [CLAUDE.md](../../CLAUDE.md) — "API as the only data boundary," hard rule #6.
- [docs/BUILD_PLAN.md](../BUILD_PLAN.md) — §5 MCP server, §7 Milestone 1.
- [docs/architecture-table-storage.md](../architecture-table-storage.md) — table schema.
