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

Source files are read once and repository-wide joins (host bindings and client
HTTP calls) are indexed once. The default repository scan is bounded to 5,000
eligible source/configuration files. Override that limit or request the next
deterministic page with:

```text
quality boundaries scan . --max-files 1000
quality boundaries scan . --max-files 1000 --continuation-token frontend/src/client.ts
```

Sensor/API callers use the equivalent `maxFiles` and `continuationToken`
configuration keys. Paths and continuation tokens are ordered repository-
relative paths, so repeated scans of an unchanged tree return stable pages.

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the repository truth.

Every inventory includes a `scan` object. `complete` is true only when the
result covers the whole repository; `mode`, discovered/scanned/omitted counts,
`partialReason`, and `continuationToken` make bounded, incremental, unreadable-
file, and path-scoped results explicit. A partial scan returns the findings it
did establish, but never overwrites `.quality/boundaries/inventory.json` and
must not be interpreted as a clean repository result. `SensorScanResult`
projects the same state through `complete`, `partialReason`, and
`continuationToken`, allowing security review to continue with visibly partial
deterministic evidence instead of waiting indefinitely.

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
