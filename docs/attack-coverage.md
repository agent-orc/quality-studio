# Attack coverage matrix

Quality Studio projects the boundary inventory into an attack-coverage matrix.
Rows are derived boundaries and columns are applicable attack-catalogue entries.
An empty cell is not a pass: every applicable pair is projected as one of
`pass`, `finding`, `notApplicable`, or `notYetChecked`.

The API and dashboard default work list is ordered by the boundaries with the
most covered-code changes and then by the oldest verdict. The dashboard keeps
verdict age and confidence visible on each cell. Opening a cell shows its
evidence, exact provenance, staleness reasons, and complete assessment
trajectory.

## Catalogue and precedence

The built-in catalogue is
[`src/AgentOrchestrator.CodeQuality/catalogues/attack-catalogue.v1.json`](../src/AgentOrchestrator.CodeQuality/catalogues/attack-catalogue.v1.json)
and conforms to
[`schemas/attack-catalogue.v1.schema.json`](../schemas/attack-catalogue.v1.schema.json).
It is seeded from the OWASP API Security Top 10, relevant OWASP Top 10 entries,
and session/authentication attack classes. Every entry has an id, its own
version, description, boundary-kind/direction predicate, evidence requirements,
severity frame, and optional deterministic boundary rules.
Broad OWASP classes use deterministic rules only to establish findings; a clean
subset is not treated as proof that the whole class passes. Narrow
`QS-CONTROL-*` entries mark their mechanical checks as conclusive in both
directions, which is what allows a clean sensor result to become a pass.

Catalogue precedence mirrors review inputs:

1. the embedded repository-owned catalogue;
2. `<global-inputs-directory>/attack-catalogue.json`;
3. `<repository>/.quality/attacks/catalogue.json`.

A later entry with the same id replaces the earlier entry; new ids extend the
catalogue. Disabled project entries remove an inherited entry from the
effective matrix. The effective catalogue version records every contributing
source version, while drift is calculated from the individual effective entry
hash. Consequently, changing one entry marks only cells for that attack stale.

## Ledger and provenance

Judgements append as JSON Lines to:

```text
.quality/attacks/coverage-ledger.jsonl
```

The ledger is never rewritten by a re-check. Each observation records:

- verdict, reasoning, evidence, and finding lifecycle id/fingerprint;
- the exact deterministic sensor input;
- reviewer agent, model, and thinking level;
- prompt version/hash and effective catalogue version/entry hash;
- boundary-definition and endpoint-scoped covered-code hashes;
- input, output, cached, and reasoning token counts;
- timestamp, commit, optional commit range, and assessment id.

Independent judgements over the same exact input use the same assessment id.
The current cell is a projection of the newest assessment, while every prior
assessment remains available as history. A `pass → finding → pass` sequence and
the commit ranges between those states are therefore preserved.

`notYetChecked` is projected when no assessment exists or when a high/critical
cell has fewer than two independent judgements. It is never written as a
pretend verdict. Agent/model/thinking values are supplied by the routed review
caller; the coverage store does not choose or silently downgrade them.

## Staleness and uncertainty

Staleness is multi-valued. A cell can report any combination of:

- `boundaryChanged` — derived boundary facts changed;
- `codeChanged` — only the registration/handler code covered by the row changed;
- `catalogueChanged` — the applicable entry changed;
- `promptChanged` — the judgement prompt changed.

Age in days is independent of staleness and remains visible when all hashes are
current.

High and critical agent-judged cells require two distinct
agent/model/thinking-level identities. Contradicting independent verdicts set
`disagreement`, retain the conservative visible result, lower confidence, and
raise `needsHumanAttention`. They are not averaged. A deterministic boundary
sensor observation needs no duplicate model judgement and overrides a
contradicting agent claim; the override remains visible in the cell.

## API and export

The repository-aware endpoints are:

```text
GET  /api/repos/{repoId}/security/attack-coverage?path=src/QualityStudio.Api
POST /api/repos/{repoId}/security/attack-coverage/judgements?path=src/QualityStudio.Api
```

Legacy-default routes omit `/repos/{repoId}`. GET records deterministic checks
that have never run, but preserves stale prior results. An explicit
`recheck=true` query re-runs changed deterministic cells. POST captures hashes
from the current repository and catalogue rather than trusting caller-supplied
provenance. A finding verdict must link to the finding lifecycle.

The dashboard's **Export JSON** action exports the same reporting-ready matrix
contract returned by GET, including evidence and history.
