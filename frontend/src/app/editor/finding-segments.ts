import { FindingLocation, ReviewFinding } from '../quality-api';
import { TokenKind, TokenLine } from './syntax-types';

export type FindingRange = NonNullable<FindingLocation['range']>;

export interface FindingLineMatch {
  finding: ReviewFinding;
  range: FindingRange;
}

export interface FindingTokenSegment {
  text: string;
  kind: TokenKind;
  startColumn: number;
  endColumn: number;
  findings: ReviewFinding[];
  selected: boolean;
  overlap: boolean;
  endOfLine: boolean;
  interactive: boolean;
}

interface LineInterval {
  start: number;
  end: number;
  finding: ReviewFinding;
  endOfLine: boolean;
}

/**
 * Splits a syntax-tokenized line at one-based inclusive finding boundaries.
 * JavaScript string offsets intentionally match the UTF-16 column convention used
 * by the .NET range validator, so surrogate pairs stay intact when the range does.
 */
export function segmentFindingLine(
  tokens: TokenLine,
  text: string,
  line: number,
  matches: readonly FindingLineMatch[],
  selectedFindingId: string | null,
): FindingTokenSegment[] {
  const intervals = matches
    .map(match => intervalForLine(match, text.length, line))
    .filter((interval): interval is LineInterval => interval !== null);
  const boundaries = new Set<number>([0, text.length]);
  const tokenRanges = tokenOffsets(tokens);
  for (const token of tokenRanges) {
    boundaries.add(Math.min(token.start, text.length));
    boundaries.add(Math.min(token.end, text.length));
  }
  for (const interval of intervals) {
    boundaries.add(interval.start);
    boundaries.add(interval.end);
  }

  const sortedBoundaries = [...boundaries].sort((left, right) => left - right);
  const segments: FindingTokenSegment[] = [];
  for (let index = 0; index < sortedBoundaries.length - 1; index++) {
    const start = sortedBoundaries[index];
    const end = sortedBoundaries[index + 1];
    if (end <= start) continue;
    const findings = uniqueFindings(intervals
      .filter(interval => !interval.endOfLine && interval.start <= start && end <= interval.end)
      .map(interval => interval.finding));
    segments.push({
      text: text.slice(start, end),
      kind: tokenRanges.find(token => token.start <= start && start < token.end)?.kind ?? 'plain',
      startColumn: start + 1,
      endColumn: end,
      findings,
      selected: findings.some(finding => finding.id === selectedFindingId),
      overlap: findings.length > 1,
      endOfLine: false,
      interactive: false,
    });
  }

  const endOfLineFindings = uniqueFindings(intervals
    .filter(interval => interval.endOfLine)
    .map(interval => interval.finding));
  if (endOfLineFindings.length) {
    segments.push({
      text: '',
      kind: tokenRanges.at(-1)?.kind ?? 'plain',
      startColumn: text.length + 1,
      endColumn: text.length + 1,
      findings: endOfLineFindings,
      selected: endOfLineFindings.some(finding => finding.id === selectedFindingId),
      overlap: endOfLineFindings.length > 1,
      endOfLine: true,
      interactive: false,
    });
  }

  let previousFindingSet = '';
  for (const segment of segments) {
    const findingSet = segment.findings.map(finding => finding.fingerprint ?? finding.id).join('\0');
    segment.interactive = findingSet.length > 0 && findingSet !== previousFindingSet;
    previousFindingSet = findingSet;
  }
  return segments;
}

export function primaryFindingLocationLabel(finding: ReviewFinding): string {
  const location = finding.locations.find(candidate => candidate.range) ?? finding.locations[0];
  if (!location) return 'Location unavailable';
  if (!location.range) return location.path;
  return `${location.path}:${location.range.start.line}:${location.range.start.column}`;
}

function intervalForLine(match: FindingLineMatch, lineLength: number, line: number): LineInterval | null {
  const { start, end } = match.range;
  if (line < start.line || line > end.line) return null;
  const rawStart = line === start.line ? start.column - 1 : 0;
  const rawEnd = line === end.line ? end.column : lineLength;
  const intervalStart = Math.min(Math.max(0, rawStart), lineLength);
  const intervalEnd = Math.min(Math.max(intervalStart, rawEnd), lineLength);
  return {
    start: intervalStart,
    end: intervalEnd,
    finding: match.finding,
    endOfLine: intervalStart === lineLength && intervalEnd === lineLength,
  };
}

function tokenOffsets(tokens: TokenLine): Array<{ start: number; end: number; kind: TokenKind }> {
  let offset = 0;
  return tokens.map(token => {
    const range = { start: offset, end: offset + token.text.length, kind: token.kind };
    offset = range.end;
    return range;
  });
}

function uniqueFindings(findings: readonly ReviewFinding[]): ReviewFinding[] {
  const seen = new Set<string>();
  return findings.filter(finding => {
    const key = finding.fingerprint ?? finding.id;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
