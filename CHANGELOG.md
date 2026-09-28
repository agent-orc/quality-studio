# Quality Studio change history

Product-level history. The rule library keeps its own in [`rules/CHANGELOG.md`](rules/CHANGELOG.md).

## Unreleased

### Fixed — Claude usage is counted and priced in full; estimates learn only from their own route (QS-110)

The 2026-09-28 evaluation against `agent-studio-dev` showed USD 12.50 in the ledger for Claude
reviews that cost USD 22.53, and a preflight of USD 56.59 for a run that cost USD 10.61 (defects
D5–D6). CodingAgentRunner 0.7.0 drops Claude's `cache_creation_input_tokens`, reports Claude's
`input_tokens` (which excludes cache reads and writes) as if it were all input, and the ledger then
subtracted the cache reads from that fresh input a second time.

- The review agent reads Claude's usage from the raw `result` frames, including failed turns.
  `tokens.inputTokens` is now all billed input (fresh + cache reads + cache writes) for every CLI,
  and the new `tokens.cacheWriteInputTokens` holds the cache writes.
- One pricing split (`UsageLedger.ToPricingUsage`) prices fresh input, cache reads, cache writes and
  output at their own catalogue tariffs for the ledger, the running cost cap, and flow reviews.
  Token caps count all input.
- Usage ledger schema version 4 (`schemas/usage-ledger.v4.schema.json`) adds
  `tokens.cacheWriteInputTokens` and `promptCharacters`. Older lines are never rewritten: when read,
  pre-v4 Claude entries (the 2026-09-28 run among them) count their cache reads as input, are
  re-priced, and are flagged `priceAccuracy: "underPriced"`, a lower bound because their cache
  writes were never recorded. `GET /api/usage` reports `underPricedRuns` and
  `cacheWriteInputTokens`; the usage history marks such entries as "at least".
- The preflight estimate uses only history of the same CLI and model, never another route's, and
  never under-priced entries. With prompt sizes recorded, it scales fresh input, cache reads,
  cache writes and output per prompt character (`history-prompt-ratio`); older matching history
  gives the mean per operation (`history-per-operation`); without matching history it falls back
  to prompt characters / 4 with a 20% output ratio (`prompt-size`). The estimate carries its
  `basis`, which the preflight sheet shows.

### Changed — the studio no longer writes into the checkout it analyses (QS-102)

Everything a run generates now lives in a per-project **data root** outside the analysed working
copy: `%LOCALAPPDATA%\QualityStudio\projects\<project-key>\` by default, overridable with
`QualityStudio:DataRoot` or `QUALITY_STUDIO_DATA_ROOT`. The container image and `docker-compose.yml`
point it at the `/data` volume that was already reserved for this.

Moved out of the checkout: review sidecars, `findings/`, `reports/`, `usage/`, `boundaries/`,
`coverage/`, `changes/`, `flows/`, `runs/` and the attack coverage ledger. Author-owned inputs stay
where they are and are still versioned — `.quality/scope.json`, `.quality/inputs/`,
`.quality/rules/overrides.json`, `.quality/security/` and `.quality/attacks/catalogue.json` — as does
`.quality/preflight/`, which an external analyzer writes under the working directory it runs in.

Review sidecars changed shape as well as location: instead of a `.quality/reviews` folder beside
every reviewed file, one `reviews/` lane in the data root mirrors the subject's directory.

Why: this repository is both the integration checkout of the QS project in Agent Studio and a
project the studio analyses. Every run left the working tree dirty, Agent Studio refuses to
fast-forward a dirty checkout, and QS-85 failed on that on 2026-08-27. Committing 139 generated
files on 2026-09-06 unblocked nine deliveries but was a workaround, not the rule.

### Added

- `quality migrate-data [path] [--dry-run]` moves an existing checkout's generated `.quality` data
  to its data root. Idempotent, refuses to overwrite what the data root already holds, and leaves
  inputs alone.
- The API warns at startup when a registered project root still carries generated studio data or has
  uncommitted changes below a `.quality` folder — the exact condition that blocks integration.

### Fixed

- The live report no longer grades files that are absent from the checked-out branch. Sidecars used
  to be committed, so a branch switch changed which of them existed; the data root is shared across
  branches, and without this a branch that deleted a file would still be graded on it.

### Removed

- Monthly usage ledgers and `.quality/reports/runs/*.json` are no longer versioned. `.gitignore`
  ignores everything under `.quality` and names the author-owned inputs back in. A report meant to
  be shared is exported deliberately with `quality report --output`.
- `.gitattributes` held one rule, `merge=union` for the in-tree usage ledgers. Those ledgers are no
  longer in the tree, so the file is gone.

### Upgrading

`.gitignore` does not untrack a file that is already committed, so an existing checkout needs one
migration and one commit:

```shell
dotnet run --project src/quality-cli -- migrate-data . --dry-run
dotnet run --project src/quality-cli -- migrate-data .
git commit -am "chore(quality): move generated studio data out of the checkout"
```

Two known consequences, both from features that read sidecars out of Git history and therefore can
only see the layout that history was written in:

- The score trend in `quality report` keeps every point up to the migration commit and gains none
  after it. Live grades are read from the data root and are unaffected.
- A change review over a range whose endpoints are both after the migration reports no agent-grade
  movement. Everything it derives from the diff — touched units, boundary and coverage facts,
  evidence economy — is unaffected, and a range spanning older commits still reads their sidecars.

See [`docs/data-root.md`](docs/data-root.md).
