# Changelog

## Unreleased

- Store Quality Studio project runtime data outside analysed checkouts, defaulting
  to the user's local application-data directory keyed by project id.
- Migrate legacy root and per-folder `.quality` trees once at startup and warn when
  Git still reports dirty `.quality` paths.
- Ignore all in-checkout `.quality/` directories and define explicit report exports
  as the only versionable Quality Studio artefacts.
