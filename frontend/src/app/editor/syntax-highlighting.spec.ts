import { TestBed } from '@angular/core/testing';

import { SyntaxHighlighting } from './syntax-highlighting';
import { SyntaxRequestMessage, SyntaxResponseMessage, TokenLine } from './syntax-types';

/** Stands in for the tokenizer worker so the lifecycle can be driven message by message. */
class FakeWorker {
  static instances: FakeWorker[] = [];
  onmessage: ((event: MessageEvent<SyntaxResponseMessage>) => void) | null = null;
  onerror: (() => void) | null = null;
  terminated = false;
  request: SyntaxRequestMessage | null = null;

  constructor() { FakeWorker.instances.push(this); }

  postMessage(request: SyntaxRequestMessage): void { this.request = request; }

  terminate(): void { this.terminated = true; }

  deliver(message: SyntaxResponseMessage): void {
    this.onmessage?.({ data: message } as MessageEvent<SyntaxResponseMessage>);
  }
}

function line(text: string): TokenLine { return [{ text, kind: 'plain' }]; }

describe('SyntaxHighlighting worker lifecycle', () => {
  let highlighting: SyntaxHighlighting;
  let originalWorker: typeof Worker;

  beforeEach(() => {
    FakeWorker.instances = [];
    originalWorker = window.Worker;
    (window as unknown as { Worker: unknown }).Worker = FakeWorker;
    highlighting = TestBed.inject(SyntaxHighlighting);
  });

  afterEach(() => {
    (window as unknown as { Worker: unknown }).Worker = originalWorker;
  });

  function callbacks() {
    return {
      chunks: [] as { startLine: number; lines: TokenLine[] }[],
      done: 0,
      errors: [] as string[],
    };
  }

  it('streams chunks and terminates the worker when tokenizing completes', () => {
    const seen = callbacks();
    highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', {
      chunk: (startLine, lines) => seen.chunks.push({ startLine, lines }),
      done: () => seen.done++,
      error: message => seen.errors.push(message),
    });

    const worker = FakeWorker.instances[0];
    expect(worker.request?.language).toBe('csharp');

    worker.deliver({ kind: 'chunk', requestId: worker.request!.requestId, startLine: 0, lines: [line('var a = 1;')] });
    worker.deliver({ kind: 'done', requestId: worker.request!.requestId, lineCount: 1 });

    expect(seen.chunks.length).toBe(1);
    expect(seen.done).toBe(1);
    expect(worker.terminated).withContext('a finished worker is not kept alive').toBeTrue();
  });

  it('cancels the previous request when another file is opened', () => {
    const first = callbacks();
    highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', {
      chunk: (startLine, lines) => first.chunks.push({ startLine, lines }),
      done: () => first.done++,
      error: message => first.errors.push(message),
    });
    const firstWorker = FakeWorker.instances[0];

    highlighting.highlight('src/B.cs', 'var b = 2;', 'csharp', { chunk: () => undefined, done: () => undefined, error: () => undefined });

    expect(firstWorker.terminated).toBeTrue();
    firstWorker.deliver({ kind: 'chunk', requestId: firstWorker.request!.requestId, startLine: 0, lines: [line('stale')] });
    expect(first.chunks.length).withContext('a superseded worker cannot write into the new file').toBe(0);
    expect(first.done).toBe(0);
  });

  it('stops delivering after the caller cancels', () => {
    const seen = callbacks();
    const cancel = highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', {
      chunk: (startLine, lines) => seen.chunks.push({ startLine, lines }),
      done: () => seen.done++,
      error: message => seen.errors.push(message),
    });
    const worker = FakeWorker.instances[0];

    cancel();
    expect(worker.terminated).toBeTrue();

    worker.deliver({ kind: 'done', requestId: worker.request!.requestId, lineCount: 1 });
    expect(seen.done).toBe(0);
  });

  it('reports a worker failure to the caller', () => {
    const seen = callbacks();
    highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', {
      chunk: () => undefined,
      done: () => seen.done++,
      error: message => seen.errors.push(message),
    });

    FakeWorker.instances[0].onerror?.();

    expect(seen.errors).toEqual(['Syntax worker failed']);
  });

  it('replays a cached file without starting a second worker', async () => {
    highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', { chunk: () => undefined, done: () => undefined, error: () => undefined });
    const worker = FakeWorker.instances[0];
    worker.deliver({ kind: 'chunk', requestId: worker.request!.requestId, startLine: 0, lines: [line('var a = 1;')] });
    worker.deliver({ kind: 'done', requestId: worker.request!.requestId, lineCount: 1 });

    const replay = callbacks();
    highlighting.highlight('src/A.cs', 'var a = 1;', 'csharp', {
      chunk: (startLine, lines) => replay.chunks.push({ startLine, lines }),
      done: () => replay.done++,
      error: message => replay.errors.push(message),
    });

    expect(FakeWorker.instances.length).withContext('the cached result is not recomputed').toBe(1);
    await new Promise(resolve => setTimeout(resolve, 5));
    expect(replay.chunks.length).toBe(1);
    expect(replay.done).toBe(1);
  });

  it('keeps only the four most recently tokenized files', () => {
    for (const path of ['a', 'b', 'c', 'd', 'e']) {
      highlighting.highlight(`src/${path}.cs`, `var ${path} = 1;`, 'csharp', { chunk: () => undefined, done: () => undefined, error: () => undefined });
      const worker = FakeWorker.instances[FakeWorker.instances.length - 1];
      worker.deliver({ kind: 'done', requestId: worker.request!.requestId, lineCount: 1 });
    }
    const workersSoFar = FakeWorker.instances.length;

    highlighting.highlight('src/e.cs', 'var e = 1;', 'csharp', { chunk: () => undefined, done: () => undefined, error: () => undefined });
    expect(FakeWorker.instances.length).withContext('the newest file is still cached').toBe(workersSoFar);

    highlighting.highlight('src/a.cs', 'var a = 1;', 'csharp', { chunk: () => undefined, done: () => undefined, error: () => undefined });
    expect(FakeWorker.instances.length).withContext('the oldest file was evicted').toBe(workersSoFar + 1);
  });
});
