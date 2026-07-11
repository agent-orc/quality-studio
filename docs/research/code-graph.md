# Code graph research (QS-12)

Date: 2026-07-11

Decision: **Adopt Roslyn's workspace and compiler APIs for the QS-5 .NET hierarchy. Research further before adopting a retained type-use graph or impact-staleness policy.**

This is a time-boxed research result, not a production design or dependency. The
spike under `spikes/code-graph/` is deliberately absent from
`QualityStudio.slnx`.

The evidence here is sufficient for the hierarchy decision requested by this
task, whose 100-project deliverable is an estimate. It does **not** satisfy the
broader research-box exit in `docs/concept.md`: this run has no 100-project
corpus measurement, one-file incremental benchmark, UI-budget trace, or timed
comparison against tree navigation. Those remain gates for adopting the impact
graph, so the dependency/visualization decision is “research further,” not an
unqualified “adopt.”

## What was tested

The question is whether the .NET adapter can derive Module and Namespace from
compiler structure, rather than treating directories as those levels, while
leaving a credible path to queries such as “what source types depend on this
type?”

In Roslyn terminology, a solution contains `Project` objects. In Quality Studio,
the selected `.sln`/`.slnx` is the **Project review level**, while each Roslyn
project/`.csproj` is a **Module review level**. A type is useful graph structure,
but it does not become a sixth review level. This preserves the existing
`Project -> Module -> Namespace -> File -> Function` contract.

The spike proves the following on this repository's `QualityStudio.slnx`:

- MSBuild evaluates all six SDK-style .NET 10 projects from the `.slnx` file.
- compiler namespaces, including `<global>`, form the Module-to-Namespace tree;
- ordinary project source-backed type symbols form namespace-to-type structure;
- all five declared project references appear in the evaluated compilation graph,
  which also contains one transitive project reference after restore; and
- one semantic pass over source documents finds 181 deduplicated source-type
  use edges. Workspace loading reported no diagnostics.

## Options compared

| Option | Structure and reference fidelity | Incremental-update story | Cost and operational risk | 100-project planning footprint | Decision |
| --- | --- | --- | --- | --- | --- |
| Roslyn `MSBuildWorkspace` + compiler APIs | Full evaluated solution/project model; semantic namespaces, partial and nested types, source locations, symbols, project references, and bindable use sites. It can represent code that has not emitted successfully. | Immutable solution snapshots can share unchanged state. Start with conservative project/dependent-project invalidation; narrow to document deltas only after classifying edits safely. Project-system input changes require re-evaluation. | Highest in-process startup and memory; requires the matching SDK/MSBuild toolset, restored dependencies, pinned Roslyn packages, and visible handling of workspace diagnostics. C#/VB-specific. | **Heuristic estimate:** 0.8-2.2 GiB warm retained; 1.5-3.0 GiB transient peak for the corpus and formula below. | **Preferred for hierarchy.** It is the only option that directly satisfies the QS-5 semantic hierarchy and leaves a high-fidelity impact path. |
| Compiled-assembly reflection/metadata | Assembly, namespace string, type, signature, and assembly-reference data are cheap to read. Source files, declaration locations, partial declarations, local functions, conditional source, and failed/unbuilt edits are absent. Complete use edges require IL/PDB analysis. | Watch DLL/PDB/MVID changes and replace one assembly's index. The unit of invalidation is a built assembly, so the graph is stale until a successful build emits it and possibly its dependents. | Simple and relatively language-neutral for managed output. Prefer `System.Reflection.Metadata` or `MetadataLoadContext` to loading executable code. Dependency resolution, reference-vs-implementation assemblies, target variants, and build freshness remain the caller's problem. | Directionally lower for a streaming reader, but not measured or numerically estimated here; compiler/build memory is outside that process. | Reject as the hierarchy backbone. It remains useful for a post-build public-API inventory or deployment-only tool. |
| LSP-based (`documentSymbol`, `workspace/symbol`, `references`) | Provides editor-oriented locations and queries. The protocol is not a stored graph: workspace symbols are query-filtered, reference queries start from a position, capabilities are optional, and there is no standard stable cross-request symbol identity. `containerName` is explicitly insufficient for reconstructing hierarchy. | Text synchronization and watched-file notifications deliver file changes, but LSP defines no graph-delta feed. Quality Studio would still enumerate/requery, invent identities, and own invalidation. | Process isolation and possible polyglot reuse are attractive. For C#, a Roslyn-backed server retains the same compiler model and adds lifecycle, capability negotiation, JSON-RPC, serialization, and version coupling. | No protocol bound and no estimate from this spike. A Roslyn-backed C# server cannot be assumed to use less aggregate memory than Roslyn itself. | Do not use for the .NET backbone. Reconsider a static LSIF/SCIP-like import only if a future polyglot requirement justifies lower fidelity. |

