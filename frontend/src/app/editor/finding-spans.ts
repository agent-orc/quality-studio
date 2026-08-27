import { TokenKind, TokenLine } from './syntax-types';

/** A 1-based, inclusive column range on one line, tagged with the value it highlights. */
export interface ColumnRange<T> {
  startColumn: number;
  endColumn: number;
  value: T;
}

export interface RenderSpan<T> {
  text: string;
  kind: TokenKind;
  values: T[];
}

/**
 * Clips a finding's (possibly multi-line) range to the part that falls on `line`, using the same
 * 1-based inclusive column convention as `FindingIdentity.ExtractSnippet` on the backend.
 * Returns null when the range does not touch this line.
 */
export function clipRangeToLine(
  range: { start: { line: number; column: number }; end: { line: number; column: number } },
  line: number,
  lineLength: number,
): { startColumn: number; endColumn: number } | null {
  if (line < range.start.line || line > range.end.line) return null;
  const startColumn = line === range.start.line ? Math.max(1, range.start.column) : 1;
  const endColumn = line === range.end.line ? Math.min(range.end.column, lineLength) : lineLength;
  return endColumn < startColumn ? null : { startColumn, endColumn };
}

/**
 * Splits syntax tokens at finding column boundaries so each output span carries one token kind
 * and the set of values (e.g. findings) whose range covers it. Token-kind boundaries are always
 * respected, so a span never straddles two different syntax colors.
 */
export function segmentTokensByColumns<T>(tokens: TokenLine, ranges: ColumnRange<T>[]): RenderSpan<T>[] {
  if (!ranges.length) return tokens.map(token => ({ text: token.text, kind: token.kind, values: [] }));

  const boundaries = new Set<number>();
  let column = 1;
  boundaries.add(column);
  for (const token of tokens) { column += token.text.length; boundaries.add(column); }
  const lineEnd = column;
  for (const range of ranges) {
    boundaries.add(Math.max(1, Math.min(range.startColumn, lineEnd)));
    boundaries.add(Math.max(1, Math.min(range.endColumn + 1, lineEnd)));
  }
  const cuts = [...boundaries].sort((left, right) => left - right);

  const spans: RenderSpan<T>[] = [];
  let tokenIndex = 0;
  let tokenColumnStart = 1;
  for (let i = 0; i < cuts.length - 1; i++) {
    const segmentStart = cuts[i];
    const segmentEnd = cuts[i + 1];
    if (segmentEnd <= segmentStart) continue;
    while (tokenIndex < tokens.length && tokenColumnStart + tokens[tokenIndex].text.length <= segmentStart) {
      tokenColumnStart += tokens[tokenIndex].text.length;
      tokenIndex++;
    }
    const token = tokens[tokenIndex];
    if (!token) break;
    const text = token.text.slice(segmentStart - tokenColumnStart, segmentEnd - tokenColumnStart);
    const values = ranges
      .filter(range => range.startColumn <= segmentStart && range.endColumn + 1 >= segmentEnd)
      .map(range => range.value);
    spans.push({ text, kind: token.kind, values });
  }
  return spans;
}
