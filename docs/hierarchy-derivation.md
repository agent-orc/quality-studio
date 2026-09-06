# Hierarchy derivation

How Quality Studio turns a repository into the five review levels
`Project -> Module -> Namespace -> File -> Function`, per adapter, and where each
adapter stops. `docs/concept.md` states the contract; this document states what
the implementation actually derives today.

## Principle

Each language needs its own derivation strategy. A structural claim is made only
where a compiler-grade parser is available in-process; otherwise the level is
reported as unsupported for that adapter, never approximated. In particular, no
structural statement — project membership, namespace, type, or member identity —
is derived from a regular expression. Regular expressions are used only for path
matching (`.gitignore`, `.quality/scope.json`, MSBuild item globs), where a glob
is the specification rather than a guess.

A level that an adapter cannot derive has no units. An empty level is honest; a
plausible-looking one derived from text patterns is not.

## Adapter overview

| Level | .NET solution | Angular / TypeScript | Generic path |
| --- | --- | --- | --- |
| Project | Solution file | `angular.json` project entry (or workspace/reference fallback) | Repository |
| Module | Member `.csproj` | Project root module only | Repository root |
| Namespace | C# namespace (parsed) | Source directory | Source directory |
| File | Evaluated `Compile` items | Configured source extensions | Every non-ignored file |
| Function | Parsed C# members | not derived | not derived |

## .NET solution adapter

| Level | Source | Method | Guarantees | Known gaps |
| --- | --- | --- | --- | --- |
| Project | `.sln` / `.slnx` at the repository root | `.slnx` parsed as XML; `.sln` project lines parsed by splitting their quoted fields | Every root solution is its own Project root; no arbitrary winner. Solution folders are transparent: the projects inside them are found. Without a solution, one synthetic project holds every discovered `.csproj`. | `.slnf` solution filters and solutions outside the repository root are not read. Non-C# project types (`.fsproj`, `.vbproj`, `.vcxproj`) are skipped rather than modelled. |
| Module | Solution member `.csproj` | XML read of the project file | One module per project file; target frameworks are variants of one module, not separate modules. | Project references are not modelled as edges here. Multi-targeting conditions are not evaluated. |
| Namespace | C# syntax trees of the module's compile items | Roslyn parser; block, file-scoped, nested and dotted declarations all normalize to one fully qualified name | Block and file-scoped forms produce the same identity. Types outside any namespace produce the `<global>` unit. A file contributing to several namespaces is aliased below each while remaining one canonical File unit and one sidecar. | Namespace membership follows declarations, not semantics: an `extern alias` or a conditional-compilation branch that the parser skips is invisible. |
| File | Evaluated `Compile` items of the module | Default item glob plus the project body's `Compile` `Include`/`Remove` elements, then repository scope rules | Build output never becomes a candidate. Partial types do not merge: every physical source stays independently reviewable. A linked source belongs to the module that includes it and receives a distinct File identity there. | MSBuild is not run; see the rule table below. |
| Function | C# syntax trees | Roslyn parser; a documentation-ID-shaped identifier built from syntax | Overloads are distinct units. Identity is deterministic and free of line or span offsets. | The identifier is a syntactic approximation of a Roslyn documentation ID; local functions are not units. See below. |

### MSBuild rules honoured

- The default compile glob: every `*.cs` below the project directory.
- `DefaultItemExcludes`-equivalent pruning of `bin` and `obj`, plus the tool
  directories `.git`, `.vs`, `.quality`, and `node_modules`.
- `EnableDefaultItems` and `EnableDefaultCompileItems` set to `false`.
- `<Compile Include="..." />`, including linked sources outside the project
  directory, and `<Compile Remove="..." />`, applied in document order after the
  default items — the order MSBuild uses, because the SDK's default item props
  are imported before the project body.
- Item glob syntax: `**` crosses directories, `*` and `?` do not, a trailing
  `/**` means every file below that directory, and `;` separates a list. Matching
  follows the filesystem's case rules.
- A source below a nested project directory belongs to the nested project
  ("nearest project ancestor wins"). Every `.csproj` in the repository takes part
  in this rule, including projects no solution references.

