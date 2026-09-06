# Changelog

## Unreleased

- Keep Quality Studio runtime metadata in a configurable per-project local data root,
  migrate legacy in-tree `.quality` data once, warn about dirty legacy metadata at
  startup, and reserve checkout writes for explicit operator exports.
