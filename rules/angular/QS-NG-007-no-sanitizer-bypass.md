---
id: QS-NG-007
version: 1.0.0
title: Render text as text; never bypass Angular's sanitizer
technology: angular
kinds: [security]
category: rendering-safety
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

Content that did not come from this component's own template — repository source, a model's prose,
a finding title, anything fetched — is rendered through interpolation or property binding. No
`innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval`, or
`DomSanitizer.bypassSecurityTrust*`. If markup is genuinely required, render it from a parsed model
into elements, not from a string.

## Rationale

There is no `innerHTML` or sanitizer bypass anywhere under `frontend/src` today: the editor renders
every line as `{{ segment.text }}` inside spans and the review panel renders model-authored
descriptions as `<p>{{ finding.description }}</p>`, and the HTML report encodes with
`WebUtility.HtmlEncode` before writing. That is the property worth keeping, because the strings in
question are exactly the untrusted ones — source code a repository controls and prose a model wrote.

## Detection

Search the component and its template for `innerHTML`, `outerHTML`, `insertAdjacentHTML`,
`bypassSecurityTrust`, `DomSanitizer`, `document.write`, `eval(`, and `new Function(`. A
`[innerText]` or `[textContent]` binding is not a violation, and neither is `[attr.*]` on a value
the sanitizer still sees.

## Good example

```html
<!-- frontend/src/app/editor/editor.html -->
<code>@for (segment of segmentedLine(row.number, row.text, row.findings); track $index) {
  <span [class]="segmentClass(segment)" [attr.aria-label]="segmentAriaLabel(segment, row.number)">{{ segment.text }}</span>
}</code>
```

## Bad example

```html
<!-- The description is model-authored prose; this hands it to the HTML parser. -->
<p [innerHTML]="sanitizer.bypassSecurityTrustHtml(finding.description)"></p>
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, recording the property a repository-wide search already
  establishes for `frontend/src` so a regression is visible as one.