The Roslyn Workspace API is specifically the solution/project/document layer that
provides source text, syntax trees, semantic models, compilations, compiler
options, and project/assembly references without Quality Studio rebuilding the
project system itself. Roslyn symbols also preserve language concepts that CLR
reflection sees only after lowering. These properties are the decisive benefit,
not merely that Roslyn can parse C#.

Reflection's lower memory use does not compensate for requiring fresh build
outputs. Reference assemblies are particularly unsuitable for dependency impact:
they may omit private members and method bodies, so implementation-only use edges
are unavailable. Runtime `Assembly.Load` also introduces loader-context and
dependency-resolution concerns that a metadata reader avoids.

LSP is a transport for language features, not a canonical graph API. Its process
boundary would be valuable if Quality Studio needed to consume an existing
multi-language server, but it does not remove the need to build, identify, store,
and incrementally update the graph.

## Preferred graph shape

The production adapter should keep hierarchy and dependency edges related but
distinct:

```text
Solution (.sln/.slnx)                         Quality Studio Project
  -> Roslyn Project (.csproj)                 Quality Studio Module
       -> semantic Namespace                  Quality Studio Namespace
            -> File declarations/aliases      Quality Studio File
                 -> callable symbols          Quality Studio Function

Roslyn Project --PROJECT_REFERENCE--> Roslyn Project
source Type    --TYPE_USE-----------> source Type
```

Project references are graph edges, never hierarchy parents. A file that declares
types in multiple namespaces still has one canonical File unit and can be
projected beneath each namespace as the concept contract requires. Partial types
must not merge their physical files into one review subject.

For persisted DTOs, use Quality Studio's existing path-derived Project/Module
identity contract and an explicit target-framework variant field. All variants
of one `.csproj` coalesce to one Module identity. Namespace
identity is `(Module ID, fully qualified namespace)`. Use a type's containing-type
chain plus metadata name/arity as an internal declaration key and retain all
declaring file IDs. The spike combines the Roslyn `ProjectId` with assembly
identity and documentation ID for source-node identity, then fails rather than
guessing when a referenced symbol remains ambiguous between projects. Those
in-process keys are not proposed as a durable schema.

Edges should carry their kind and provenance. `PROJECT_REFERENCE` comes from
evaluated project references. A future `TYPE_USE` should record the source and
target type keys plus the declaring source file(s), so a document change can
remove its old contributions without rebuilding unrelated edge records.

## Incremental-update story

### Adopt in QS-5: bounded batch snapshots

QS-5 should initially open the selected solution on scan, derive the five-level
hierarchy, copy it into compact Quality Studio records, surface every workspace
failure, and dispose the Roslyn workspace. A complete rebuild is acceptable for
the first correctness milestone and avoids retaining compiler state in the API
process between requests. Cache the compact result by a fingerprint of the
solution, project files, imported repo-local build inputs, configuration, and
source inputs.

Include project-reference edges because they are already part of the evaluated
project model. Build the declaration index needed to associate semantic
namespaces, files, and callable symbols. Do not run a solution-wide reference
search once per type; that changes the work from approximately one semantic pass
over source into `types x solution searches`.

### Add later: conservative graph deltas, then document-scoped optimization

A long-lived, preferably out-of-process graph worker can hold the current
immutable `Solution` and compact reverse indexes:

1. Quality Studio owns a debounced file watcher; a standalone
   `MSBuildWorkspace` is not an editor and will not make arbitrary disk changes
   appear by itself.
2. For a `.cs` edit, create a new solution snapshot with the document's new
   `SourceText`. Roslyn can reuse unchanged text/tree/compilation state.
