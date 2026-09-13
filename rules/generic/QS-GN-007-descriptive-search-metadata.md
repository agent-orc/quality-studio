---
id: QS-GN-007
version: 1.0.0
title: Describe each public page in its own title and metadata
technology: generic
kinds: [code]
category: seo
severity: medium
defaultOn: false
autofixable: false
deterministicRuleIds: []
since: 1.5.0
enabled: true
---

## Statement

Give search-relevant HTML pages descriptive titles that match their visible purpose and language. Where descriptions are supplied, make them accurate summaries of the corresponding page.

## Rationale

Distinct titles help people choose between reviews, criteria and model information. Copied or misleading metadata promises the wrong content. Search engines can generate different title links and snippets; authoring metadata does not control their exact display.
Primary references: [Google title links](https://developers.google.com/search/docs/appearance/title-link) and [search snippets](https://developers.google.com/search/docs/appearance/snippet).

## Detection

Inspect delivered or rendered metadata together with the page's main content. Cite an absent or empty title, demonstrably misleading language/purpose, or identical boilerplate across materially different reviewed pages. Treat a missing description as an improvement opportunity unless an explicit project requirement makes it a defect. Do not flag every repeated brand name, impose arbitrary character counts or keyword density, or promise a ranking increase. A source template without a title is insufficient evidence if a known build or render stage supplies it. Private dashboards and JSON API responses are outside this rule.

## Good example

Illustrative pattern for Quality Studio's public website:

```html
<!-- Criteria page; main content explains rule evidence and limits. -->
<title>Review-Kriterien und Qualitätsmetriken | Quality Studio</title>
<meta name="description" content="Wie Quality Studio Regeln, Messwerte und Review-Evidenz erklärt und welche Grenzen die Ergebnisse haben.">
<h1>Qualität nachvollziehbar prüfen</h1>
```

## Bad example

```html
<!-- Every generated page receives this unrelated template metadata. -->
<title>Home</title>
<meta name="description" content="Buy shoes at the best price.">
<h1>Code review models and token costs</h1>
```

## Change history

- 1.0.0 (2026-09-13): Added an opt-in SEO review rule with explicit audience, evidence and scope limits.