### MSBuild rules not honoured

These are the deliberate gaps of a derivation that never runs an evaluation. Each
one is a place where Quality Studio's item set can differ from a real build.

- `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  and every other `<Import>` are not evaluated. Items or properties defined there
  are invisible — including a repository-wide `EnableDefaultCompileItems` or an
  additional `<Compile Include>`.
- `Condition` attributes are not evaluated. For
  `EnableDefaultItems`/`EnableDefaultCompileItems` the last occurrence in the
  project file wins, conditional or not.
- MSBuild property and item functions are not expanded. A glob containing
  `$(...)` or `@(...)` matches nothing rather than guessing a value.
- `<Compile Update="..." />` neither adds nor removes an item, which matches
  MSBuild, but its metadata (including `Link`) is ignored.
- Multi-targeting produces one item set. Target-framework-conditional sources are
  therefore all included.
- Source generators and other build-time generated sources are never units. They
  live under `obj` and are pruned; nothing regenerates them for review.
- `Link` metadata does not move a file: a linked source keeps its repository path.
  The contract gives it a distinct File identity in every owning module, which
  falls out of the `(Module ID, repo path)` tuple.
- A compile item resolving outside the repository worktree is dropped rather than
  reported under a fabricated path.

### Roslyn syntax, not semantics

C# sources are parsed with `CSharpSyntaxTree.ParseText`. There is no
`MSBuildLocator`, no `MSBuildWorkspace`, no `Compilation`, and no semantic model.
That choice is deliberate:

- Deterministic: the same bytes always produce the same units, independent of
  installed SDKs, NuGet caches, or restore state.
- Offline and side-effect free: derivation neither restores nor builds, so it can
  run on a checkout that does not compile.
- Cheap enough to run on every hierarchy scan; a workspace load is not.

The price is that everything requiring binding is out of reach:

- Type and parameter names keep the text written in the source. `using` aliases,
  `global::`, imported namespaces, generic type parameters, and `dynamic` are not
  resolved, so a parameter written `Money` stays `Money` where Roslyn's semantic
  model would say `Demo.Money`.
- Conditional compilation is parsed with no preprocessor symbols defined. Members
  that only exist inside `#if DEBUG` are not units.
- Nothing detects that a type or member is compiler-generated beyond the
  repository's configured generated-source patterns.

### Function identity

Function units use the identity tuple
`(File ID, "csharp-syntax-doc-id-v1", documentation ID)`. The literal deliberately
differs from the contract's `roslyn-doc-id-v1`: the value is a documentation ID
*shape* built from syntax, not a documentation ID produced by Roslyn's symbol
API, and naming it `roslyn-doc-id-v1` would be a claim the derivation cannot back.
When a semantic sensor lands, that contract literal becomes available and function
IDs move to it. Function units carry no stored sidecars today, so the identity may
still change.