3. Initially treat every edit as capable of changing semantic binding. Rebuild
   declarations and type-use edges for the containing project and its transitive
   dependent projects. Renames, global usings, overload/base/member changes,
   partial declarations, and generator inputs can all affect unchanged files.
4. Only after an edit classifier is validated may trivia or implementation-body
   changes with an unchanged declared semantic surface remove/rebuild just the
   edited document's contributions. A rename remains a delete plus add unless an
   explicit rename detector proves continuity.
5. Recompute affected namespace/file aliases and reverse-dependent summaries,
   then atomically publish a new compact graph version.
6. Changes to `.sln[x]`, `.csproj`, `Directory.Build.*`, imported targets/props,
   packages, analyzers/generators, target framework, or compiler options trigger
   project/solution re-evaluation rather than a text-only patch.

This supplies candidates for future impact analysis: changed declarations map to
incoming `TYPE_USE` edges and then to dependent review subjects. An edge is not,
by itself, proof that a review is stale. The product still needs a versioned rule
for edge kinds, propagation depth, test/generated-code treatment, and how the
dependency set participates in the reviewed manifest before it can state “this
change staled dependent reviews.”

Workspace change events can identify project/document changes made to a workspace,
and Roslyn syntax snapshots can report incrementally identical nodes. Those APIs
make the later design plausible. The spike deliberately does not claim an
incremental latency result; that must be measured on the QS-5 fixture corpus with
real file watching and project reloads.

## Memory footprint estimate for 100 projects

There is no official “bytes per Roslyn project” bound. Project count alone is a
poor predictor: source bytes, document count, generated code, metadata references,
active compilations/semantic models, analyzers, and target variants dominate.
The following is an engineering planning estimate, not a benchmark result.

Assumed corpus and process:

- x64 Release worker, one target/configuration, 100 SDK-style C# projects;
- 5,000-10,000 source documents, 0.5-1.0 million LOC, and 40-80 MiB of UTF-8
  source;
- restored dependencies, all projects loaded, declarations walked, and semantic
  type-use edges materialized once;
- no analyzers executing and no retained semantic model per document; and
- a compact graph index rather than retained syntax/symbol objects in the API.

The allowances below are transparent heuristics. The 250 MiB baseline rounds the
measured 196.4 MiB small-solution process upward for tool/runtime headroom. The
1-4 MiB/project and 8-16x source multipliers are deliberately broad engineering
proxies for evaluation/reference state and multiple in-memory source/compiler
representations, not Microsoft-published ratios. The 50-150 MiB graph allowance
assumes on the order of one to three million compact records plus lookup/index
overhead. A real corpus measurement must replace every multiplier.

A deliberately broad sizing model is:

| Component | Planning allowance |
| --- | ---: |
| Process, MSBuild/Roslyn services, shared metadata baseline | about 250 MiB |
| Project/configuration/reference state | 1-4 MiB/project = 100-400 MiB |
| Text, trees, symbols, compilations, and caches | 8-16x 40-80 MiB source = 320-1,280 MiB |
| Compact declaration/edge/reverse indexes | 50-150 MiB |
| **Warm retained estimate** | **about 0.7-2.1 GiB; plan for 0.8-2.2 GiB** |
| **Transient load/rebuild peak** | **plan for 1.5-3.0 GiB** |

Reserve roughly **3 GiB per graph worker** until a representative 100-project
fixture proves a lower limit. Do not extrapolate linearly from this repository's
six projects: shared framework metadata creates a high fixed cost, while large
generated projects and multi-targeting create nonlinear peaks.

The final representative spike run on this machine used .NET SDK 10.0.301,
Roslyn 5.6.0, a Debug build, and warm restored packages. It reported 36.5 MiB
managed heap, 196.4 MiB current/peak working set, 4.135 s solution load, and
5.841 s analysis. This is a smoke measurement, not a performance result. It
measures only the spike process's working set, not an aggregate sampled MSBuild
child-process tree, and it is far smaller than the planning corpus.

Before retaining a workspace in production, measure 25-, 50-, and 100-project
fixtures. Record documents, UTF-8 source bytes, generated source, metadata
references, declarations, and edges. Sample aggregate process-tree Private Bytes
at 100 ms intervals through baseline, `OpenSolutionAsync`, compilation/type walk,
edge pass, one-file edit, and project reload. After each stage, also record the
retained managed heap. Run three cold and three warm iterations and report the
median steady state and maximum peak. That measurement can fit base,
per-project, and per-source-MiB terms and replace this estimate.

