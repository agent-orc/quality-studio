# The data root

Quality Studio analyses a Git checkout. It does not file its results in one.

Everything a run generates — review sidecars, the finding-state ledger, run reports, the token
ledger, the boundary inventory, coverage snapshots, change reviews, flow reports and the resumable
run journals — is written to a **data root** outside the analysed checkout, one directory per
project. The checkout is read-only for the studio apart from the deliberate exceptions named below.

## Why

`C:\Projects\quality-studio` is both the registered integration checkout of the QS project in Agent
Studio and a project the studio analyses. Every run wrote into it, so the working tree was never
clean. Agent Studio refuses to fast-forward a checkout with uncommitted changes, and on 2026-08-27
QS-85 failed with exactly that:

```
Integration working tree has uncommitted changes; refusing to fast-forward it from origin.
Dirty files: .quality/boundaries/inventory.json, .quality/coverage/, .quality/reports/, …
```

On 2026-09-06 an operator committed 139 generated files to unblock nine reviewed deliveries. That
was a workaround. Generated data is not repository history, and a tool that dirties the tree it is
measuring cannot be run against its own repository without the two purposes fighting.

## Where

| | |
| --- | --- |
| Default base | `%LOCALAPPDATA%\QualityStudio` on Windows, `$XDG_DATA_HOME`/`~/.local/share/QualityStudio` elsewhere |
| Override, per host | `QualityStudio:DataRoot` (for example `QualityStudio__DataRoot=/data`) |
| Override, per process | `QUALITY_STUDIO_DATA_ROOT` |
| Per project | `<base>/projects/<project-key>/` |

The `quality` CLI has no ASP.NET configuration, so it reads both environment variables and prefers
`QUALITY_STUDIO_DATA_ROOT`. That matters because the API's own startup warning tells an operator to
run `quality migrate-data`: in a container the image sets only `QualityStudio__DataRoot`, and a CLI
that ignored it would migrate the data to a container-local path the host never reads and the next
container replacement would delete.

The project key is a readable slug of the checkout's directory name plus twelve hex characters of a
digest of its canonical absolute path — `quality-studio-3f1c9a02b7d4`, for instance.

**The path is the identity, not the Git remote.** Two working copies of the same remote — a second
clone, a Git worktree, a release branch checked out beside `main` — are separately analysed projects
with their own findings and their own review history. Keying on the remote would merge them into one
ledger and make each look stale to the other.

Inside a project directory the layout is the one the `.quality` folder used to have, minus the
`.quality` segment:

```
<base>/projects/<project-key>/
  findings/state.json
  reports/runs/*.json
  reports/pins.json
  usage/<yyyy-MM>.jsonl
  reviews/<mirrored subject directory>/<lane>/<level>.<hash>.review-meta.<kind>.json
  boundaries/inventory.json
  coverage/coverage.json
  changes/<commit>.json
  flows/<hash>.flow-review.json
  runs/<run-id>/…
  attacks/coverage-ledger.jsonl
```

Review sidecars are the one family whose layout changed shape. They used to sit in a `.quality`
folder next to each reviewed file, scattered across the tree. They now live under a single
`reviews/` lane that mirrors the subject's directory, so one folder holds every sidecar of a project
while a sidecar is still attributable to its folder without opening it.

## What stays in the checkout

Author-owned inputs. These are hand-written configuration of the analysed project, they are meant to
be reviewed and versioned with it, and the studio only ever reads them:

- `.quality/scope.json` — which paths are in scope
- `.quality/inputs/` — repository guidelines resolved into review prompts
- `.quality/rules/overrides.json` — the project's rule-catalogue overrides
- `.quality/security/gitleaks.toml`, `.quality/security/gitleaks.baseline.json`
- `.quality/attacks/catalogue.json` — the project's attack catalogue

One generated family also stays: `.quality/preflight/`. An external analyzer (eslint, tsc, Roslyn)
writes its report there, and the analyzer command confines that output path to the checkout it runs
in. Relaxing a path-confinement guard to gain tidiness is a bad trade, so preflight reports stay
where the tool puts them and are ignored by Git.

