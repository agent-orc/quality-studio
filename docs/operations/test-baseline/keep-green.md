# Keeping the Quality Studio gate honest

The required gate is the acceptance authority. The fast pre-review target is a
developer feedback loop, not a substitute for CI.

## Test lanes

`scripts/test-lanes.mjs` is the single filter definition. Nothing else may spell a
selection out by hand.

| Lane | xUnit selection | Environment | Required behavior |
| --- | --- | --- | --- |
| Portable | `Category!=ToolBound&Category!=MachineBound&Category!=ExternalLive` | Any pinned .NET host | Deterministic assertions; no real process, timing, socket, or external-service dependency. |
| Tool-bound | `Category=ToolBound&Category!=MachineBound&Category!=ExternalLive` | Provisioned PR host | Real Git, .NET, browser, or pinned native-tool behavior with controlled fixtures. |
| Non-machine coverage | `Category!=MachineBound&Category!=ExternalLive` | Provisioned PR host | One combined measurement of portable plus tool-bound code so the committed ratchet stays comparable. |
| Machine-bound | `Category=MachineBound` | Labeled canary host | Host timing and performance with three retained samples. |
| External live | `Category=ExternalLive` | Explicit manual canary | External agent/service behavior. Missing opt-in is a failure, not a skip. |

`scripts/run-dotnet-lane.mjs` first inventories each expected test project and rejects
an empty selection before it runs anything, so a filter typo fails the job instead of
reporting a silent pass. The required workflow executes the portable and tool-bound
lanes separately, then generates Cobertura coverage from the combined non-machine
selection into each project's own `TestResults` folder, where
`.github/scripts/validate_coverage.py` validates it against
`tests/coverage-baseline.json`. `.github/workflows/release-canary.yml` owns the
machine-bound and external-live selections.

A selection of `Category!=MachineBound` alone is *not* the required gate: it also picks
up the external-live review check, which fails by design without
`QUALITY_RUN_LIVE_REVIEW=1`. Use the lane scripts, or spell the full selection
`Category!=MachineBound&Category!=ExternalLive`.

## Fast pre-review target

Run:

```shell
npm run test:pre-review
```

It runs the repository gate contracts, one incremental Release build, the portable
.NET lane in both test projects without redundant rebuilds, and the browser-binary
resolver. It is designed for quick local feedback and prints its scope before
starting. It does not run controlled tools, dev-stack host integration, the production
Angular build and spec suite, coverage, Gitleaks, timing checks, or external services.
A green result therefore means "ready to request CI review", not "accepted".

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

`tests/test-lanes.test.mjs` enforces the workflow selections, fixture consumption, the
shared Node process helper, and the skip rule. When a test gains a real boundary, move
the complete test class into the correct lane or isolate the method in a dedicated
categorized class. Do not weaken a filter, add a retry, or turn an environment failure
into a skip to obtain green.

## The one accepted skip

`Assert.Skip` is rejected except directly under an `OperatingSystem.Is...()` guard. A
capability the host operating system cannot exhibit at all — Windows file-locking
semantics, for example — is a platform fact, not a hidden failure. Everything else
(a missing tool, an absent opt-in, an offline service) must fail the lane it belongs
to; that is what the categories are for.

## Change checklist

1. Keep pure tests uncategorized and deterministic.
2. Put real tool/process tests in a class with `Category=ToolBound`.
3. Put host timing tests under `Category=MachineBound` and retain repeated canary evidence.
4. Put external service tests under `Category=ExternalLive` and require explicit opt-in.
5. Run `npm run test:pre-review`, then let the complete required gate produce acceptance evidence.
6. Raise a coverage floor only after measuring added tests; never lower it to absorb a regression.
