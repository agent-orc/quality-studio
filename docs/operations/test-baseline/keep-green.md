# Keeping the Quality Studio gate honest

The required gate is the acceptance authority. The fast pre-review target is a
developer feedback loop, not a substitute for CI.

## Test lanes

| Lane | xUnit selection | Environment | Required behavior |
| --- | --- | --- | --- |
| Portable | `Category!=ToolBound&Category!=MachineBound&Category!=ExternalLive` | Any pinned .NET host | Deterministic assertions; no real process, timing, socket, or external-service dependency. |
| Tool-bound | `Category=ToolBound&Category!=MachineBound&Category!=ExternalLive` | Provisioned PR host | Real Git, .NET, browser, or pinned native-tool behavior with controlled fixtures. |
| Non-machine coverage | `Category!=MachineBound&Category!=ExternalLive` | Provisioned PR host | One combined measurement of portable plus tool-bound code so the committed ratchet remains comparable. |
| Machine-bound | `Category=MachineBound` | Labeled canary host | Host timing and performance with three retained samples. |
| External live | `Category=ExternalLive` | Explicit manual canary | External agent/service behavior. Missing opt-in is a failure, not a skip. |

`scripts/test-lanes.mjs` is the single filter definition. `scripts/run-dotnet-lane.mjs`
first inventories each expected project and rejects an empty selection before it runs
the tests. The required workflow executes the portable and tool-bound lanes separately,
then generates coverage from the combined non-machine selection. The release workflow
owns machine-bound and external-live selections.

## Fast pre-review target

Run:

```shell
npm run test:pre-review
```

It runs repository gate contracts, one incremental Release build, the portable .NET
lane in both test projects without redundant rebuilds, and the browser-binary resolver.
It is designed for quick local feedback and prints its scope before starting. It does
not run controlled tools, dev-stack host integration,
the production Angular build/spec suite, coverage, Gitleaks, timing checks, or external
services. A green result therefore means “ready to request CI review,” not “accepted.”

## Fixture ownership

- `tests/TestSupport/GitTestRepository.cs` is the only owner of real Git process setup.
  It fixes identity, timestamps, and line endings, captures command diagnostics, and
  cleans up repositories. A C# test file that consumes it must declare class-level
  `Category=ToolBound`.
- `tests/TestSupport/node-process-fixture.mjs` owns platform-neutral Node command stubs
  and ephemeral port allocation for launcher tests. No `.cmd` fixture or fixed port is
  permitted.
- Recorded external-format fixtures remain versioned inputs with validity assertions.
  Live downloads and external calls belong to controlled tool or canary lanes.

`tests/test-lanes.test.mjs` enforces the workflow selections, fixture consumption,
shared Node process helper, and absence of `Assert.Skip`. When a test gains a real
boundary, move the complete test class into the correct lane or isolate the method in
a dedicated categorized class. Do not weaken a filter, add a retry, or turn an
environment failure into a skip to obtain green.

## Change checklist

1. Keep pure tests uncategorized and deterministic.
2. Put real tool/process tests in a class with `Category=ToolBound`.
3. Put host timing tests under `Category=MachineBound` and retain repeated canary evidence.
4. Put external service tests under `Category=ExternalLive` and require explicit opt-in.
5. Run `npm run test:pre-review`, then let the complete required gate produce acceptance evidence.
6. Raise a coverage floor only after measuring added tests; never lower it to absorb a regression.
