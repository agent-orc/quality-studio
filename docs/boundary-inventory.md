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

## Scaling and partial scans

The sensor reads each eligible source once and builds repository-wide host and
client-call indexes once. Endpoint discovery queries those indexes instead of
rescanning every source for every endpoint. Test directories, generated output,
package caches, files larger than 2 MiB, and the documented lockfile/test-file
patterns are not analyzed.

Operators can put a deterministic file and/or byte budget around a scan:

```text
quality boundaries scan . --max-files 2000 --max-bytes 134217728
```

Supplying either limit selects bounded mode. When no explicit limit is supplied,
bounded mode defaults to 2,000 files and 128 MiB. Incremental callers supply the
exact changed files or directories explicitly:

```text
quality boundaries scan . --changed src/Api.cs --changed frontend/src/app
```

The serialized `scan` object reports the mode, candidate/scanned/omitted file
and byte counts, configured limits, incremental paths, `complete`, and a typed
`partialReason`. Path-scoped, incremental, budget-truncated, oversized-file, or
unreadable-file results are partial. A partial result is useful positive
evidence only for the files that were scanned: absence of a finding is not a
repository-wide pass. Sensor APIs propagate this as `completeness: partial`,
security evidence raises at least a warning, and the CLI returns exit code `3`.
Partial repository scans never overwrite
`.quality/boundaries/inventory.json`; only a complete repository scan may do so.

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
