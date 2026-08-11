import { ReviewFinding } from '../quality-api';
import { TokenKind, TokenLine } from './syntax-types';

export interface FindingTokenSegment {
  text: string;
  kind: TokenKind;
  startColumn: number;
  endColumn: number;
  findings: ReviewFinding[];
  selected: boolean;
  overlap: boolean;
  endOfLine: boolean;
}

interface FindingSpan {
  finding: ReviewFinding;
  start: number;
  end: number;
}

/**
 * Splits a syntax-tokenized line at every finding boundary. Review-meta columns are
 * one-based and inclusive; JavaScript and .NET both count UTF-16 code units, so the
 * indices stay aligned for non-BMP source text as well.
 */
export function segmentFindingTokens(
  lineNumber: number,
  lineText: string,
  tokens: TokenLine,
  findings: readonly ReviewFinding[],
  path: string,
  selectedIdentity: string | null,
): FindingTokenSegment[] {
  const spans = findingSpans(lineNumber, lineText, findings, path);
  const tokenSpans = tokenOffsets(tokens, lineText);
  const boundaries = new Set<number>([0, lineText.length]);
  for (const token of tokenSpans) boundaries.add(token.end);
  for (const span of spans) {
    boundaries.add(span.start);
    boundaries.add(span.end);
  }

  const sorted = [...boundaries].sort((left, right) => left - right);
  const result: FindingTokenSegment[] = [];
  for (let index = 0; index < sorted.length - 1; index++) {
    const start = sorted[index];
    const end = sorted[index + 1];
    if (end <= start) continue;
    const active = distinctFindings(spans
      .filter(span => span.start < end && span.end > start)
      .map(span => span.finding));
    result.push(segment(
      lineText.slice(start, end),
      tokenSpans.find(token => token.start <= start && token.end > start)?.kind ?? 'plain',
      start,
      end,
      active,
      selectedIdentity,
      false,
    ));
  }

  const endOfLine = distinctFindings(spans
    .filter(span => span.start === lineText.length && span.end === lineText.length)
    .map(span => span.finding));
  if (endOfLine.length) {
    result.push(segment('', 'plain', lineText.length, lineText.length, endOfLine, selectedIdentity, true));
  }
  return result;
}

function findingSpans(
  lineNumber: number,
  lineText: string,
  findings: readonly ReviewFinding[],
  path: string,
): FindingSpan[] {
  const result: FindingSpan[] = [];
  for (const finding of findings) {
    for (const location of finding.locations) {
      const range = location.range;
      if (location.path !== path || !range || lineNumber < range.start.line || lineNumber > range.end.line) continue;
      const start = lineNumber === range.start.line
        ? clamp(range.start.column - 1, 0, lineText.length)
        : 0;
      const end = lineNumber === range.end.line
        ? clamp(range.end.column, 0, lineText.length)
        : lineText.length;
      if (end >= start) result.push({ finding, start, end });
    }
  }
  return result;
}

function tokenOffsets(tokens: TokenLine, lineText: string): Array<{ start: number; end: number; kind: TokenKind }> {
  const result: Array<{ start: number; end: number; kind: TokenKind }> = [];
  let offset = 0;
  for (const token of tokens) {
    const end = Math.min(lineText.length, offset + token.text.length);
    if (end > offset) result.push({ start: offset, end, kind: token.kind });
    offset = end;
  }
  if (offset < lineText.length) result.push({ start: offset, end: lineText.length, kind: 'plain' });
  return result;
}

function segment(
  text: string,
  kind: TokenKind,
  start: number,
  end: number,
  findings: ReviewFinding[],
  selectedIdentity: string | null,
  endOfLine: boolean,
): FindingTokenSegment {
  return {
    text,
    kind,
    startColumn: start + 1,
    endColumn: endOfLine ? end + 1 : end,
    findings,
    selected: findings.some(finding => identity(finding) === selectedIdentity),
    overlap: findings.length > 1,
    endOfLine,
  };
}

function distinctFindings(findings: ReviewFinding[]): ReviewFinding[] {
  return findings.filter((finding, index) => findings.findIndex(candidate => identity(candidate) === identity(finding)) === index);
}

function identity(finding: ReviewFinding): string {
  return finding.fingerprint ?? finding.id;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
}
