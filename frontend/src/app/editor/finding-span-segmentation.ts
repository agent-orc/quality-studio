import { TokenLine, TokenSpan } from './syntax-types';

export interface FindingSpanRange {
  fingerprint: string;
  start: { line: number; column: number };
  end: { line: number; column: number };
}

export type SpanState = 'plain' | 'selected' | 'overlap';

export interface SegmentedSpan extends TokenSpan {
  state: SpanState;
  fingerprints: readonly string[];
}

/**
 * Splits a tokenized line into segments aligned to both syntax-token boundaries and finding-range
 * column boundaries, so a single token can carry a mix of plain/selected/overlap sub-spans without
 * losing its highlight kind.
 *
 * Columns are 1-based, as the finding-location contract in docs/concept.md defines them, which is
 * why `start.column - 1` converts to a UTF-16 offset. `end.column` is used as the exclusive offset
 * directly, matching the server's own snippet extraction in FindingIdentity.ExtractSnippet, so a
 * highlighted span and the recorded evidence excerpt always cover the same characters. Boundaries
 * never depend on grapheme clustering.
 */
export function segmentLineTokens(
  tokens: TokenLine,
  line: number,
  text: string,
  ranges: readonly FindingSpanRange[],
  selectedFingerprint: string | null,
): SegmentedSpan[] {
  const intervals: [string, number, number][] = [];
  const cuts = [0, text.length];
  for (const range of ranges) {
    if (line < range.start.line || line > range.end.line) continue;
    const start = Math.max(0, Math.min(line === range.start.line ? range.start.column - 1 : 0, text.length));
    const end = Math.max(0, Math.min(line === range.end.line ? range.end.column : text.length, text.length));
    if (end > start) { intervals.push([range.fingerprint, start, end]); cuts.push(start, end); }
  }
  if (!intervals.length || !text.length) return tokens.map(token => ({ ...token, state: 'plain', fingerprints: [] }));

  const tokenEnds: number[] = [];
  let position = 0;
  for (const token of tokens) { position += token.text.length; tokenEnds.push(position); cuts.push(position); }

  const boundaries = [...new Set(cuts)].sort((a, b) => a - b);
  return boundaries.slice(0, -1).map((start, index) => {
    const end = boundaries[index + 1];
    const covering = intervals.filter(interval => interval[1] <= start && end <= interval[2]);
    const fingerprints = covering.map(interval => interval[0]);
    const state: SpanState = covering.length >= 2 ? 'overlap'
      : covering[0]?.[0] === selectedFingerprint ? 'selected'
      : 'plain';
    const tokenIndex = tokenEnds.findIndex(tokenEnd => start < tokenEnd);
    return { text: text.slice(start, end), kind: tokens[tokenIndex < 0 ? tokens.length - 1 : tokenIndex]?.kind ?? 'plain', state, fingerprints };
  });
}
