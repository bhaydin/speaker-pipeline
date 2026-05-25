# Architecture Decision Records

Non-obvious architectural choices live here, one file per decision, in the order they were made. The goal is to make future-Brian (and contributors) able to reconstruct *why* a thing is the way it is without a Slack archaeology trip.

## When to write an ADR

Write one when a choice meets any of these:

- It commits the project to a technology or pattern that would be painful to reverse.
- It deviates from the default Microsoft-first stack bias declared in [CLAUDE.md](../../CLAUDE.md).
- A reviewer could reasonably ask "why didn't you just use X?" — answer it in advance.
- It's the kind of thing you would want to cite from a conference talk.

Don't write one for routine fixes, refactors, doc tweaks, or anything that would normally fit in a commit message.

## Filename convention

```
NNNN-short-kebab-case-title.md
```

- `NNNN` is a zero-padded sequence number, monotonically increasing (`0001`, `0002`, …).
- Title is short, kebab-case, and reads like a decision (`use-table-storage-not-cosmos`, not `database`).
- Once an ADR is committed and merged, its number is permanent. Superseding decisions get new numbers and link back.

## Status lifecycle

- **Proposed** — under discussion in a PR.
- **Accepted** — merged into `main`.
- **Superseded by NNNN** — replaced by a newer ADR. Don't delete the old one; the trail matters.
- **Deprecated** — no longer applies but wasn't replaced.

## Template

Copy this for new ADRs:

```markdown
# NNNN. Title in sentence case

- **Status:** Proposed | Accepted | Superseded by NNNN | Deprecated
- **Date:** YYYY-MM-DD
- **Deciders:** Brian Haydin (+ any co-deciders)

## Context

What's the situation? What forces are in play? Constraints, requirements,
deadlines. Keep this factual.

## Decision

What did we decide. One or two sentences, declarative.

## Consequences

What gets easier. What gets harder. What we're locked into. What we'd
have to do to reverse this.

## Alternatives considered

Two or three. For each: one sentence on what it is, one on why we
didn't pick it.

## References

Links to related ADRs, docs, issues, or external material.
```

## Index

_(No ADRs yet. The first one will likely be `0001-use-azure-table-storage.md` once Phase 2 begins.)_