## Spike

The spike consists of:

- `spikes/code-graph/CodeGraphSpike.csproj`, with pinned Roslyn/MSBuild Locator
  packages; and
- `spikes/code-graph/Program.cs`, a deterministic console graph printer.

It registers the installed MSBuild toolset, opens `.sln` or `.slnx`, walks each
compilation assembly's global namespace, and filters to ordinary project
documents, excluding `bin`/`obj` paths relative to the owning project and Roslyn
source-generated documents.
For type-use edges it binds each C# `SimpleNameSyntax`, attributes the occurrence
to its enclosing source type, normalizes the target type/member to the target's
original containing type, keeps only types declared in the loaded solution, and
deduplicates the pair. This is intentionally a useful dependency approximation,
not a complete call/data-flow graph.

Run from the repository root:

```powershell
dotnet restore QualityStudio.slnx
dotnet run --project spikes/code-graph/CodeGraphSpike.csproj -- QualityStudio.slnx
```

Representative output from the final run is below. The middle of the declaration
tree and type-edge list is elided here only for readability; the command prints
all 77 source types and all 181 type edges.

```text
CODE GRAPH
Solution: <repo>\QualityStudio.slnx
Summary: 6 projects, 7 namespaces, 77 source types, 6 project-reference edges, 181 source-type reference edges

TREE
PROJECT AgentOrchestrator.CodeQuality [C#]
  NAMESPACE AgentOrchestrator
    NAMESPACE CodeQuality
      TYPE AggregateExclusion [Record]
      TYPE HierarchyNode [Class]
      TYPE RepositoryHierarchyBuilder [Class]
      TYPE ReviewMetaDocument [Record]
      TYPE ReviewRunner [Class]
      TYPE StalenessEvaluator [Class]
      ... 47 more types ...
PROJECT AgentOrchestrator.CodeQuality.Tests [C#]
  NAMESPACE AgentOrchestrator
    NAMESPACE CodeQuality
      NAMESPACE Tests
        TYPE HierarchyAggregationTests [Class]
        TYPE ReviewRunnerTests [Class]
        TYPE StalenessEvaluatorTests [Class]
        ... 8 more types ...
PROJECT QualityStudio.Api [C#]
  NAMESPACE <global>
    TYPE Program [Class]
  NAMESPACE QualityStudio
    NAMESPACE Api
      TYPE RepositoryAccess [Class]
      TYPE TreeNodeResponse [Record]
      ... 4 more types ...
PROJECT QualityStudio.Api.Tests [C#]
  NAMESPACE QualityStudio
    NAMESPACE Api
      NAMESPACE Tests
        TYPE ApiSmokeTests [Class]
        TYPE ApiSmokeTests.TestApplication [Class]
PROJECT quality [C#]
  NAMESPACE <global>
    TYPE Program [Class]
    TYPE QualityCommand [Class]
PROJECT quality-cli [C#]
  NAMESPACE <global>
    TYPE Program [Class]
    TYPE QualityCli [Class]

REFERENCE EDGES
PROJECT_REF AgentOrchestrator.CodeQuality.Tests -> AgentOrchestrator.CodeQuality
PROJECT_REF QualityStudio.Api -> AgentOrchestrator.CodeQuality
PROJECT_REF QualityStudio.Api.Tests -> AgentOrchestrator.CodeQuality
PROJECT_REF QualityStudio.Api.Tests -> QualityStudio.Api
PROJECT_REF quality -> AgentOrchestrator.CodeQuality
PROJECT_REF quality-cli -> AgentOrchestrator.CodeQuality
TYPE_REF AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.RepositoryHierarchyBuilder -> AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.HierarchyNode
TYPE_REF QualityStudio.Api::Program -> AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.RepositoryHierarchyBuilder
TYPE_REF QualityStudio.Api::Program -> QualityStudio.Api::QualityStudio.Api.RepositoryAccess
TYPE_REF AgentOrchestrator.CodeQuality.Tests::AgentOrchestrator.CodeQuality.Tests.ReviewRunnerTests.FakeAgent -> AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.IReviewAgent
TYPE_REF QualityStudio.Api.Tests::QualityStudio.Api.Tests.ApiSmokeTests.TestApplication -> QualityStudio.Api::Program
TYPE_REF quality::QualityCommand -> AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.HierarchyAggregation
TYPE_REF quality-cli::QualityCli -> AgentOrchestrator.CodeQuality::AgentOrchestrator.CodeQuality.ReviewRunner
... 174 more TYPE_REF lines ...

WORKSPACE DIAGNOSTICS
  none
  load_ms=4135 analysis_ms=5841

METRICS
  elapsed_ms=10034 load_ms=4134 analysis_ms=5841 managed_mb=36.5 working_set_mb=196.4 peak_working_set_mb=196.4
```

