# SpeakerPipeline.Agents.Scoring.Evals

This is the **eval suite** for the scoring agent. It is intentionally a
separate project from `SpeakerPipeline.Agents.Scoring.Tests` because evals
have a different purpose, cadence, and failure semantics from unit tests.

## What lives here

- `Goldens/goldens.json` — the canonical golden cases covering
  each lane, each `Recommendation` value, urgency, congestion, and the
  two known edge cases (high-fit-bad-timing and low-fit-easy-win).
  Current-event availability feedback is also checked with an empty calendar.
- `ScoringAgentEvalRunner.cs` — the xUnit harness that loads the goldens,
  runs the scoring agent against each, and writes `evals-report.json` as a
  workflow artifact.
- `HeuristicChatClient.cs` — a deterministic `IChatClient` that approximates
  the rubric in `ScoringRubric.SystemPrompt`. Lets the eval suite run
  hermetically in CI without burning model tokens. When a real model is
  wired in Phase 3+, swap this for the real client (see "Plugging in a real
  model" below).

## When to add a golden

Add one any time you change scoring-agent behaviour:

- **New `Recommendation` value or new rubric factor.** Add at least one
  golden that exercises it. If the new value should be reachable from
  multiple paths, add one golden per path.
- **New lane** (we have four; if a fifth ever lands). Add a golden whose
  `FocusFit` lists that lane and whose expected talk slug is set.
- **New decision branch** in the agent code that isn't already covered.
- **New edge case** the rubric should handle.

## When to update an existing golden

Update only when the **rubric itself** changes — not when the model output
drifts. If the model starts returning a different `Recommendation` for the
same inputs and the rubric hasn't changed, that's a regression, not a
golden update.

If you do change the rubric, bump `rubricVersion` in `goldens.json` and
note the rationale in the PR description.

## When **not** to add a golden

- Pure refactors that don't change agent output.
- New transport, hosting, or DI wiring — those are unit tests.
- Bug fixes in code that the existing goldens already cover.

## Running locally

```bash
dotnet test tests/SpeakerPipeline.Agents.Scoring.Evals
```

The test fails if any golden fails. `evals-report.json` is written to the
test bin directory and (in CI) published as a workflow artifact.

## Plugging in a real model

The catalog calibration suite uses the real configured Foundry provider only
when explicitly enabled. Set `SCORING_EVALS_LIVE=1`,
`ScoringAgent__Endpoint`, `ScoringAgent__ModelName`, and an explicit deployment-tenant
`SCORING_EVALS_TENANT_ID`, then run
`dotnet test tests/SpeakerPipeline.Agents.Scoring.Evals --filter CatalogLiveEvaluation`.
Authentication uses `AzureCliCredential` restricted to that tenant; do not put secrets in fixtures.
The suite makes exactly five requests with 16,000-character input and
4096-completion-token caps per request (including reasoning). It never posts decisions or contacts the
production tracker. Inspect `catalog-live-evals-report.json` for actual model
responses and the model/rubric version. It checks direct versus wrong-audience
and unrelated fit, increasing effort for new work, and international eligibility.
Ordinary CI skips this billed suite and continues to use deterministic tests.

The separate `FeedbackLiveEvaluation` suite runs four synthetic paired cases against
the same configured deployment: a direct catalog match, explicit unavailability for
that event, unrelated topic feedback, and a different event's excessive travel burden.
Run it with the same opt-in environment and `--filter FeedbackLiveEvaluation`.
It checks an appropriate timing verdict change with grounded rationale, canonical
talk/version IDs, ignored unrelated feedback, and no blanket travel veto. Its report,
`feedback-live-evals-report.json`, records actual input, completion, and reasoning
usage plus the four-call/16,384-completion-token ceiling. The cases make no tracker
calls or writes and do not change the rubric. A passing run is bounded evidence for
these cases, not a claim of statistical stability or general preference learning.

Keep the heuristic client as the CI default until model output is stable
enough to be the baseline. **Never** make CI depend on a model call billed
to a user account — the eval suite's primary job is regression detection,
which the heuristic does for free.

## Reading the report

`evals-report.json` looks like this:

```json
{
  "runId": "…",
  "rubricVersion": "v1",
  "totalCases": 12,
  "passed": 12,
  "failed": 0,
  "cases": [
    {
      "caseId": "clear-submit-now-hometown-agentops",
      "passed": true,
      "expected": "SubmitNow",
      "actual": "SubmitNow",
      "fitScore": 9,
      "effortScore": 3,
      "confidenceScore": 8,
      "rationale": "…"
    }
  ]
}
```

Failures include a `failureReason` string. The two common kinds are
**recommendation drift** (the agent picked a different `Recommendation`)
and **missing rationale keywords** (the rationale doesn't mention the
rubric factor the case is exercising). Both are worth investigating before
they become noise.
