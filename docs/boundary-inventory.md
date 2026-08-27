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

Large repositories can request a deterministic bounded page and continue from
the returned cursor:

```text
quality boundaries scan . --max-files 500 --start-after src/Previous.cs --no-write
```

`--start-after` is normally copied from the preceding page's `nextCursor`.
The equivalent sensor configuration keys are `maxFiles` and `startAfter`.

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the repository truth.

Every result includes `completeness`. A bounded or continuation page reports
`complete: false`, counts discovered, analyzed, and skipped files, and exposes
whether more work is available through `nextCursor`. Its cross-file facts are
explicitly incomplete and its `findings` array is empty because repository-wide
security findings must not be inferred from a subset. Partial results are never
persisted over `.quality/boundaries/inventory.json`, even when metadata
persistence was requested. Run an unbounded scan for authoritative findings and
a persistable repository inventory. Unreadable or source files larger than 2 MiB
also make a result partial, with no continuation cursor, because the omitted
content cannot be analyzed honestly.

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
