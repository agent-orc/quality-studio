# Derived boundary inventory

Quality Studio's `boundaries` sensor derives externally callable and
caller-influenced surfaces from source and configuration. It does not consume a
hand-maintained endpoint list. A repository scan atomically writes the stable,
diffable result below the project's data root, outside the scanned checkout
([`data-root.md`](data-root.md)):

```text
boundaries/inventory.json
```

Run it directly with:

```text
quality boundaries scan .
```

It is also available through the sensor API as sensor id `boundaries`.
Repository scans persist the inventory; path-scoped scans return a partial
inventory without replacing the stored repository-wide one.

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
  configured host bindings across supported source/configuration files. Typed C# calls to
  `ISensorCommandRunner` / `ProcessSensorCommandRunner` preserve each delegated process
  boundary, including executable/arguments/working-directory inputs and known API consumers;
  the shared direct OS process sink remains a separate inventory entry.

## Mechanical findings

Findings are computed from the inventory for missing authorization, permissive
CORS, exception detail in error responses, missing rate or size limits,
unauthenticated side effects, and request input reaching filesystem or process
surfaces. They are returned as normal sensor findings so later security review
stages consume the same deterministic evidence.

### Authentication uncertainty and severity (sensor 1.1.0)

Missing evidence for a control is not proof that the control is absent. Minimal API routes
without a recognized requirement now retain `authentication: unknown` and
`authorization: unknown`; a fallback policy, middleware in another file, or an upstream
control may apply. An explicit `AllowAnonymous` removes a derived route/group authorization
requirement, while a separately recognized custom authentication middleware still applies.

The authentication-related findings distinguish that uncertainty:

| Inventory evidence | Finding | Severity |
| --- | --- | --- |
| Inbound authentication unknown | `boundary/missing-authorization`, described as unverified | Medium |
| Authentication unknown, filesystem reads only | `boundary/unverified-side-effect-authorization` | Medium |
| Authentication unknown, process/write/mutation/quota/outbound effects | `boundary/unverified-side-effect-authorization` | High |
| Authentication explicitly absent, filesystem reads only | `boundary/unauthenticated-side-effect` | High |
| Authentication explicitly absent, process/write/mutation/quota/outbound effects | `boundary/unauthenticated-side-effect` | Critical |

Read-only static asset delivery therefore does not become a critical unauthenticated mutation.
It remains inventoried, and its access policy and file scope remain reviewable. Health routes
receive the same evidence-based treatment as every other route; neither route names nor static
file registration create a blanket exemption. A configured public endpoint can be intentional.

These are source-derived review signals, not runtime exploitability proofs. In particular,
`boundary/request-to-system-sink` identifies request inputs and a filesystem/process operation in
the same recognized handler; it does not prove unsafe taint propagation or defeat of confinement.
That independent signal remains unchanged by the authentication classification.

ASP.NET can authorize otherwise unannotated endpoints through a
[fallback policy](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/secure-data?view=aspnetcore-10.0#require-authenticated-users);
[static-file authorization](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0#static-file-authorization)
depends on middleware ordering and configuration. The scanner preserves `unknown` when it cannot
establish those effective controls.

The inventory intentionally contains no generation timestamp. Re-running it
against unchanged source produces byte-identical content, so adding, changing, or
removing a boundary is the only thing that can change the file, and comparing two
copies of it names exactly what moved.

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
