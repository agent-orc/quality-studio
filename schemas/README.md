# Schemas

JSON Schema draft 2020-12 artifacts for every document Quality Studio writes.

## Canonical domain

Every `$id` uses `https://agent-orchestrator.dev/quality/schemas/<name>.schema.json`.
That is the product URL in the repository README, and it is the only form current
writers emit.

Twelve schemas and their C# constants previously used
`https://quality.studio/schemas/<name>.schema.json`. Both URLs identify the same
document. The old form is an accepted alias: the affected schemas list both values
for a document's own `$schema` property, and the two contracts that reject a
mismatched schema id — the quality finding envelope and change-review evidence —
accept it explicitly. Artifacts already committed under `.quality/` keep the value
their producing run wrote; they are not rewritten.

The schemas are not published under that URL today. They live only in this
repository, and the copy here is authoritative.

## Versions

`review-meta` is the only document with more than one live version. Writers emit
v3; readers accept v1, v2, and v3. The version table, the per-field semantics, and
the compatibility rule are in
[`../docs/concept.md`](../docs/concept.md#review-meta-schema-v3).

`quality-report.v1` and `quality-run-report.v1` carry `aggregateScore` and
`aggregateBand`. The older `score` and `grade` properties hold the same values and
are marked deprecated; see [`../docs/quality-reports.md`](../docs/quality-reports.md).
