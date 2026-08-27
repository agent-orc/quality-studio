import { TokenLine, TokenSpan } from './syntax-types';

export interface FindingSpanRange {
  fingerprint: string;
  start: { line: number; column: number };
  end: { line: number; column: number };
}

export type SpanState = 'plain' | 'selected' | 'overlap';

export interface SegmentedSpan extends TokenSpan {
  state: SpanState;
}

/**
 * Splits syntax tokens at inclusive, one-based finding columns. JavaScript string indexing and the
 * server's C# indexing both use UTF-16 code units, so the rendered boundaries match validated ranges.
 */
export function segmentLineTokens(
  tokens: TokenLine,
  line: number,
  ranges: readonly FindingSpanRange[],
  selectedFingerprint: string | null,
): SegmentedSpan[] {
  const segments: SegmentedSpan[] = [];
  let offset = 0;
  for (const token of tokens) {
    for (let index = 0; index < token.text.length; index++) {
      const column = offset + index + 1;
      const covering = ranges.filter(range =>
        (line > range.start.line || line === range.start.line && column >= range.start.column) &&
        (line < range.end.line || line === range.end.line && column <= range.end.column));
      const state: SpanState = covering.length > 1 ? 'overlap'
        : covering[0]?.fingerprint === selectedFingerprint ? 'selected'
        : 'plain';
      const previous = segments.at(-1);
      const character = token.text[index];
      if (previous?.kind === token.kind && previous.state === state) previous.text += character;
      else segments.push({ text: character, kind: token.kind, state });
    }
    offset += token.text.length;
  }
  return segments.length ? segments : tokens.map(token => ({ ...token, state: 'plain' }));
}
