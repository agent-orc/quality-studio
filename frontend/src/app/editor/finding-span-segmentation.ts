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
 * Ranges are one-based, inclusive at both ends (matching FindingIdentity.ExtractSnippet on the
 * server), so an inclusive end column c is the half-open upper bound c in zero-based text indices.
 */
function intervalForLine(range: FindingSpanRange, line: number, lineLength: number): { start: number; end: number } | null {
  if (line < range.start.line || line > range.end.line) return null;
  const start = line === range.start.line ? range.start.column - 1 : 0;
  const end = line === range.end.line ? range.end.column : lineLength;
  return { start: Math.max(0, Math.min(start, lineLength)), end: Math.max(0, Math.min(end, lineLength)) };
}

function kindAt(tokens: TokenLine, offset: number): TokenSpan['kind'] {
  let position = 0;
  for (const token of tokens) {
    if (offset < position + token.text.length) return token.kind;
    position += token.text.length;
  }
  return tokens.at(-1)?.kind ?? 'plain';
}

/**
 * Splits a tokenized line into segments aligned to both syntax-token boundaries and finding-range
 * column boundaries, so a single token can carry a mix of plain/selected/overlap sub-spans without
 * losing its highlight kind. Boundaries never depend on grapheme clustering: columns are zero-based
 * UTF-16 code unit offsets, identical to the server's C# string indexing.
 */
export function segmentLineTokens(
  tokens: TokenLine,
  line: number,
  text: string,
  ranges: readonly FindingSpanRange[],
  selectedFingerprint: string | null,
): SegmentedSpan[] {
  const intervals = ranges
    .map(range => {
      const interval = intervalForLine(range, line, text.length);
      return interval && interval.end > interval.start ? { fingerprint: range.fingerprint, ...interval } : null;
    })
    .filter((value): value is { fingerprint: string; start: number; end: number } => value !== null);

  if (!intervals.length || !text.length) {
    return tokens.map(token => ({ ...token, state: 'plain', fingerprints: [] }));
  }

  const boundaries = new Set<number>([0, text.length]);
  for (const interval of intervals) { boundaries.add(interval.start); boundaries.add(interval.end); }
  let position = 0;
  for (const token of tokens) { boundaries.add(position); position += token.text.length; }
  boundaries.add(position);

  const cuts = [...boundaries].filter(cut => cut >= 0 && cut <= text.length).sort((a, b) => a - b);
  const segments: SegmentedSpan[] = [];
  for (let index = 0; index < cuts.length - 1; index++) {
    const start = cuts[index];
    const end = cuts[index + 1];
    if (end <= start) continue;
    const covering = intervals.filter(interval => interval.start <= start && end <= interval.end);
    const fingerprints = covering.map(interval => interval.fingerprint);
    const state: SpanState = covering.length >= 2 ? 'overlap'
      : covering.length === 1 && covering[0].fingerprint === selectedFingerprint ? 'selected'
      : 'plain';
    segments.push({ text: text.slice(start, end), kind: kindAt(tokens, start), state, fingerprints });
  }
  return segments;
}
