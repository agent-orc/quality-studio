FOUNDING TASK of coding-agent-code-quality (READ README.md first - it carries the operator-approved concept; this task elaborates, it does NOT re-decide).

1. REVIEW-META SCHEMA v1: precise JSON schema for the per-unit meta file (reviewedAt, kind, findings[] structure, grade scale, reviewedHash - hash algorithm + what exactly is hashed, multi-kind handling per unit, module/project aggregate files). File naming + placement convention (same feature folder). Worked examples for an Angular component and a .NET service.
2. HIERARCHY CONTRACT: how Project/Module/Namespace/File/Function levels are identified per technology (Angular workspace, .NET solution) - keep it derivable from repo structure, no manual registry.
3. STALENESS: rules for reviewedHash mismatch (stale marking, partial staleness for module aggregates).
4. EMBEDDING PATH recommendation: component package vs iframe vs API-only for the Agent Studio integration - one recommendation with reasons.
5. PACKAGE NAMING finalization proposal: AgentOrchestrator.CodeQuality direction (check nuget availability, propose final id + repo docs update).
6. HONEST SLICE PLAN: CQ-2..n (scaffold with TE-style release rails, schema lib + tests, first file-level code-review sweep runner, module aggregation, Studio embedding v1) with sizes.
Deliverable: docs/concept.md (English) + updated README status; NO production code beyond doc examples. Second-opinion pass before finishing.

## Addendum (operator dictation #2, 2026-07-11 - now part of the mandate):
7. UI CHARACTER: augmented code browsing is the product core (see README section) - concept must define the browser/tree/editor experience: meta layer on every node, aspect-split file reviews rendered at the code, staleness inline.
8. PERFORMANCE GOALS as first-class requirements: editor view robust + extremely fast; tree keyboard-driven, context menu, instant file loads, follows Git state. Name the technical approach (virtualization, incremental loading).
9. INPUT MANAGEMENT: global + per-project standards as review inputs - schema and precedence.
10. REVIEW KINDS now explicitly: code, security (detachable), PERFORMANCE.
11. RESEARCH BOX: code graph as graphical meta layer - scope a small research spike, do not decide.
12. WEBSITE SEED: the operator artifact (ecosystem page) seeds agent-orchestrator.dev/quality - concept should include the website outline.

13. INTEGRATION DIRECTION DECIDED (dictation #3): Quality Studio -> Agent Studio HANDOVER (finding -> task via normal task mutation path). Drop the embedding-path evaluation from point 4; specify the handover contract instead (what a finding-task carries: file, hash, findings, backlink).