`Project.ProjectReferences` is the evaluated compilation-reference graph, not a
provenance-preserving list of literal `<ProjectReference>` items. In this run it
promoted `QualityStudio.Api.Tests -> AgentOrchestrator.CodeQuality` through
`QualityStudio.Api`; the other five edges correspond to direct declarations.
Production data should distinguish direct evaluated MSBuild items from transitive
compilation reachability instead of inferring provenance from this list.

The graph answers one concrete question that the hierarchy tree cannot: the
`PROJECT_REF` and `TYPE_REF` lines show which modules and source types use
`RepositoryHierarchyBuilder`, giving an impact-analysis candidate set without
guessing from folder proximity. The tree remains the accessible primary
navigation: a visual graph should be secondary, keyboard reachable, and expose
the same nodes/edges as a sortable textual list. This run did not time that task
against tree navigation, so it demonstrates added information, not that a graph
is faster. QS-5 does not need a graph UI.

## Recommendation for QS-5

### Adopt now

- Use `MSBuildWorkspace` plus the Roslyn compiler model in the .NET adapter to
  evaluate solution members, Compile items, project references, semantic
  namespaces, declarations, and callable symbols.
- Implement the existing five-level Quality Studio hierarchy exactly; keep types
  as an internal declaration/ownership index, not a review level.
- Coalesce all evaluated target-framework variants of a `.csproj` into one Module
  identity while recording variant provenance and diagnostics. Exclude `bin`,
  `obj`, generated, and configured non-subject inputs according to the existing
  QS-5 adapter contract.
- Materialize compact adapter-owned DTOs and dispose the workspace after a batch
  scan. Surface workspace diagnostics and fail rather than fabricating missing
  projects/namespaces.
- Include direct project-reference edges. Keep edge kinds explicit so future
  reverse-dependency data can be added without changing hierarchy identity.
- Start with whole-snapshot rebuilds and measure QS-5's multi-project fixtures
  before choosing a persistent or long-lived worker.

### Add later, with measurements and policy

- A dedicated out-of-process worker retaining an immutable solution snapshot,
  conservative project/dependent-project rebuilds, a compact reverse index, and
  later document-scoped deltas only for safely classified edits.
- Type-use edges calibrated against fixtures, then targeted Roslyn
  `SymbolFinder` queries for interactive “find references” rather than an
  all-types search loop.
- Versioned impact-propagation rules and reviewed-manifest inputs before dependent
  reviews are marked stale.
- Exhaustive cross-variant dependency edges, provenance for generated declarations
  beyond the required QS-5 exclusions, source-generator invalidation, and
  persistent graph storage if measured startup cost warrants it.
- The TypeScript/Angular investigation described below. LSP or static-index
  ingestion is reconsidered only for a demonstrated polyglot requirement.

### Explicit non-goals

- Shipping or referencing the throwaway spike from production projects.
- Replacing the derivable hierarchy or tree UI, or adding Type as a review level.
- Method-level call graphs, control/data-flow graphs, or exhaustive overload-level
  edges.
- Proving runtime behavior through reflection, dependency injection, routing,
  configuration strings, generated runtime proxies, or dynamic dispatch.
- Automatically declaring dependent reviews stale from an unversioned reference
  edge.
- External-package internals, cross-repository graphs, or a production graph
  database.
- Unsaved editor buffers or an LSP client/server integration.
- Exhaustive cross-variant/generated dependency edges in the first QS-5 adapter;
  baseline variant coalescing and generated/output exclusion are still required.

## Analogous TypeScript/Angular research brief

The Angular investigation should be a separate measured spike, not an LSP-shaped
port of this program.

