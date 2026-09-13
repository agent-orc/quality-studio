---
id: QS-GN-006
version: 1.0.0
title: Keep canonical and language URLs consistent
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

For search-relevant HTML, keep declared canonical URLs, redirects and language alternates consistent with the actual pages. Give separately targeted language versions stable URLs and reciprocal alternate references when hreflang is used.

## Rationale

A language switch on a single URL cannot describe several independently addressable language pages. Conflicting canonical and alternate declarations can obscure which page a visitor should reach. Canonical declarations are signals; they do not guarantee the search engine's selected URL.
Primary references: [Google canonicalization](https://developers.google.com/search/docs/crawling-indexing/consolidate-duplicate-urls) and [localized versions](https://developers.google.com/search/docs/specialty/international/localized-versions).

## Detection

Compare the reviewed page's delivered language, canonical, redirects and declared alternate targets. Report conflicting canonicals, nonexistent locale URLs or missing return links in an existing hreflang set, with exact source or response evidence. Keep each language's canonical in that language where an equivalent exists; do not canonicalize every locale to the German home page. Inspect rendered output when templates generate href values. Absence of canonical or hreflang alone is not a universal defect; establish duplicates or a declared localization requirement. Do not infer that a JavaScript application cannot be indexed, or that an unvisited target is broken.

## Good example

Illustrative pattern for Quality Studio's public website:

```html
<!-- /quality/en/criteria/; the German page links back to this URL. -->
<html lang="en">
<head>
  <link rel="canonical" href="https://agent-orchestrator.dev/quality/en/criteria/">
  <link rel="alternate" hreflang="en" href="https://agent-orchestrator.dev/quality/en/criteria/">
  <link rel="alternate" hreflang="de" href="https://agent-orchestrator.dev/quality/criteria/">
</head>
</html>
```

## Bad example

```html
<!-- English criteria content points to an unrelated German overview. -->
<link rel="canonical" href="https://agent-orchestrator.dev/quality/">
<link rel="alternate" hreflang="en" href="https://agent-orchestrator.dev/quality/#english">
```

## Change history

- 1.0.0 (2026-09-13): Added an opt-in SEO review rule with explicit audience, evidence and scope limits.
