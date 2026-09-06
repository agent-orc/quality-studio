# Changelog

## Unreleased

- Store Quality Studio runtime state in a configurable, repository-identity-keyed local
  data root instead of analysed checkouts. Includes one-time in-tree `.quality`
  migration, startup dirty-tree diagnostics, and an explicit-export-only versioning
  policy.
