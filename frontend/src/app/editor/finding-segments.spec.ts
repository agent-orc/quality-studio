import { ReviewFinding } from '../quality-api';
import { FindingLineMatch, primaryFindingLocationLabel, segmentFindingLine } from './finding-segments';
import { TokenLine } from './syntax-types';

const path = 'src/example.ts';

function finding(id: string, startLine: number, startColumn: number, endLine: number, endColumn: number): ReviewFinding {
  return {
    id,
    ruleId: `rule:${id}`,
    aspect: 'correctness',
    severity: 'medium',
    title: `Finding ${id}`,
    description: 'Problem',
    recommendation: 'Fix it',
    locations: [{ path, range: {
      start: { line: startLine, column: startColumn },
      end: { line: endLine, column: endColumn },
    } }],
  };
}

function match(value: ReviewFinding): FindingLineMatch {
  return { finding: value, range: value.locations[0].range! };
}

function plain(text: string): TokenLine { return [{ text, kind: 'plain' }]; }

describe('finding range segmentation', () => {
  it('splits syntax tokens at a single-line inclusive range', () => {
    const value = finding('single', 1, 7, 1, 11);
    const tokens: TokenLine = [
      { text: 'const', kind: 'keyword' },
      { text: ' value', kind: 'plain' },
      { text: ' = 1', kind: 'number' },
    ];

    const segments = segmentFindingLine(tokens, 'const value = 1', 1, [match(value)], value.id);

    expect(segments.map(segment => segment.text)).toEqual(['const', ' ', 'value', ' = 1']);
    expect(segments[2].kind).toBe('plain');
    expect(segments[2].startColumn).toBe(7);
    expect(segments[2].endColumn).toBe(11);
    expect(segments[2].selected).toBeTrue();
  });

  it('projects a multi-line range onto the first, interior, and final lines', () => {
    const value = finding('multi', 2, 3, 4, 2);

    const first = segmentFindingLine(plain('abcdef'), 'abcdef', 2, [match(value)], null);
    const middle = segmentFindingLine(plain('middle'), 'middle', 3, [match(value)], null);
    const last = segmentFindingLine(plain('last'), 'last', 4, [match(value)], null);

    expect(first.filter(segment => segment.findings.length).map(segment => segment.text).join('')).toBe('cdef');
    expect(middle.filter(segment => segment.findings.length).map(segment => segment.text).join('')).toBe('middle');
    expect(last.filter(segment => segment.findings.length).map(segment => segment.text).join('')).toBe('la');
  });

  it('uses the backend UTF-16 column convention without splitting a surrogate pair', () => {
    const value = finding('unicode', 1, 2, 1, 3);

    const selected = segmentFindingLine(plain('a😀bc'), 'a😀bc', 1, [match(value)], value.id)
      .filter(segment => segment.selected);

    expect(selected.length).toBe(1);
    expect(selected[0].text).toBe('😀');
    expect(selected[0].startColumn).toBe(2);
    expect(selected[0].endColumn).toBe(3);
  });

  it('retains an identifiable zero-width point at end of line', () => {
    const value = finding('eol', 1, 5, 1, 5);

    const segments = segmentFindingLine(plain('text'), 'text', 1, [match(value)], value.id);
    const endOfLine = segments.at(-1)!;

    expect(endOfLine.text).toBe('');
    expect(endOfLine.endOfLine).toBeTrue();
    expect(endOfLine.startColumn).toBe(5);
    expect(endOfLine.endColumn).toBe(5);
    expect(endOfLine.selected).toBeTrue();
  });

  it('marks overlapping findings and only the selected finding range', () => {
    const left = finding('left', 1, 2, 1, 5);
    const right = finding('right', 1, 4, 1, 7);

    const segments = segmentFindingLine(plain('abcdefgh'), 'abcdefgh', 1, [match(left), match(right)], right.id);
    const overlap = segments.find(segment => segment.overlap)!;

    expect(overlap.text).toBe('de');
    expect(overlap.findings.map(item => item.id)).toEqual(['left', 'right']);
    expect(overlap.selected).toBeTrue();
    expect(overlap.interactive).toBeTrue();
    expect(segments.find(segment => segment.text === 'bc')?.selected).toBeFalse();
    expect(segments.find(segment => segment.text === 'fg')?.selected).toBeTrue();
  });

  it('formats a compact primary path, line, and column label', () => {
    expect(primaryFindingLocationLabel(finding('label', 9, 4, 9, 8))).toBe(`${path}:9:4`);
  });
});
