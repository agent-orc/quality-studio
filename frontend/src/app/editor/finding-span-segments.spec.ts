import { ReviewFinding } from '../quality-api';
import { segmentFindingSpans } from './finding-span-segments';

function finding(id: string, startLine: number, startColumn: number, endLine: number, endColumn: number): ReviewFinding {
  return {
    id, fingerprint: `sha256:${id.repeat(64).slice(0, 64)}`, ruleId: `rule.${id}`, aspect: 'correctness', severity: 'high',
    title: `Finding ${id}`, description: 'Description.', recommendation: 'Fix it.',
    locations: [{ path: 'src/A.cs', range: { start: { line: startLine, column: startColumn }, end: { line: endLine, column: endColumn } } }],
  };
}

describe('segmentFindingSpans', () => {
  it('splits syntax tokens at inclusive single-line finding columns', () => {
    const selected = finding('a', 1, 5, 1, 9);
    const segments = segmentFindingSpans([{ text: 'const value = 1;', kind: 'keyword' }], 1, 'src/A.cs', [selected], selected.fingerprint);

    expect(segments.map(segment => segment.text)).toEqual(['cons', 't val', 'ue = 1;']);
    expect(segments[1]).toEqual(jasmine.objectContaining({ startColumn: 5, endColumn: 9, selected: true }));
  });

  it('covers the correct columns on every line of a multi-line range', () => {
    const selected = finding('b', 1, 3, 3, 4);
    expect(segmentFindingSpans([{ text: 'abcdef', kind: 'plain' }], 1, 'src/A.cs', [selected], selected.fingerprint)
      .filter(segment => segment.selected).map(segment => segment.text)).toEqual(['cdef']);
    expect(segmentFindingSpans([{ text: 'middle', kind: 'plain' }], 2, 'src/A.cs', [selected], selected.fingerprint)
      .filter(segment => segment.selected).map(segment => segment.text)).toEqual(['middle']);
    expect(segmentFindingSpans([{ text: 'abcdef', kind: 'plain' }], 3, 'src/A.cs', [selected], selected.fingerprint)
      .filter(segment => segment.selected).map(segment => segment.text)).toEqual(['abcd']);
  });

  it('uses UTF-16 columns consistently with the persisted .NET range contract and accepts end-of-line', () => {
    const selected = finding('c', 1, 2, 1, 4);
    const segments = segmentFindingSpans([{ text: 'A😀BC', kind: 'string' }], 1, 'src/A.cs', [selected], selected.fingerprint);
    expect(segments.filter(segment => segment.selected).map(segment => segment.text).join('')).toBe('😀B');
  });

  it('marks overlaps while preserving syntax-token boundaries', () => {
    const first = finding('d', 1, 2, 1, 6);
    const second = finding('e', 1, 5, 1, 8);
    const segments = segmentFindingSpans(
      [{ text: 'abc', kind: 'keyword' }, { text: 'defghi', kind: 'variable' }], 1, 'src/A.cs', [first, second], first.fingerprint);
    expect(segments.filter(segment => segment.overlap).map(segment => segment.text).join('')).toBe('ef');
    expect(segments.some(segment => segment.kind === 'keyword' && segment.findings.length)).toBeTrue();
    expect(segments.some(segment => segment.kind === 'variable' && segment.findings.length)).toBeTrue();
  });

  it('does not render ignored findings as current-source highlights', () => {
    const ignored = { ...finding('f', 1, 1, 1, 3), suppression: {
      id: 'exact-f', reason: 'Known issue.', author: 'Reviewer', createdAt: '2026-08-12T20:00:00Z', expiresAt: null,
    } };
    expect(segmentFindingSpans([{ text: 'abcdef', kind: 'plain' }], 1, 'src/A.cs', [ignored], ignored.fingerprint)
      .some(segment => segment.findings.length)).toBeFalse();
  });
});
