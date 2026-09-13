# Quality domains, project properties and shared YAML

Quality Studio groups checks by the question they answer, the evidence they need and the scope in which they matter. A public website, an internal dashboard and a payment API should not inherit identical obligations merely because they share a repository.

## What is available

| Surface | Current behavior |
| --- | --- |
| Named review rules | 33 versioned rules; four SEO rules are available through explicit project opt-in. |
| Implemented product metrics | Six formulas in [review-methodology.json](../rules/review-methodology.json), with their actual implementation references. |
| Quality-domain catalogue | [quality-domains.json](../rules/quality-domains.json) lists implemented metrics, available review rules and planned checks across thirteen domains. The tool and public website use this same source. |
| Project-property applicability | Documented selectors and scope, not a runtime YAML filter. Missing properties remain unknown. |
| Shared project YAML | AGT's active `.agent-studio/project.yml` is execution contract v1. Its coordinated v2 extension is a draft; Quality Studio and Voice Studio do not yet consume it. |

The domain catalogue describes coverage of the product, not the quality of the currently selected repository. A planned check has not run. An enabled rule is an instruction to a reviewer, not evidence that the code passes it. A metric is a measured or calculated quantity only when its required input exists; heuristics remain identified as heuristics.

Review outcomes retain the [existing taxonomy](../backend/AgentOrchestrator.CodeQuality/catalogues/quality-taxonomy-core.v1.json): `evidenceStatus` is separate from `assessment`, and a policy `decision` requires a policy reference. Missing evidence does not become `pass`; an unknown scope does not become `not-applicable`. Domain IDs are catalogue groups, not automatically registered observation-aspect IDs.

## Activate the initial SEO review rules

For a repository containing HTML intentionally published for search discovery, merge these entries into its existing `.quality/rules/overrides.json`. Keep other project overrides:

```json
{
  "schemaVersion": 1,
  "overrides": [
    { "id": "QS-GN-005", "enabled": true, "reason": "Public product pages are intended for search discovery." },
    { "id": "QS-GN-006", "enabled": true, "reason": "The product website publishes German and English pages." },
    { "id": "QS-GN-007", "enabled": true, "reason": "Public routes need titles and descriptions matching their content." },
    { "id": "QS-GN-008", "enabled": true, "reason": "Public routes are linked and listed in the published sitemap." }
  ]
}
```

Open **Review policy**, inspect the effective rules and the **Prompt input preview**, then run a **Code** review on the website scope. The preview exposes character-budget omissions; a file review also filters by its adapter and level. Repository overrides enable rules repository-wide today, so each SEO rule explicitly excludes internal APIs, private dashboards and deliberate indexing exclusions. A component property will not narrow these overrides until the shared v2 consumer work ships.

| Rule | Review question | Important boundary |
| --- | --- | --- |
| [QS-GN-005](../rules/generic/QS-GN-005-public-page-indexability.md) | Do production crawler directives agree with the intended public audience? | Preserve deliberate noindex and authentication. robots.txt is not access control. |
| [QS-GN-006](../rules/generic/QS-GN-006-canonical-and-locale-consistency.md) | Do canonical and language references point consistently to the right pages? | A missing canonical is not universally a defect; prove the conflicting identity or requirement. |
| [QS-GN-007](../rules/generic/QS-GN-007-descriptive-search-metadata.md) | Do titles and supplied descriptions describe their actual pages? | No arbitrary character count, keyword density or ranking promise. |
| [QS-GN-008](../rules/generic/QS-GN-008-crawlable-discovery.md) | Can people and crawlers follow the intended routes? | A small well-linked website does not automatically need a sitemap. |

These rules produce agent review findings through the existing pipeline. They are not a deployed crawler, a Search Console integration or an SEO score. Source-only reviews cannot establish unseen HTTP headers, live indexing or search rankings. Each rule links to the relevant primary guidance from Google Search Central.

## One project contract across AGT, Quality Studio and Voice Studio

The shared authority is the existing AGT [execution-world dossier](https://github.com/agent-orc/agent-studio/blob/cbdc9fb427307ac01f3dc408d17df7c79d337e54/docs/operations/docker-ausfuehrungswelt-migration/index.html), extended by the [project-definition v2 plan](https://github.com/agent-orc/agent-studio/blob/cbdc9fb427307ac01f3dc408d17df7c79d337e54/docs/operations/docker-ausfuehrungswelt-migration/project-definition-v2-plan.md). Its [draft schema](https://github.com/agent-orc/agent-studio/blob/cbdc9fb427307ac01f3dc408d17df7c79d337e54/docs/operations/docker-ausfuehrungswelt-migration/project-execution.v2.draft.schema.json) and examples are design artifacts, not installation files.

AGT's v1 reader rejects unknown keys. Do not add `project`, `quality`, `voice`, `services` or `commands.start` to an active v1 document. The v2 plan keeps execution requirements (`capabilities`) separate from product properties and defines migration gates before any consumer can opt in. Existing Quality Studio activation and rule overrides remain in their current `.quality` contracts.

The planned property map uses these common keys:

| Property | Meaning of an explicit `true` declaration |
| --- | --- |
| `public-facing` | Accessible to an external audience; this alone does not request indexing. |
| `html-ui` | Presents an HTML user interface. |
| `seo-relevant` | Intended to be discovered through search engines. |
| `payment-api` | Handles payment operations or provider events. |
| `personal-data` | Processes personal data. |
| `authenticated` | Has authenticated behavior or protected resources. |
| `persistent-data` | Stores state that must survive process restarts. |
| `realtime` | Has time-sensitive interactive or streaming behavior. |
| `localized` | Provides multiple language or regional variants. |
| `deployable` | Produces a runnable deployment or service. |

Properties describe intent and functionality. They do not certify security, accessibility, correctness or compliance. The maps belong to `project.properties` and `project.components[].properties`. A component uses its own declarations; there is no implicit inheritance that would turn an internal API into a search-relevant website.

Planned `quality.applicability[]` entries refer to stable `domains` or `ruleIds`, a list of component IDs in `componentScope`, and a `selector` using `allOf`, `anyOf` and `noneOf`. Each selector reads true, false or unknown. A definite false condition excludes that scope; unresolved conditions stay unknown and visible. Explicit false and missing are different. Matching scope selects candidates only, never authorizes commands or certifies a result.

The draft includes examples and a consumer matrix for all three products. AGT currently validates `devServer` but starts registered URLs through separate start rules. Quality Studio starts through `scripts/dev-stack.mjs`. Voice Studio starts through `scripts/start.sh`; its route/source mappings remain in `voice.config.json`, and executable checks remain in host-approved profiles. The plan identifies how these declarations can converge without silently changing the launch, port, lifecycle or approval behavior of any product.

## Maintain the catalogue

Rule Markdown remains authoritative for prompts. `review-methodology.json` remains authoritative for existing formulas. `quality-domains.json` references those IDs and adds scope, rationale, evidence, interpretation, limits and primary sources. Do not copy formulas into domain prose or label proposed sensors as implemented.

Run `npm run rules:sync`, `npm run rules:check`, `npm run test:rule-catalogue`, `npm run test:review-reference` and the domain-catalogue checks after a source change. The separate public website imports a committed source snapshot through its existing synchronization script, preserving provenance and shared meanings across the tool and website.
