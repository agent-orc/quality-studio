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
  endOfLine: boolean;
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
  const lineText = tokens.map(token => token.text).join('');
  const textLength = lineText.length;
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
    .filter(range => range.end >= range.start));

  let tokenOffset = 0;
  const tokenRanges = tokens.map(token => {
    const start = tokenOffset;
    tokenOffset += token.text.length;
    return { token, start, end: tokenOffset };
  });
  const boundaries = new Set<number>([0, textLength]);
  for (const token of tokenRanges) boundaries.add(token.end);
  for (const range of ranges) { boundaries.add(range.start); boundaries.add(range.end); }

  const result: FindingSpanSegment[] = [];
  const sorted = [...boundaries].sort((left, right) => left - right);
  for (let index = 0; index < sorted.length - 1; index++) {
    const start = sorted[index];
    const end = sorted[index + 1];
    if (end <= start) continue;
    const active = distinctFindings(ranges.filter(range => range.start < end && range.end > start)
      .map(range => range.finding));
    result.push({
      text: lineText.slice(start, end),
      kind: tokenRanges.find(token => token.start <= start && token.end > start)?.token.kind ?? 'plain',
      startColumn: start + 1,
      endColumn: end,
      findings: active,
      selected: !!selectedFindingKey && active.some(finding => identity(finding) === selectedFindingKey),
      overlap: active.length > 1,
      endOfLine: false,
    });
  }

  const endOfLineFindings = distinctFindings(ranges
    .filter(range => range.start === textLength && range.end === textLength)
    .map(range => range.finding));
  if (endOfLineFindings.length) result.push({
    text: '',
    kind: 'plain',
    startColumn: textLength + 1,
    endColumn: textLength + 1,
    findings: endOfLineFindings,
    selected: !!selectedFindingKey && endOfLineFindings.some(finding => identity(finding) === selectedFindingKey),
    overlap: endOfLineFindings.length > 1,
    endOfLine: true,
  });
  return result;
}

function distinctFindings(findings: ReviewFinding[]): ReviewFinding[] {
  return findings.filter((finding, index) => findings.findIndex(candidate => identity(candidate) === identity(finding)) === index);
}

function identity(finding: ReviewFinding): string { return finding.fingerprint ?? finding.id; }
