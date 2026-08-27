import { TokenLine, TokenSpan } from './syntax-types';

export interface FindingSpanRange {
  fingerprint: string;
  start: { line: number; column: number };
  end: { line: number; column: number };
}

export type SpanState = 'selected' | 'overlap';

export interface SegmentedSpan extends TokenSpan {
  state?: SpanState;
}

function kindAt(tokens: TokenLine, offset: number): TokenSpan['kind'] {
  for (const token of tokens) {
    offset -= token.text.length;
    if (offset < 0) return token.kind;
  }
  return 'plain';
}

/**
 * Splits a tokenized line at both syntax-token and finding-range boundaries. Review-meta columns
 * are one-based inclusive UTF-16 code-unit positions; string slicing uses the equivalent zero-based,
 * end-exclusive offsets, matching FindingIdentity.ExtractSnippet on the server.
 */
export function segmentLineTokens(
  tokens: TokenLine,
  line: number,
  text: string,
  ranges: readonly FindingSpanRange[],
  selectedFingerprint: string | null,
): readonly SegmentedSpan[] {
  const intervals = ranges
    .map(range => {
      const start = line === range.start.line ? Math.min(range.start.column - 1, text.length) : 0;
      const end = line === range.end.line ? Math.min(range.end.column, text.length) : text.length;
      return end > start ? { fingerprint: range.fingerprint, start, end } : null;
    })
    .filter((value): value is { fingerprint: string; start: number; end: number } => value !== null);

  if (!intervals.length || !text.length) return tokens;

  const boundaries = new Set<number>([0, text.length]);
  for (const interval of intervals) { boundaries.add(interval.start); boundaries.add(interval.end); }
  let position = 0;
  for (const token of tokens) { boundaries.add(position); position += token.text.length; }

  const cuts = [...boundaries].sort((a, b) => a - b);
  const segments: SegmentedSpan[] = [];
  for (let index = 0; index < cuts.length - 1; index++) {
    const start = cuts[index];
    const end = cuts[index + 1];
    const covering = intervals.filter(interval => interval.start <= start && end <= interval.end);
    const selected = covering.some(interval => interval.fingerprint === selectedFingerprint);
    const state: SpanState | undefined = selected
      ? covering.some(interval => interval.fingerprint !== selectedFingerprint) ? 'overlap' : 'selected'
      : undefined;
    segments.push({ text: text.slice(start, end), kind: kindAt(tokens, start), state });
  }
  return segments;
}