## What is versioned

**Nothing the studio generates.** `.gitignore` ignores everything under `.quality` and names the
author-owned inputs back in. This reverses the earlier decision that monthly usage ledgers and
`.quality/reports/runs/*.json` were "durable repository history": they are reproducible outputs of a
run, they change on every run, and keeping them in the tree is what blocked integration.

When a report is genuinely meant to be shared — a gate result on a pull request, a snapshot for a
review — export it deliberately:

```powershell
quality report . --format markdown --output docs/reports/2026-09-quality.md
```

An explicit export lands where the author chose, is reviewed like any other change, and does not
reappear on the next run.

One set of already-committed files is kept on purpose. The twenty change-set artifacts under
`.quality/changes/` are the measured sample behind the 72.48% evidence-reduction figure in
[`change-reviews.md`](change-reviews.md); they are frozen evidence for a published claim, not live
studio output. Git does not untrack a committed file because a rule now ignores it, so they stay
where they are and the ignore rule simply stops new ones joining them. Anyone who moves that sample
should move the measurement with it.

Two consequences are worth stating plainly. Both come from features that read sidecars out of **Git
history**, which can only ever show the layout that history was written in:

- The score trend in `quality report` keeps every point up to the commit that migrated sidecars out
  of the tree and gains no new ones after it. Live grades are read from the data root and are
  unaffected.
- A change review (`quality diff`) reports no agent-grade movement when both ends of the range are
  after that commit. Everything it derives from the diff itself — touched units, boundary and
  coverage facts, evidence economy — is unaffected, and a range spanning older commits still reads
  their sidecars.

Neither is fixed by keeping sidecars in the tree; a history-shaped feature needs a history-shaped
source, and the run reports under `reports/runs/` are the candidate whenever someone decides to
rebuild these on the data root instead.

## Migrating an existing checkout

```powershell
quality migrate-data C:\Projects\quality-studio --dry-run   # report what would move
quality migrate-data C:\Projects\quality-studio             # move it
```

The migration moves the generated families to the project's data root, leaves inputs alone, removes
the `.quality` shells that are left empty, and refuses to overwrite an artefact the data root
already holds rather than deciding for you which copy is real. It is idempotent; a second run moves
nothing.

It is a command an operator runs, not something the host does at startup, because it deletes files
from a working tree that still tracks them. `.gitignore` does not untrack a file that is already
committed, so after the first migration:

```
git status                     # the 139 generated files now show as deleted
git commit -am "chore(quality): move generated studio data out of the checkout"
```

That is the one commit that makes the tree clean. After it, a full studio run leaves `git status`
clean.

## The startup check

The API inspects every registered project root while it starts and warns — it never refuses to
start — when a checkout still carries generated studio data or when Git reports uncommitted changes
below a `.quality` folder:

```
warn: QualityStudio.Api.CheckoutCleanlinessCheck[1620]
      Repository 'default' at C:\Projects\quality-studio: 3 generated artefact(s) still live in the
      checkout (.quality/findings, .quality/reports, .quality/usage). Run
      'quality migrate-data "C:\Projects\quality-studio"' to move them to the project's data root,
      then commit the deletion once.
```

The studio no longer writes into a checkout, so anything this reports is either data from before the
data root existed or a writer that is not the studio. Both are worth naming at startup rather than
discovering when an integration fails.

## Backing it up, moving it, throwing it away

The data root is per user and per machine. It holds no source code — only findings, grades, ledgers
and journals that a re-review can regenerate, at the cost of the model spend the token ledger
records. Back it up if that spend matters; delete a project directory to make the studio forget a
project entirely.

A container has no per-user directory worth keeping, so point `QualityStudio__DataRoot` at a mounted
volume:

```yaml
environment:
  QualityStudio__DataRoot: /data
volumes:
  - quality-studio-data:/data
```
