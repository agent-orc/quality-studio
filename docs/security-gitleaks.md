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

- Secret values are never written to logs, UI text, task handover prompts, or persisted reports.
- The scanner only stores file paths, rule ids, fingerprints, and safe line ranges.
- A missing or failed scanner is reported as `unavailable`; it is never treated as a clean pass.
- Baselines and allowlists are repository-owned so accepted placeholders stay auditable.
- High-confidence new findings are treated as a blocking security verdict.

## Commands

- `quality security scan .`
- `quality security scan . --mode range --range main..HEAD`
- `quality security scan . --mode staged`

These diagnostic scan commands report evidence but do not write review sidecars. Run a normal `security` review to persist the combined statement.
