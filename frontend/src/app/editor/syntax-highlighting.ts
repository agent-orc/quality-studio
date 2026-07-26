import { Injectable } from '@angular/core';
import { SYNTAX_CHUNK_LINES, SyntaxLanguage, SyntaxRequestMessage, SyntaxResponseMessage, TokenLine } from './syntax-types';

interface CachedHighlight {
  content: string;
  language: SyntaxLanguage;
  lines: TokenLine[];
}

export interface HighlightCallbacks {
  chunk(startLine: number, lines: TokenLine[]): void;
  done(lineCount: number): void;
  error(message: string): void;
}

@Injectable({ providedIn: 'root' })
export class SyntaxHighlighting {
  private readonly cache = new Map<string, CachedHighlight>();
  private worker: Worker | null = null;
  private requestId = 0;
  private cachedDeliveryTimer: ReturnType<typeof setTimeout> | null = null;

  highlight(path: string, content: string, language: SyntaxLanguage, callbacks: HighlightCallbacks): () => void {
    this.cancel();
    const requestId = ++this.requestId;
    const cached = this.cache.get(path);
    if (cached?.content === content && cached.language === language) {
      this.deliverCached(requestId, cached.lines, callbacks);
      return () => this.cancelRequest(requestId);
    }

    const lines: TokenLine[] = [];
    const worker = new Worker(new URL('./syntax.worker', import.meta.url), { type: 'module', name: 'quality-studio-syntax' });
    this.worker = worker;
    worker.onmessage = ({ data }: MessageEvent<SyntaxResponseMessage>) => {
      if (data.requestId !== this.requestId) return;
      if (data.kind === 'chunk') {
        lines.splice(data.startLine, data.lines.length, ...data.lines);
        callbacks.chunk(data.startLine, data.lines);
      } else if (data.kind === 'done') {
        this.remember(path, { content, language, lines });
        this.worker?.terminate();
        this.worker = null;
        callbacks.done(data.lineCount);
      } else {
        this.worker?.terminate();
        this.worker = null;
        callbacks.error(data.message);
      }
    };
    worker.onerror = () => {
      if (requestId !== this.requestId) return;
      this.worker?.terminate();
      this.worker = null;
      callbacks.error('Syntax worker failed');
    };

    const request: SyntaxRequestMessage = { kind: 'tokenize', requestId, language, content };
    worker.postMessage(request);
    return () => this.cancelRequest(requestId);
  }

  private deliverCached(requestId: number, lines: TokenLine[], callbacks: HighlightCallbacks, startLine = 0): void {
    this.cachedDeliveryTimer = setTimeout(() => {
      if (requestId !== this.requestId) return;
      const chunk = lines.slice(startLine, startLine + SYNTAX_CHUNK_LINES);
      callbacks.chunk(startLine, chunk);
      const nextLine = startLine + chunk.length;
      if (nextLine < lines.length) this.deliverCached(requestId, lines, callbacks, nextLine);
      else {
        this.cachedDeliveryTimer = null;
        callbacks.done(lines.length);
      }
    });
  }

  private remember(path: string, highlight: CachedHighlight): void {
    this.cache.delete(path);
    this.cache.set(path, highlight);
    const oldest = this.cache.keys().next().value as string | undefined;
    if (this.cache.size > 4 && oldest) this.cache.delete(oldest);
  }

  private cancelRequest(requestId: number): void {
    if (requestId === this.requestId) this.cancel();
  }

  private cancel(): void {
    this.requestId++;
    this.worker?.terminate();
    this.worker = null;
    if (this.cachedDeliveryTimer !== null) clearTimeout(this.cachedDeliveryTimer);
    this.cachedDeliveryTimer = null;
  }
}
