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

Large or change-focused runs can be bounded without turning an incomplete
result into a clean result:

```text
quality boundaries scan . --max-files 1000 --no-write
quality boundaries scan . --changed src/Api.cs --changed frontend/src/app.ts
```

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the repository truth.

Every inventory contains a `coverage` object. `complete` is true only when all
eligible repository files were read. `mode` is `full`, `bounded`, `incremental`,
or `path`, with discovered, scanned, and omitted file counts plus a reason for
partial coverage. Partial scans never replace the repository-owned inventory,
suppress findings that depend on proving an absence across the repository, and
cannot become a clean security-sensor verdict. Positive findings derived from
scanned code remain available. The CLI exits with code `3` for a partial result
that has no blocking finding.

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

The sensor indexes host bindings and client call sites once per scan. It also
skips conventional test-output and `*.Tests`/`*.Test` trees so test endpoints do
not become production boundaries. Individual files larger than 2 MiB are
omitted and make coverage partial rather than being silently ignored.
