# Deep flow reviews

Deep flow review is the agent-backed security stage after the deterministic
boundary inventory. Its review unit is a complete business flow: entry,
authentication and authorization decisions, state transitions, persistence, and
response. It covers session lifecycle, ownership and privilege escalation,
multi-step bypass, replay/idempotency, races, quota abuse, and invariants that
are not enforced by the data model.

`FlowReviewRunner` accepts a `FlowDefinition`, the derived `BoundaryInventory`,
data-model evidence, call-graph evidence, and the repository source files for the
path. The caller remains responsible for flow catalogue construction. It also
owns model selection; the runner uses the supplied `IReviewAgent` and does not
silently choose or downgrade a model.

The CLI accepts the same contract as JSON:

```text
quality flow review flow-request.json
```

It exits `0` for pass, `1` for fail, and `2` for undetermined or invalid input,
and prints the measured cost or its explicit unavailable status.

Each finding must have a zero-based ordered `flowPath` beginning at `entry` and
ending at `response` (or `external`), plus a `weakestPointIndex`. All
non-external path locations and the weakest point are validated against the
reviewed source. Agent-provided finding ids are discarded; Quality Studio
assigns the normal repository-stable fingerprint at the weakest source line and
merges it into `.quality/findings/state.json`.

An agent that cannot establish decisive behavior returns `undetermined` with a
reason. This is a persisted verdict, distinct from `pass`. Independently proven
findings can remain attached to an undetermined flow.

## Persistence, recency, and cost

Reports conform to
[`schemas/flow-review.v1.schema.json`](../schemas/flow-review.v1.schema.json) and
are atomically written below `.quality/flows/`. Provenance records:

- agent, effective model, run id, UTC review time;
- prompt id, version, and template hash;
- an input hash over the prompt, flow, entire boundary catalogue, data model,
  call graph, and exact source;
- a separate boundary-catalogue hash;
- input, output, cache, reasoning, and duration usage; and
- resolved cost and currency, or the explicit `usageUnavailable`,
  `unknownModel`, or `noPriceForDate` status.

The same operation is appended to `.quality/usage/` with kind
`deep-flow-security` and level `flow`. `EvaluateStalenessAsync` compares current
evidence and catalogue hashes with the recorded provenance. A change during the
agent run prevents the report from being written.

False-positive, waived, accepted, and open states remain on findings.
`findingCounts.falsePositive` makes the count visible in every new report; a
false positive is never removed merely to improve the scan result.

## Fixture proof

`FlowReviewRunnerTests` copies the fixture service under
`tests/AgentOrchestrator.CodeQuality.Tests/Fixtures/flow-review/` to a temporary
repository. It proves complete paths for a planted session-fixation weakness, a
horizontal ownership failure, and a replayable payment mutation. It also proves
that external identity-provider policy produces `undetermined`, per-flow token
and monetary cost is recorded, source/catalogue changes stale a result, and a
false-positive disposition is retained and counted on the next review.
