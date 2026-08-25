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

For a bounded page, pass `--max-files N`. Continue from the returned token with
`--cursor <relative-path>`; add `--no-write` when running a full diagnostic scan
that must not update repository metadata. The equivalent repository sensor
configuration keys are `maxFiles` and `cursor`.

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the repository truth. Bounded and cursor-based
scans are also explicitly partial and never replace the repository inventory.
Their `coverage` object reports the number of eligible files discovered and
processed, the number skipped, whether more remain, the continuation token, and
the reason the result is partial. A partial security-sensor result is treated as
unavailable rather than as a clean scan, while retaining findings from the
processed page.

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
