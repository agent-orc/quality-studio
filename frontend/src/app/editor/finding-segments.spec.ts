import { ReviewFinding } from '../quality-api';
import { segmentFindingTokens } from './finding-segments';

const path = 'src/A.cs';

function finding(id: string, startLine: number, startColumn: number, endLine: number, endColumn: number): ReviewFinding {
  return {
    id, fingerprint: `sha256:${id.padEnd(64, id[0])}`, ruleId: `rule.${id}`, aspect: 'correctness', severity: 'high',
    title: `Finding ${id}`, description: 'Problem.', recommendation: 'Fix it.',
    locations: [{ path, range: { start: { line: startLine, column: startColumn }, end: { line: endLine, column: endColumn } } }],
  };
}

describe('segmentFindingTokens', () => {
  it('segments a single-line finding without losing syntax token boundaries', () => {
    const item = finding('a', 1, 5, 1, 8);
    const segments = segmentFindingTokens(1, 'let value = 1', [
      { text: 'let', kind: 'keyword' }, { text: ' value ', kind: 'plain' }, { text: '= 1', kind: 'operator' },
    ], [item], path, item.fingerprint!);

    expect(segments.map(segment => segment.text).join('')).toBe('let value = 1');
    expect(segments.filter(segment => segment.findings.length).map(segment => segment.text).join('')).toBe('valu');
    expect(segments.filter(segment => segment.selected).every(segment => segment.startColumn >= 5 && segment.endColumn <= 8)).toBeTrue();
  });

  it('uses the open line edges for the middle of a multi-line range', () => {
    const item = finding('b', 2, 3, 4, 2);
    expect(segmentFindingTokens(2, 'abcdef', [{ text: 'abcdef', kind: 'plain' }], [item], path, item.fingerprint!)
      .filter(segment => segment.selected).map(segment => segment.text).join('')).toBe('cdef');
    expect(segmentFindingTokens(3, 'middle', [{ text: 'middle', kind: 'plain' }], [item], path, item.fingerprint!)
      .filter(segment => segment.selected).map(segment => segment.text).join('')).toBe('middle');
    expect(segmentFindingTokens(4, 'abcdef', [{ text: 'abcdef', kind: 'plain' }], [item], path, item.fingerprint!)
      .filter(segment => segment.selected).map(segment => segment.text).join('')).toBe('ab');
  });

  it('keeps UTF-16 columns aligned for Unicode source text', () => {
    const item = finding('c', 1, 3, 1, 4);
    const selected = segmentFindingTokens(1, 'a 🚀 z', [{ text: 'a 🚀 z', kind: 'string' }], [item], path, item.fingerprint!)
      .filter(segment => segment.selected);
    expect(selected.map(segment => segment.text).join('')).toBe('🚀');
    expect(selected[0].startColumn).toBe(3);
    expect(selected[0].endColumn).toBe(4);
  });

  it('retains an end-of-line anchor as a programmatic segment', () => {
    const item = finding('d', 1, 4, 1, 4);
    const segments = segmentFindingTokens(1, 'abc', [{ text: 'abc', kind: 'plain' }], [item], path, item.fingerprint!);
    const end = segments.at(-1)!;
    expect(end.text).toBe('');
    expect(end.endOfLine).toBeTrue();
    expect(end.startColumn).toBe(4);
    expect(end.selected).toBeTrue();
  });

  it('marks only the intersecting segment as overlapping', () => {
    const first = finding('e', 1, 2, 1, 5);
    const second = finding('f', 1, 4, 1, 7);
    const segments = segmentFindingTokens(1, 'abcdefgh', [{ text: 'abcdefgh', kind: 'plain' }],
      [first, second], path, second.fingerprint!);
    expect(segments.filter(segment => segment.overlap).map(segment => segment.text).join('')).toBe('de');
    expect(segments.filter(segment => segment.overlap)[0].findings.length).toBe(2);
  });
});

