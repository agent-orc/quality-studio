# Gitleaks security scanning

Quality Studio treats Gitleaks as a deterministic secret-detection sensor, not as console noise. The scanner writes structured security findings into review-meta sidecars and exposes a redacted security summary through the API and CLI.

## Versioning and licensing

- The pinned upstream version is `v8.24.2`.
- Binaries are fetched only from the official Gitleaks GitHub release archive and verified against the matching release checksum asset.
- Gitleaks is MIT licensed; the upstream project license applies to the downloaded binary and release artifacts.

## Update process

1. Bump `GitleaksBinaryResolver.PinnedVersion` in [`src/AgentOrchestrator.CodeQuality/GitleaksBinaryResolver.cs`](../src/AgentOrchestrator.CodeQuality/GitleaksBinaryResolver.cs).
2. Review and update repository-owned allowlists in [`/.quality/security/gitleaks.toml`](../.quality/security/gitleaks.toml).
3. Run `quality security scan` on a representative checkout.
4. Inspect generated `.review-meta.security.json` files and adjust baselines for known placeholders only.
5. Update tests and this document if the report shape changes.

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

