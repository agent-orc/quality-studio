# Derived boundary inventory

Quality Studio's `boundaries` sensor derives externally callable and
caller-influenced surfaces from source and configuration. It does not consume a
hand-maintained endpoint list. A repository scan atomically writes the stable,
diffable result to:

```text
.quality/boundaries/inventory.json
```

Run it directly with:

```text
quality boundaries scan .
```

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the repository truth.

## Contract

The JSON contract is
[`schemas/boundary-inventory.v1.schema.json`](../schemas/boundary-inventory.v1.schema.json).
Every entry records a source location, direction, transport, reachability,
authentication, authorization, inputs and their sources, response shape, side
effects, rate and size limits, and repository consumers. Facts contain
`derivedFrom` evidence. When the source cannot prove a fact, its value is
`unknown`; the analyzer never converts an assumption into a fact.

The analyzers currently recognize:

- ASP.NET minimal API registrations, route groups, health/static-file/hub and
  WebSocket registrations, authentication and authorization middleware, CORS,
  Kestrel request limits, and rate-limit policies.
- Express-style Node routes and listeners, message consumers, scheduled jobs,
  watched directories, browser `postMessage`, and common body/rate limiters.
- Subprocess creation, outbound HTTP sinks, filesystem watchers, and literal or
  configured host bindings across supported source/configuration files.

## Mechanical findings

Findings are computed from the inventory for missing authorization, permissive
CORS, exception detail in error responses, missing rate or size limits,
unauthenticated side effects, and request input reaching filesystem or process
surfaces. They are returned as normal sensor findings so later security review
stages consume the same deterministic evidence.

The inventory intentionally contains no generation timestamp. Re-running it
against unchanged source produces identical content, while adding, changing, or
removing a boundary creates a normal repository diff.

## Bounded scans and partial results

The scan holds a wall-clock budget (30 seconds by default, overridable per
request via the `boundaries.timeBudgetMs` sensor configuration key) and a
per-regex-match timeout as defense in depth against a single pathological
file. A scan that exhausts either bound stops analyzing further files rather
than hanging; it never blocks the review pipeline indefinitely.

When that happens the inventory's top-level `complete` field is `false` and
`omissions` lists every file that was skipped, each with a `reason` of
`time-budget-exceeded` or `regex-match-timeout`. Entries and findings already
derived from other files are unaffected and are not re-labeled as unknown; the
inventory only ever asserts incompleteness about the files it did not reach. A
`boundary/scan-incomplete` finding (severity medium) is emitted alongside the
mechanical findings so an incomplete scan is visible wherever findings are
consumed, not only to a caller that inspects `complete` directly.
