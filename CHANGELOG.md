# Changelog

## Unreleased

- Store Quality Studio project runtime data in a configurable, project-keyed local
  data root instead of the analysed Git checkout.
- Add lossless one-time migration for legacy root and nested `.quality` trees, a
  startup warning for dirty `.quality` paths, and ignore rules for remaining legacy
  directories.
- Define explicit exports as the only versioned quality artifacts and cover data-root
  resolution, migration, and checkout read-only review behavior with tests.
