/// <reference lib="webworker" />

import { SYNTAX_CHUNK_LINES, SyntaxRequestMessage, SyntaxResponseMessage } from './syntax-types';

// This must run before Prism's module is evaluated. Its browser bundle otherwise
// installs a second, incompatible JSON-string message handler inside workers.
const workerGlobal = globalThis as unknown as {
  Prism?: { manual?: boolean; disableWorkerMessageHandler?: boolean };
};
workerGlobal.Prism = {
  manual: true,
  disableWorkerMessageHandler: true,
};
const tokenizer = import('./syntax-tokenizer');

addEventListener('message', async ({ data }: MessageEvent<SyntaxRequestMessage>) => {
  if (data.kind !== 'tokenize') return;
  try {
    // Tokenize the entire source in one worker pass. This preserves grammar state for
    // multiline strings, templates, markup and comments; only delivery is chunked.
    const { tokenizeSource } = await tokenizer;
    const lines = tokenizeSource(data.content, data.language);
    for (let startLine = 0; startLine < lines.length; startLine += SYNTAX_CHUNK_LINES) {
      const response: SyntaxResponseMessage = {
        kind: 'chunk',
        requestId: data.requestId,
        startLine,
        lines: lines.slice(startLine, startLine + SYNTAX_CHUNK_LINES),
      };
      postMessage(response);
    }
    const done: SyntaxResponseMessage = { kind: 'done', requestId: data.requestId, lineCount: lines.length };
    postMessage(done);
  } catch (error) {
    const response: SyntaxResponseMessage = {
      kind: 'error',
      requestId: data.requestId,
      message: error instanceof Error ? error.message : 'Syntax tokenization failed',
    };
    postMessage(response);
  }
});
