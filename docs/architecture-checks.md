# Architecture and typography checks

Quality Studio checks its own agreed structure with version-controlled contracts. Another
repository does not inherit this folder convention merely because Quality Studio reviews it.

## Source ownership

`quality-architecture.json` declares required directories, retired source paths, and allowed
direct children. The current product has sibling `backend/` and `frontend/` workspaces;
.NET source/tests live under `backend/src` and `backend/tests`. Angular application ownership
lives under `frontend/src/app/core`, `shared`, `features`, and `shell`. Only bootstrap
configuration stays directly in `app/`.

The in-process `architecture` sensor validates that contract and emits normal deterministic
findings with locations, recommendations and stable fingerprints. It is automatically enabled
for registered repositories containing a contract, including older registrations loaded after
the sensor was introduced. Repositories without a contract have the sensor disabled by default.
An invalid contract produces a visible `architecture/invalid-contract` finding; a missing
contract reports that the analysis is unavailable rather than claiming a clean review.

Run the same sensor without an agent or API:

```sh
dotnet run --project backend/src/quality-cli -- analyze . --analysis architecture
```

Retired source-path checks ignore `bin`, `obj`, `node_modules`, compiled frontend output,
coverage, Git internals and Quality Studio metadata. Existing build artifacts or old review
metadata do not make a clean migration look broken. Paths must stay inside the repository;
the sensor refuses symbolic-link traversal. Contract size and directory traversal are bounded.

## Angular dependency direction

`frontend/lint/architecture.config.mjs` configures the local AST rule
`quality-architecture/layer-imports`:

| Owning layer | Allowed local dependencies |
| --- | --- |
| `core/models` | `core/models` |
| `core` | `core`, pure `shared/utils` |
| `shared/utils` | `shared/utils`, `core/models` |
| `shared` | `shared`, `core/models` |
| `features/<feature>` | `core`, `shared`, the same feature |
| `shell` | All application layers |

Imports, re-exports, literal dynamic imports, TypeScript import types and import-equals
declarations follow the same rule. Comments and text strings are not mistaken for imports.
Sibling features compose through shell; deliberate changes belong in the dependency contract.
Relative imports and configured aliases are checked. Package imports and dynamically computed
module names are not resolved by this local rule; add path aliases to the contract when adding
them to TypeScript configuration.

## Typography

The PostCSS parser checks actual CSS declarations for `font-size`, `font` shorthand and
`--studio-font-size-*` token values below the declared 11px minimum. Findings retain their
original CSS source ranges. `frontend/lint/typography.config.mjs` owns the minimum and any
exact file/selector/property exceptions, which also require a reason. No decorative exceptions
are currently needed.

This check covers literal pixel sizes and zero. Relative units, browser-dependent keywords,
`var()`, `calc()` and `clamp()` cannot be reduced to a rendered size by this check; token
definitions and browser review cover those cases. CSS parse errors fail visibly.

## Review and CI integration

```sh
npm --prefix frontend run lint
npm --prefix frontend run test:architecture
npm --prefix frontend run lint:sarif
```

Both Angular dependency and CSS typography diagnostics are native ESLint results. The
existing `eslint-frontend-sarif` sensor includes them in deterministic preflight evidence for
Quality Studio reviews. The architecture sensor also implements `IDeterministicEvidenceSensor`
and is collected by the same review evidence pipeline. Rule catalogue entries `QS-NG-001`,
`QS-NG-003` and `QS-GN-003` explain the expectations to the reviewer.

CI runs layout sensor regression tests in the .NET suite, Angular/CSS rule regression tests,
and the real repository's ESLint gate. Tests include a deliberately broken repository,
the current repository, valid alternative layouts, old build output, malformed contracts,
dependency boundary violations, and CSS diagnostic conversion to SARIF.
