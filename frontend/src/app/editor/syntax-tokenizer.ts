import Prism from 'prismjs';
import 'prismjs/components/prism-markup';
import 'prismjs/components/prism-css';
import 'prismjs/components/prism-scss';
import 'prismjs/components/prism-clike';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-javascript';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-markdown';
import { SyntaxLanguage, TokenKind, TokenLine, TokenSpan } from './syntax-types';

const TOKEN_KIND_BY_PRISM_TYPE: Record<string, TokenKind> = {
  plain: 'plain',
  comment: 'comment',
  prolog: 'comment',
  cdata: 'comment',
  string: 'string',
  char: 'string',
  code: 'string',
  url: 'string',
  entity: 'string',
  'attr-value': 'string',
  inserted: 'string',
  keyword: 'keyword',
  atrule: 'keyword',
  number: 'number',
  constant: 'number',
  symbol: 'number',
  boolean: 'boolean',
  operator: 'operator',
  punctuation: 'punctuation',
  function: 'function',
  builtin: 'type',
  'class-name': 'type',
  property: 'property',
  tag: 'tag',
  doctype: 'tag',
  'attr-name': 'attribute',
  variable: 'variable',
  regex: 'regex',
  selector: 'selector',
  title: 'heading',
  important: 'strong',
  bold: 'strong',
  italic: 'emphasis',
};

// Prism 1.x recognizes C# verbatim/interpolated strings but predates raw string literals.
// Insert the raw form ahead of its other string rules so delimiter length and multiline
// content are handled as one whole-file token.
Prism.languages.insertBefore('csharp', 'interpolation-string', {
  'raw-string': {
    pattern: /\$*("{3,})[\s\S]*?\1/,
    greedy: true,
    alias: 'string',
  },
});

function tokenKind(token: Prism.Token, inherited: TokenKind): TokenKind {
  const direct = TOKEN_KIND_BY_PRISM_TYPE[token.type];
  if (direct) return direct;
  const aliases = Array.isArray(token.alias) ? token.alias : token.alias ? [token.alias] : [];
  return aliases.map(alias => TOKEN_KIND_BY_PRISM_TYPE[alias]).find(Boolean) ?? inherited;
}

function append(lines: TokenSpan[][], value: string, kind: TokenKind): void {
  const parts = value.split('\n');
  parts.forEach((part, index) => {
    if (part) {
      const line = lines.at(-1)!;
      const previous = line.at(-1);
      if (previous?.kind === kind) previous.text += part;
      else line.push({ text: part, kind });
    }
    if (index < parts.length - 1) lines.push([]);
  });
}

function flatten(stream: Prism.TokenStream, lines: TokenSpan[][], inherited: TokenKind): void {
  if (typeof stream === 'string') {
    append(lines, stream, inherited);
    return;
  }
  if (Array.isArray(stream)) {
    stream.forEach(item => flatten(item, lines, inherited));
    return;
  }
  flatten(stream.content, lines, tokenKind(stream, inherited));
}

export function tokenizeSource(content: string, language: SyntaxLanguage): TokenLine[] {
  const grammar = Prism.languages[language];
  if (!grammar) throw new Error(`No tokenizer registered for ${language}`);

  const lines: TokenSpan[][] = [[]];
  flatten(Prism.tokenize(content.replace(/\r\n/g, '\n'), grammar), lines, 'plain');
  return lines.map(line => line.length ? line : [{ text: '', kind: 'plain' }]);
}
