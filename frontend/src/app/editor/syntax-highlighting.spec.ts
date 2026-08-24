import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { HighlightCallbacks, SyntaxHighlighting } from './syntax-highlighting';
import { SYNTAX_CHUNK_LINES, SyntaxLanguage, SyntaxRequestMessage, SyntaxResponseMessage, TokenLine } from './syntax-types';

class FakeWorker {
  static instances: FakeWorker[] = [];
  onmessage: ((event: MessageEvent<SyntaxResponseMessage>) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  readonly posted: SyntaxRequestMessage[] = [];
  terminated = 0;

  constructor(readonly url: string | URL, readonly options?: WorkerOptions) {
    FakeWorker.instances.push(this);
  }

  postMessage(message: SyntaxRequestMessage): void {
    this.posted.push(message);
  }

  terminate(): void {
    this.terminated++;
  }

  get requestId(): number {
    return this.posted[0].requestId;
  }

  emit(message: SyntaxResponseMessage): void {
    this.onmessage?.({ data: message } as MessageEvent<SyntaxResponseMessage>);
  }

  finish(lines: TokenLine[]): void {
    for (let startLine = 0; startLine < lines.length; startLine += SYNTAX_CHUNK_LINES) {
      this.emit({ kind: 'chunk', requestId: this.requestId, startLine, lines: lines.slice(startLine, startLine + SYNTAX_CHUNK_LINES) });
    }
    this.emit({ kind: 'done', requestId: this.requestId, lineCount: lines.length });
  }
}

function sourceLines(count: number): TokenLine[] {
  return Array.from({ length: count }, (_, index) => [{ text: `line ${index}`, kind: 'plain' as const }]);
}

function callbackSpies(): HighlightCallbacks & {
  chunk: jasmine.Spy; done: jasmine.Spy; error: jasmine.Spy;
} {
  return {
    chunk: jasmine.createSpy('chunk'),
    done: jasmine.createSpy('done'),
    error: jasmine.createSpy('error'),
  };
}

function chunkStarts(callbacks: { chunk: jasmine.Spy }): number[] {
  return callbacks.chunk.calls.allArgs().map(args => args[0] as number);
}

function chunkSizes(callbacks: { chunk: jasmine.Spy }): number[] {
  return callbacks.chunk.calls.allArgs().map(args => (args[1] as TokenLine[]).length);
}

describe('SyntaxHighlighting worker orchestration', () => {
  let service: SyntaxHighlighting;
  let nativeWorker: typeof Worker;

  beforeEach(() => {
    nativeWorker = window.Worker;
    FakeWorker.instances = [];
    (window as unknown as { Worker: unknown }).Worker = FakeWorker;
    TestBed.configureTestingModule({});
    service = TestBed.inject(SyntaxHighlighting);
  });

  afterEach(() => {
    (window as unknown as { Worker: unknown }).Worker = nativeWorker;
  });

  function latestWorker(): FakeWorker {
    return FakeWorker.instances[FakeWorker.instances.length - 1];
  }

  function cache(path: string, content: string, language: SyntaxLanguage = 'csharp', lines = sourceLines(2)): void {
    service.highlight(path, content, language, callbackSpies());
    latestWorker().finish(lines);
  }

  it('posts one tokenize request per file and streams the worker chunks to the caller', () => {
    const callbacks = callbackSpies();

    service.highlight('src/A.cs', 'a\nb', 'csharp', callbacks);

    expect(FakeWorker.instances.length).toBe(1);
    const worker = latestWorker();
    expect(worker.posted.length).toBe(1);
    expect(worker.posted[0]).toEqual(jasmine.objectContaining({ kind: 'tokenize', language: 'csharp', content: 'a\nb' }));

    const lines = sourceLines(2);
    worker.emit({ kind: 'chunk', requestId: worker.requestId, startLine: 0, lines });
    expect(callbacks.chunk).toHaveBeenCalledWith(0, lines);
    expect(worker.terminated).toBe(0);

    worker.emit({ kind: 'done', requestId: worker.requestId, lineCount: 2 });
    expect(callbacks.done).toHaveBeenCalledWith(2);
    expect(callbacks.error).not.toHaveBeenCalled();
    expect(worker.terminated).toBe(1);
  });

  it('runs a single worker: a new request terminates and silences the previous one', () => {
    const first = callbackSpies();
    service.highlight('src/A.cs', 'a', 'csharp', first);
    const firstWorker = latestWorker();

    const second = callbackSpies();
    service.highlight('src/B.cs', 'b', 'csharp', second);
    const secondWorker = latestWorker();

    expect(FakeWorker.instances.length).toBe(2);
    expect(firstWorker.terminated).toBe(1);

    firstWorker.finish(sourceLines(3));
    firstWorker.onerror?.(new Event('error'));
    expect(first.chunk).not.toHaveBeenCalled();
    expect(first.done).not.toHaveBeenCalled();
    expect(first.error).not.toHaveBeenCalled();

    secondWorker.finish(sourceLines(1));
    expect(second.chunk).toHaveBeenCalledTimes(1);
    expect(second.done).toHaveBeenCalledWith(1);
  });

  it('stops delivery and terminates the worker when the caller cancels', () => {
    const callbacks = callbackSpies();
    const cancel = service.highlight('src/A.cs', 'a', 'csharp', callbacks);
    const worker = latestWorker();

    cancel();

    expect(worker.terminated).toBe(1);
    worker.finish(sourceLines(2));
    expect(callbacks.chunk).not.toHaveBeenCalled();
    expect(callbacks.done).not.toHaveBeenCalled();
  });

  it('ignores a stale cancel handle so it cannot kill the request that replaced it', () => {
    const cancelFirst = service.highlight('src/A.cs', 'a', 'csharp', callbackSpies());
    const callbacks = callbackSpies();
    service.highlight('src/B.cs', 'b', 'csharp', callbacks);
    const currentWorker = latestWorker();

    cancelFirst();

    expect(currentWorker.terminated).toBe(0);
    currentWorker.finish(sourceLines(1));
    expect(callbacks.done).toHaveBeenCalledWith(1);
  });

  it('replays a completed file from cache without a worker, chunked the same way', fakeAsync(() => {
    const lines = sourceLines(SYNTAX_CHUNK_LINES * 2 + 50);
    service.highlight('src/A.cs', 'source', 'csharp', callbackSpies());
    latestWorker().finish(lines);
    expect(FakeWorker.instances.length).toBe(1);

    const replay = callbackSpies();
    service.highlight('src/A.cs', 'source', 'csharp', replay);

    expect(FakeWorker.instances.length).toBe(1);
    expect(replay.chunk).not.toHaveBeenCalled();

    tick();

    expect(chunkStarts(replay)).toEqual([0, SYNTAX_CHUNK_LINES, SYNTAX_CHUNK_LINES * 2]);
    expect(chunkSizes(replay)).toEqual([SYNTAX_CHUNK_LINES, SYNTAX_CHUNK_LINES, 50]);
    expect(replay.chunk.calls.first().args[1]).toEqual(lines.slice(0, SYNTAX_CHUNK_LINES));
    expect(replay.done).toHaveBeenCalledWith(lines.length);
  }));

  it('drops a cached replay that is cancelled before its first frame', fakeAsync(() => {
    service.highlight('src/A.cs', 'source', 'csharp', callbackSpies());
    latestWorker().finish(sourceLines(3));

    const replay = callbackSpies();
    const cancel = service.highlight('src/A.cs', 'source', 'csharp', replay);
    cancel();
    tick();

    expect(replay.chunk).not.toHaveBeenCalled();
    expect(replay.done).not.toHaveBeenCalled();
  }));

  it('abandons a cached replay when a different file is requested mid-flight', fakeAsync(() => {
    service.highlight('src/A.cs', 'source', 'csharp', callbackSpies());
    latestWorker().finish(sourceLines(SYNTAX_CHUNK_LINES * 2));

    const replay = callbackSpies();
    service.highlight('src/A.cs', 'source', 'csharp', replay);
    service.highlight('src/B.cs', 'other', 'csharp', callbackSpies());
    tick();

    expect(replay.chunk).not.toHaveBeenCalled();
    expect(replay.done).not.toHaveBeenCalled();
    expect(FakeWorker.instances.length).toBe(2);
    latestWorker().finish(sourceLines(1));
  }));

  it('re-tokenizes when the content or the language changed under the same path', () => {
    cache('src/A.cs', 'source');

    service.highlight('src/A.cs', 'edited', 'csharp', callbackSpies());
    expect(FakeWorker.instances.length).toBe(2);
    latestWorker().finish(sourceLines(1));

    service.highlight('src/A.cs', 'edited', 'typescript', callbackSpies());
    expect(FakeWorker.instances.length).toBe(3);
  });

  it('keeps only the four most recently completed files', fakeAsync(() => {
    for (const path of ['a', 'b', 'c', 'd', 'e']) cache(path, `${path} source`);
    expect(FakeWorker.instances.length).toBe(5);

    for (const path of ['b', 'c', 'd', 'e']) {
      service.highlight(path, `${path} source`, 'csharp', callbackSpies());
      expect(FakeWorker.instances.length).withContext(`${path} should still be cached`).toBe(5);
    }

    const cancel = service.highlight('a', 'a source', 'csharp', callbackSpies());
    expect(FakeWorker.instances.length).toBe(6);
    cancel();
  }));

  it('reports a worker error, terminates it, and caches nothing', () => {
    const callbacks = callbackSpies();
    service.highlight('src/A.cs', 'source', 'csharp', callbacks);
    const worker = latestWorker();

    worker.emit({ kind: 'error', requestId: worker.requestId, message: 'grammar exploded' });

    expect(callbacks.error).toHaveBeenCalledWith('grammar exploded');
    expect(callbacks.done).not.toHaveBeenCalled();
    expect(worker.terminated).toBe(1);

    service.highlight('src/A.cs', 'source', 'csharp', callbackSpies());
    expect(FakeWorker.instances.length).toBe(2);
    latestWorker().finish(sourceLines(1));
  });

  it('reports a worker that fails to start', () => {
    const callbacks = callbackSpies();
    service.highlight('src/A.cs', 'source', 'csharp', callbacks);
    const worker = latestWorker();

    worker.onerror?.(new Event('error'));

    expect(callbacks.error).toHaveBeenCalledWith('Syntax worker failed');
    expect(worker.terminated).toBe(1);
  });
});