The identifier is built as
`M:<namespace>.<type chain>.<member>(<parameter types>)`, with `` ` `` for type
arity, ` `` ` for method arity, `@` for `ref`/`out`/`in` parameters, an omitted
parameter list when there are none, and `~<type>` appended for conversion
operators. Members derived:

| Derived | Not derived |
| --- | --- |
| Methods | Local functions (locations on their containing member, per contract) |
| Instance and static constructors, destructors | Lambdas and anonymous methods |
| Operators and conversion operators, including `checked` forms | Record primary constructors |
| Property, indexer, and event accessors, including expression-bodied properties and indexers | Properties, fields, and events as such — the contract makes accessors the units |

`init` accessors share the `set_` metadata name, an `IndexerNameAttribute` is not
evaluated (indexer accessors are always `get_Item`/`set_Item`), and if one file
yields the same identifier twice the first declaration wins.

Ordering is deterministic: namespaces ordinally by name, files ordinally by
repository path, functions in source order.

### Effect on stored units

Project, Module, and File identity tuples are unchanged by this derivation, so
every published sidecar keeps resolving. Two consequences are worth naming:

- Function IDs changed with the new literal and identifier. No function sidecar
  exists, so nothing is orphaned.
- Build output is no longer a scope candidate and therefore no longer appears in
  an aggregate's `excluded` list. Aggregates whose stored manifest contained
  machine-local `obj/Debug` or `obj/Release` paths go stale once and are stable
  afterwards; before, they could not be reproduced on a different machine at all.

## Angular and TypeScript adapter

This adapter serves Angular workspaces and, through two fallbacks, plain
TypeScript monorepos. Its unit IDs use the `angular` adapter segment in all three
cases.

| Level | Source | Method | Guarantees | Known gaps |
| --- | --- | --- | --- | --- |
| Project | `projects` entries of `angular.json`; else `workspaces` globs in `package.json`; else `references` in a `tsconfig` | JSON read; workspace globs matched as paths | Every named workspace project is a Project root, ordered ordinally. | A TypeScript monorepo is reported under the `angular` adapter segment. Project `targets`/`architect` configuration is not read. |
| Module | Project root | Single synthetic root module per project | Stable and never invented. | **The contract's NgModule and lazy-route boundaries are not derived.** `@NgModule` declarations, `loadChildren`, and `loadComponent` would require the TypeScript compiler and the Angular compiler's ownership rules; until then every source belongs to the root module. |
| Namespace | Repository directory of the file | Path grouping | Matches the contract, which defines the Angular Namespace as a directory rather than the TypeScript `namespace` keyword. | None beyond File-level gaps. |
| File | Files below the project `sourceRoot` with a source extension (`.ts`, `.tsx`, `.html`, `.css`, `.scss`, `.sass`, `.less`) | Extension and suffix filter, plus `.gitignore` and repository scope | Deterministic and ordinal. Specs, `.d.ts`, `.ngtypecheck.ts`, and `.generated.ts` are excluded. | `tsconfig` `include`/`exclude`/`files` are not evaluated, so a file the compiler does not own can become a unit. A component's `templateUrl`/`styleUrl` files are separate File units, not additional subject inputs of the component. |
| Function | — | **not derived** | — | The TypeScript compiler API runs in Node and is not available in-process from .NET. Deriving named declarations from text would be exactly the regex-based structural claim this design rejects, so the level stays empty. See "Future compiler-grade options". |

## Generic path adapter

Used when a repository has neither a .NET solution/project nor an
Angular/TypeScript workspace.

| Level | Source | Method | Guarantees | Known gaps |
| --- | --- | --- | --- | --- |
| Project | Repository | Synthetic identity `(".", "synthetic-generic-project")` | One stable root without a registry. | Cannot distinguish several logical products in one repository. |
| Module | Repository root | Single synthetic root module | Stable. | No sub-module structure of any kind. |
| Namespace | Repository directory | Path grouping | Every directory holding a file is a unit. | Directories are not language structure; this is a browsing contract, not a semantic one. |
| File | Every non-ignored, non-build-output file | `git ls-files` when Git is available, otherwise a pruned walk plus `.gitignore` parsing | Tracked and untracked-but-not-ignored files are both visible. | Binary files are units too; whether a subject can be hashed is decided later, by the hashing contract. |
| Function | — | **not derived** | — | The repository language is unknown by definition. |

## Future compiler-grade options

- **Roslyn semantic model.** Adding `MSBuildLocator` plus `MSBuildWorkspace`
  would give real evaluated `Compile` items — honouring `Directory.Build.props`,
  conditions, imports, and multi-targeting — and real
  `ISymbol.GetDocumentationCommentId()` values, which would let function identity
  move to the contract's `roslyn-doc-id-v1`. The cost is a runtime dependency on
  an installed SDK, restore state, and a workspace load per solution, which is why
  it belongs behind an optional sensor rather than in the default scan path.
  `spikes/code-graph` measures that path.
- **TypeScript compiler API as a Node sidecar.** A short-lived Node process using
  `typescript`'s program API could emit owned files per `tsconfig`, `@NgModule`
  and lazy-route boundaries, and named declarations with stable symbol keys, which
  the .NET adapter would consume as data. That would fill the Angular Module and
  Function levels without ever making a structural claim from text.
- Until either exists, the affected levels stay unsupported for their adapter.
