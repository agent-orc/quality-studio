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

Large or latency-sensitive scans can be split into deterministic pages:

```text
quality boundaries scan . --max-files 500 --no-write
quality boundaries scan . --max-files 500 --start-after src/last-file.ts --no-write
```

The result's `scan` object reports `status`, discovered/scanned/skipped file
counts, and an exclusive `nextCursor`. Every bounded page is explicitly
`partial`; its findings apply only to the files in that page. Partial results
return CLI exit code `3`, are unavailable as pass/fail sensor evidence, and
never replace the repository-owned inventory. Callers may merge pages for
exploration, but only an unbounded `complete` scan is authoritative repository
truth.

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

The analyzer indexes repository host bindings and JavaScript client-call lines
once per scan. It does not rescan every source line or rebuild route regular
expressions for every discovered boundary. Test and build-output directories
are excluded case-insensitively, including conventional `*.Tests` directories.