- Compare the TypeScript Compiler API's `Program`, `TypeChecker`, module resolver,
  and `createSemanticDiagnosticsBuilderProgram`/`createWatchProgram` with a
  version-matched Angular compiler integration. Treat `@angular/compiler-cli`
  entry points as candidates whose public support/stability must be verified;
  do not build production identity on an Angular private API. Keep LSP or a
  static index as the same transport-oriented comparison used here.
- Prove the existing Angular ownership contract: `angular.json` projects,
  `tsconfig` inheritance/path aliases/project references, `@NgModule`
  declarations/imports, standalone components, and lazy `loadChildren` and
  `loadComponent` boundaries. Include templates/styles, declarations outside a
  feature directory, ambiguous ownership diagnostics, tests, and generated files.
- Use a mixed module/standalone monorepo plus a scaled corpus. Record project,
  source-byte, file, component/directive/pipe, route-boundary, symbol, and edge
  counts. Measure cold/warm initial load, a local implementation edit, an exported
  signature edit, route/NgModule metadata edits, config reload, retained/peak
  process-tree memory, and graph-delta size.
- Time concrete questions against the ordinary hierarchy: “which module owns this
  component?”, “which lazy boundary loads it?”, and “which source symbols consume
  this service/component?” Require keyboard-accessible textual results as the
  baseline before considering a visual graph.
- Exit with explicit identity/invalidation rules, evidence that incremental
  results stay inside the product's UI budgets, and a measured adopt/research
  further/do-not-adopt decision. Do not assume TypeScript namespaces correspond
  to Quality Studio Namespace; the existing contract deliberately uses owned
  repository directories at that level.

## Risks to carry forward

- `MSBuildWorkspace` reports some evaluation failures through workspace
  diagnostics rather than thrown exceptions. Diagnostics must be collected and
  made visible in scan results.
- Toolset/package skew can change project evaluation. Pin one Roslyn family
  version, register the installed MSBuild SDK before touching MSBuild assemblies,
  and record configuration/SDK in scan provenance.
- A semantic pass can dominate full-load time even when the retained graph is
  small. Build hierarchy first and schedule dependency indexing separately if
  measurement shows it would delay browsing.
- Linked files, assembly-name collisions, target variants, partial types, and
  generated documents require explicit identity/provenance rules; display-name
  matching is not sufficient.
- Keeping compiler snapshots in the API process risks large and unpredictable
  memory peaks. Prefer a disposable batch or bounded worker process until the
  100-project measurements exist.

## Primary sources

- [Roslyn workspace model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace)
- [Roslyn compiler API model and symbols](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)
- [Roslyn `SymbolFinder`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.findsymbols.symbolfinder?view=roslyn-dotnet-5.0.0)
- [Roslyn workspace change event data](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.workspacechangeeventargs?view=roslyn-dotnet-5.0.0)
- [Roslyn incrementally identical syntax nodes](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.syntaxnode.isincrementallyidenticalto?view=roslyn-dotnet-5.0.0)
- [MSBuild Locator](https://github.com/microsoft/MSBuildLocator)
- [`Microsoft.CodeAnalysis.Workspaces.MSBuild` package](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild/5.6.0)
- [`System.Reflection.Metadata`](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata)
- [`MetadataLoadContext` inspection](https://learn.microsoft.com/en-us/dotnet/standard/assembly/inspect-contents-using-metadataloadcontext)
- [.NET reference assemblies](https://learn.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies)
- [Language Server Protocol overview](https://microsoft.github.io/language-server-protocol/)
- [LSP 3.18 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.18/specification/)
- [TypeScript Compiler API, type checker, and incremental watcher](https://github.com/microsoft/TypeScript/wiki/Using-the-Compiler-API)
- [Angular NgModule ownership concepts](https://angular.dev/guide/ngmodules/overview)
- [Angular lazy `loadChildren`/`loadComponent` boundaries](https://angular.dev/best-practices/performance/lazy-loaded-routes)
- [Visual Studio notes on large-solution memory drivers](https://devblogs.microsoft.com/visualstudio/working-with-large-net-5-solutions-in-visual-studio-2019-16-8/)
- [.NET `Process.PrivateMemorySize64`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64)
- [.NET `GC.GetTotalMemory`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalmemory)
