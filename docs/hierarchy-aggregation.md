# Hierarchy aggregation

Quality Studio derives a five-level tree:

```text
Project -> Module -> Namespace -> File -> Function
```

Each node retains the independently authored review-meta documents attached at
that exact level. Documents are keyed by review kind, so code, security, and
performance reviews can coexist on a file without refreshing or replacing one
another.

For every kind, a node exposes three states:

- `Direct` is the state of metadata at that exact level. It is `NotReviewed`
  when no such document exists; absence is information, not a failed review.
- `Descendants` rolls up child evidence. `Stale` wins over `Current`, while
  `NotReviewed` is returned only when the entire descendant set has no review.
- `Overall` applies the same rule to direct and descendant evidence. Thus an
  unreviewed module can still summarize current file reviews without falsely
  claiming that a module-level review exists: `Overall` is current and `Direct`
  remains `NotReviewed`.

Aggregation never combines kinds, and state aggregation never combines grades. The CLI uses
`Overall` for its compact module lines; consumers needing exact-level truth must display `Direct` as
well.

## Grade projection

A directory or other container has a grade only once an aggregate review graded it. Until then the
API adds a **projection** per kind: the mean of its descendant files' grades, each weighted by its line
count (by size ÷ 40 when the line count is unknown). It records how many of the container's files were
graded (`gradedFiles` of `files`), because a projection over 3 of 200 files says little. Ungraded files
are counted, not scored — they neither pull the projection to zero nor pass as healthy.

The projection is labelled as such everywhere: `basis: "size-weighted-file-grades"` in the API, and a
muted, italic `≈C · 79 · projection` in the UI. It never feeds `Direct`, `Descendants` or `Overall`,
never replaces a real grade, and disappears once the container has its own review.

Run `quality scan <repository> --by-level` for one summary line per module. For
example:

```text
project QualityStudio
  module AgentOrchestrator.CodeQuality code=current security=stale performance=not-reviewed
```
