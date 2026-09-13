---
id: QS-GN-008
version: 1.0.0
title: Make intended public pages discoverable through real links
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

Connect search-relevant pages with meaningful links that resolve to their intended destinations. When the project publishes a sitemap, keep its entries aligned with the canonical public routes.

## Rationale

A crawler and a visitor need a route to the content. Real link destinations also support opening a new tab and navigation without custom click code. A sitemap can aid discovery, but neither replaces usable navigation nor guarantees indexing.
Primary references: [Google crawlable links](https://developers.google.com/search/docs/crawling-indexing/links-crawlable) and [sitemap purpose](https://developers.google.com/search/docs/crawling-indexing/sitemaps/overview).

## Detection

For intended public HTML, check rendered anchors for usable href destinations and describe an evidenced broken or inaccessible navigation path. A framework's source router directive may generate a valid href, so inspect its output before flagging it. For an existing sitemap, compare declared entries with known canonical routes and explicit indexability intent; identify the concrete stale, private or nonexistent entry. Missing sitemap is not a universal violation, especially on a small well-linked site. Buttons that perform actions rather than navigation are valid. Do not demand a fixed link count or claim unseen pages are orphaned from a partial file review.

## Good example

Illustrative pattern for Quality Studio's public website:

```html
<nav aria-label="Quality Studio">
  <a href="/quality/reviews/">Reviews verstehen</a>
  <a href="/quality/criteria/">Kriterien und Metriken</a>
  <a href="/quality/models/">Modelle vergleichen</a>
</nav>
```

## Bad example

```html
<!-- The only route to the criteria page exists inside a click handler. -->
<span onclick="location.href='/quality/criteria/'">Click here</span>
```

## Change history

- 1.0.0 (2026-09-13): Added an opt-in SEO review rule with explicit audience, evidence and scope limits.
