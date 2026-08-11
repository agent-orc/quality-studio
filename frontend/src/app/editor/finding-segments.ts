import { FindingLocation, ReviewFinding } from '../quality-api';
import { TokenKind, TokenLine } from './syntax-types';

export interface FindingSegment {
  text: string;
  kind: TokenKind;
  startColumn: number;
  endColumn: number;
  findings: ReviewFinding[];
  selected: boolean;
}

/**
 * Splits syntax spans at one-based, inclusive finding boundaries without
 * changing any source text or syntax classification.
 */
export function segmentFindingLine(
  tokens: TokenLine,
  line: number,
  path: string,
  findings: readonly ReviewFinding[],
  selectedFindingId: string | null,
): FindingSegment[] {
  const textLength = tokens.reduce((length, token) => length + token.text.length, 0);
  const locations = findings.flatMap(finding => finding.locations
    .filter(location => location.path === path && location.range && line >= location.range.start.line && line <= location.range.end.line)
    .map(location => ({ finding, location })));
  const boundaries = new Set<number>([0, textLength]);

  let tokenOffset = 0;
  for (const token of tokens) {
    tokenOffset += token.text.length;
    boundaries.add(tokenOffset);
  }
  for (const { location } of locations) {
    const [start, endExclusive] = offsetsForLine(location, line, textLength);
    boundaries.add(start);
    boundaries.add(endExclusive);
  }

  const ordered = [...boundaries].filter(value => value >= 0 && value <= textLength).sort((left, right) => left - right);
  const segments: FindingSegment[] = [];
  tokenOffset = 0;
  let tokenIndex = 0;
  for (let index = 0; index < ordered.length - 1; index++) {
    const start = ordered[index];
    const end = ordered[index + 1];
    if (end <= start) continue;
    while (tokenIndex < tokens.length - 1 && tokenOffset + tokens[tokenIndex].text.length <= start) {
      tokenOffset += tokens[tokenIndex].text.length;
      tokenIndex++;
    }
    const token = tokens[tokenIndex];
    const active = locations
      .filter(({ location }) => {
        const [locationStart, locationEnd] = offsetsForLine(location, line, textLength);
        return start < locationEnd && end > locationStart;
      })
      .map(({ finding }) => finding);
    segments.push({
      text: token.text.slice(start - tokenOffset, end - tokenOffset),
      kind: token.kind,
      startColumn: start + 1,
      endColumn: end,
      findings: active,
      selected: active.some(finding => finding.id === selectedFindingId),
    });
  }
  return segments;
}

function offsetsForLine(location: FindingLocation, line: number, textLength: number): [number, number] {
  const range = location.range!;
  const start = line === range.start.line ? range.start.column - 1 : 0;
  const endExclusive = line === range.end.line ? range.end.column : textLength;
  return [Math.max(0, Math.min(start, textLength)), Math.max(0, Math.min(endExclusive, textLength))];
}
