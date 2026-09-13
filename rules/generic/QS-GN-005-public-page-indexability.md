---
id: QS-GN-005
version: 1.0.0
title: Keep intended public pages indexable
technology: generic
kinds: [code]
category: seo
severity: high
defaultOn: false
autofixable: false
deterministicRuleIds: []
since: 1.5.0
enabled: true
---

## Statement

For HTML pages explicitly intended for search discovery, keep the delivered response and crawler directives consistent with that intent. Preserve deliberate exclusions for private, preview and non-search pages.

## Rationale

A public product page cannot explain the product in search when its deployment accidentally retains a preview noindex directive. Crawl access and index permission are separate: robots.txt is neither access control nor a reliable way to remove a URL from search.
Primary references: [Google block indexing](https://developers.google.com/search/docs/crawling-indexing/block-indexing) and [robots.txt location and scope](https://developers.google.com/crawling/docs/robots-txt/create-robots-txt).

## Detection

Establish the intended audience and route before reporting a defect; public-facing alone does not imply SEO relevance. Cite a conflicting production response, robots meta tag, X-Robots-Tag, authentication redirect or applicable host-root robots.txt directive. A robots.txt inside /quality/ does not govern the host. A crawler blocked by robots.txt cannot read that page's noindex directive. Do not recommend removing authentication, deliberate noindex or privacy controls. Missing robots.txt is not itself a defect. Without deployment/header evidence, report only the source-level conflict you can establish; do not claim a live indexing failure or ranking loss.

## Good example

Illustrative pattern for Quality Studio's public website:

```html
<!-- Public product overview intended for search; no preview exclusion. -->
<!doctype html>
<html lang="de">
<head><title>Quality Studio: nachvollziehbare Code-Reviews</title></head>
<body><main><h1>Code-Reviews mit nachvollziehbaren Findings</h1></main></body>
</html>
```

## Bad example

```html
<!-- Production overview still inherits the preview-only directive. -->
<meta name="robots" content="noindex">
<h1>Discover Quality Studio</h1>
```

## Change history

- 1.0.0 (2026-09-13): Added an opt-in SEO review rule with explicit audience, evidence and scope limits.
