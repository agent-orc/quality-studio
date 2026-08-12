import { ReviewFinding } from '../quality-api';
import { TokenKind, TokenLine } from './syntax-types';

export interface FindingSpanSegment {
  text: string;
  kind: TokenKind;
  startColumn: number;
  endColumn: number;
  findings: ReviewFinding[];
  selected: boolean;
  overlap: boolean;
}

interface LineRange {
  start: number;
  end: number;
  finding: ReviewFinding;
}

export function segmentFindingSpans(
  tokens: TokenLine,
  line: number,
  path: string,
  findings: readonly ReviewFinding[],
  selectedFindingKey?: string | null,
): FindingSpanSegment[] {
  const textLength = tokens.reduce((length, token) => length + token.text.length, 0);
  const ranges = findings.flatMap(finding => finding.suppression ? [] : finding.locations
    .filter(location => location.path === path && location.range &&
      line >= location.range.start.line && line <= location.range.end.line)
    .map(location => {
      const range = location.range!;
      return {
        start: range.start.line === line ? Math.max(0, range.start.column - 1) : 0,
        end: range.end.line === line ? Math.min(textLength, range.end.column) : textLength,
        finding,
      };
    })
    .filter(range => range.end > range.start));

  const result: FindingSpanSegment[] = [];
  let tokenStart = 0;
  for (const token of tokens) {
    const tokenEnd = tokenStart + token.text.length;
    const boundaries = new Set([tokenStart, tokenEnd]);
    for (const range of ranges) {
      if (range.start > tokenStart && range.start < tokenEnd) boundaries.add(range.start);
      if (range.end > tokenStart && range.end < tokenEnd) boundaries.add(range.end);
    }
    const sorted = [...boundaries].sort((left, right) => left - right);
    for (let index = 0; index < sorted.length - 1; index++) {
      const start = sorted[index];
      const end = sorted[index + 1];
      if (end <= start) continue;
      const active = ranges.filter(range => range.start < end && range.end > start).map(range => range.finding);
      result.push({
        text: token.text.slice(start - tokenStart, end - tokenStart),
        kind: token.kind,
        startColumn: start + 1,
        endColumn: end,
        findings: active,
        selected: !!selectedFindingKey && active.some(finding => (finding.fingerprint ?? finding.id) === selectedFindingKey),
        overlap: active.length > 1,
      });
    }
    tokenStart = tokenEnd;
  }
  return result;
}
