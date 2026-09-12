export const LARGE_FILE_HIGHLIGHT_LIMIT_BYTES = 200 * 1024;
export const SYNTAX_CHUNK_LINES = 200;

export type SyntaxLanguage = 'csharp' | 'typescript' | 'markup' | 'css' | 'scss' | 'json' | 'markdown';

export type TokenKind =
  | 'plain'
  | 'comment'
  | 'string'
  | 'keyword'
  | 'number'
  | 'boolean'
  | 'operator'
  | 'punctuation'
  | 'function'
  | 'type'
  | 'property'
  | 'tag'
  | 'attribute'
  | 'variable'
  | 'regex'
  | 'selector'
  | 'heading'
  | 'strong'
  | 'emphasis';

export interface TokenSpan {
  text: string;
  kind: TokenKind;
}

export type TokenLine = readonly TokenSpan[];

export interface SyntaxRequestMessage {
  kind: 'tokenize';
  requestId: number;
  language: SyntaxLanguage;
  content: string;
}

export type SyntaxResponseMessage =
  | { kind: 'chunk'; requestId: number; startLine: number; lines: TokenLine[] }
  | { kind: 'done'; requestId: number; lineCount: number }
  | { kind: 'error'; requestId: number; message: string };

