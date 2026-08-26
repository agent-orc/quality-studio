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

Large or latency-sensitive callers can request a deterministic page:

```text
quality boundaries scan . --max-files 500
quality boundaries scan . --max-files 500 --after "frontend/src/app.ts"
```

`--max-files` bounds the number of source files read. `--after` resumes in
ordinal repository-path order using the `continuationAfter` value from the
previous page. The same options are available to registered sensor callers as
the `maxFiles` and `after` configuration keys.

It is also available through the sensor API as sensor id `boundaries`.
Only complete repository scans persist the inventory. Path-scoped scans and
bounded/incremental pages return partial inventories without replacing the
repository truth. The CLI exits with code 2 for partial coverage so automation
cannot mistake an empty page for a clean repository result.

## Contract

The current JSON contract is
[`schemas/boundary-inventory.v2.schema.json`](../schemas/boundary-inventory.v2.schema.json).
The `coverage` object records the mode, whether the result is complete, eligible
and analyzed file counts, omitted counts grouped by reason, and the next
continuation path. Omission examples are capped at five paths per reason so the
metadata itself remains bounded. Files larger than 2 MiB and unreadable files
make even an otherwise full scan explicitly incomplete.

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

Client consumers and host reachability are indexed once per scan. Route
matching reuses that index instead of rescanning every browser source line for
every server endpoint, and MVC controller matching uses a non-backtracking
expression.

## Mechanical findings

Findings are computed from the inventory for missing authorization, permissive
CORS, exception detail in error responses, missing rate or size limits,
unauthenticated side effects, and request input reaching filesystem or process
surfaces. They are returned as normal sensor findings so later security review
stages consume the same deterministic evidence.

The inventory intentionally contains no generation timestamp. Re-running it
against unchanged source produces identical content, while adding, changing, or
removing a boundary creates a normal repository diff.
