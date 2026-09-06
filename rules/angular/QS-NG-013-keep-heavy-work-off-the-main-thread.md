---
id: QS-NG-013
version: 1.0.0
title: Move heavy work off the main thread, chunked and cancellable
technology: angular
kinds: [performance]
category: main-thread
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

Work whose cost scales with repository data — tokenizing a file, diffing, indexing — runs in a
worker, starts after the first paint, proceeds in bounded chunks, and is cancelled when its subject
changes. At most one such job is in flight per subject; a superseded job is cancelled, not awaited.

## Rationale

Whole-file main-thread highlighting is prohibited in this codebase for a measurable reason:
tokenization runs in a cancellable single-concurrency worker in 200-line chunks with a 200 kB cap,
which is what keeps opening a large file inside the under-100 ms transition budget. Without
chunking and cancellation, scrolling through five files queues five full tokenizations and the pane
stops responding to the sixth.

## Detection

Look for parsing, tokenizing, or diffing loops over whole-file content on the main thread, for
`await` of such work directly in a component initializer, and for a worker call with no cancellation
when its input changes. Work bounded by a small constant, or done once at start-up, is not a
violation.

## Good example

```ts
// One job per subject: the previous one is cancelled rather than awaited.
this.pending?.cancel();
this.pending = this.highlighter.tokenize(path, text, { chunkLines: 200, maxBytes: 200_000 });
```

## Bad example

```ts
ngOnInit() {
  // Whole-file tokenization on the main thread, on every open, uncancellable
  this.tokens = tokenizeEveryLine(this.file.text);
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the chunked, cancellable, single-concurrency
  syntax worker described in `frontend/PERF.md`.
