# Gitleaks security scanning

Quality Studio treats Gitleaks as a deterministic secret-detection sensor, not as console noise. Security reviews consume its redacted findings and combine them with agent judgement in one review-meta sidecar according to [the security combination rule](security-review-combination.md).

## Versioning and licensing

- The pinned upstream version is `v8.24.2`.
- Binaries are fetched only from the official Gitleaks GitHub release archive and verified against the
  SHA-256 tracked in [`gitleaks-binaries.json`](../backend/AgentOrchestrator.CodeQuality/gitleaks-binaries.json).
  That file is the authority: the release checksum asset is fetched as well and must agree with it, but
  a checksum served from the same release as the archive proves only that the two match each other.
- A platform with no tracked digest is refused rather than installed. Provide the binary yourself and
  point `QUALITY_GITLEAKS_PATH` at it, or add the digest.
- Provisioning is serialized per cache path, so two scans starting together download once and never
  read a half-written binary. The unpacked executable is moved into place in one step.
- Gitleaks is MIT licensed; the upstream project license applies to the downloaded binary and release artifacts.

## Running without a download

Set `QUALITY_GITLEAKS_PATH` to an existing Gitleaks executable and nothing is downloaded; the resolver
only checks that it reports the pinned version. This is how the container image works: it installs the
pinned binary at build time and sets the variable, so no scan reaches the network at run time. It is
also the way to run scans on an air-gapped host or on a platform without a tracked digest.

## Update process

1. Bump `GitleaksBinaryResolver.PinnedVersion` in [`backend/AgentOrchestrator.CodeQuality/GitleaksBinaryResolver.cs`](../backend/AgentOrchestrator.CodeQuality/GitleaksBinaryResolver.cs).
2. Replace the digests in [`gitleaks-binaries.json`](../backend/AgentOrchestrator.CodeQuality/gitleaks-binaries.json)
   from the new release's `gitleaks_<version>_checksums.txt`, and set its `version` to match. A mismatch
   between the two fails the host at first use instead of silently installing an unreviewed binary.
3. Update `GITLEAKS_VERSION` and `GITLEAKS_SHA256` in the [`Dockerfile`](../Dockerfile).
4. Review and update repository-owned allowlists in [`/.quality/security/gitleaks.toml`](../.quality/security/gitleaks.toml).
5. Run `quality security scan` on a representative checkout.
6. Inspect generated `.review-meta.security.json` files and adjust baselines for known placeholders only.
7. Update tests and this document if the report shape changes.

## Threat model

- This sensor redacts secret values and does not place them in its logs, finding text, or persisted sensor reports. This is not a system-wide guarantee for model-authored output.
- The scanner only stores file paths, rule ids, fingerprints, and safe line ranges.
- A missing or failed scanner is reported as `unavailable`; it is never treated as a clean pass.
- Baselines and allowlists are repository-owned so accepted placeholders stay auditable.
- High-confidence new findings are treated as a blocking security verdict.

## Process and report limits

Scanning and supporting Git commands use the shared bounded process runner: stdout and stderr are
drained concurrently, each pipe is limited to 1,000,000 characters, and the command deadline is five
minutes per command, not per complete scan job. Version probes use a 30-second deadline. Overflow, cancellation and timeout terminate the
process tree and do not return partial output as successful evidence.

The JSON or SARIF report has an independent 8 MiB byte limit, enforced before allocation and while
reading. This bounds report ingestion, not the disk space a child process can write before it exits.
Missing, blank, malformed or structurally invalid reports are unavailable. Exit code 1 is
accepted only with valid findings; a scanner/configuration failure that exits 1 without findings
cannot become a clean result. A successful clean JSON scan must produce a valid empty array.

These are application-level limits. They do not isolate the scanner, Git configuration or repository
build processes from the host; the [security review](../results/review-2026-09-19/security-review.md)
describes the additional worker boundary needed for hostile repositories.

## Commands

- `quality security scan .`
- `quality security scan . --mode range --range main..HEAD`
- `quality security scan . --mode staged`

These diagnostic scan commands report evidence but do not write review sidecars. Run a normal `security` review to persist the combined statement.

## Scan boundary and generated data

Repository mode scans the selected checkout; range and staged modes scan their respective Git
inputs. None of these modes includes the external project data root. After the data-root migration,
review sidecars, model prose, reports and handover drafts stored there therefore need a separate
generated-output scan and redaction boundary. A clean repository scan does not certify those outputs.

The [September 2026 security review](../results/review-2026-09-19/security-review.md) demonstrates this
gap using only a synthetic secret in isolated directories. It reopens the historical F-04
scan-coverage claim. The old in-checkout review directory remains eligible for scanning; no blanket
exclusion for review output is added.
