import { ReviewFinding } from '../quality-api';
import { segmentFindingLine } from './finding-segments';
import { TokenLine } from './syntax-types';

const tokens = (...values: Array<[string, TokenLine[number]['kind']]>): TokenLine =>
  values.map(([text, kind]) => ({ text, kind }));

function finding(id: string, startLine: number, startColumn: number, endLine: number, endColumn: number): ReviewFinding {
  return {
    id, aspect: 'correctness', severity: 'high', title: id, description: '', recommendation: '',
    ruleId: 'built-in:code',
    locations: [{ path: 'src/file.ts', range: { start: { line: startLine, column: startColumn }, end: { line: endLine, column: endColumn } } }],
  };
}

describe('finding source segmentation', () => {
  it('splits syntax spans at a single-line inclusive range', () => {
    const result = segmentFindingLine(tokens(['const ', 'keyword'], ['value', 'variable'], [' = 1;', 'plain']), 1, 'src/file.ts', [finding('one', 1, 7, 1, 11)], 'one');

    expect(result.map(segment => segment.text).join('')).toBe('const value = 1;');
    expect(result.find(segment => segment.text === 'value')).toEqual(jasmine.objectContaining({ startColumn: 7, endColumn: 11, selected: true }));
  });

  it('uses the start and end columns only on the edge lines of a multi-line range', () => {
    const target = finding('multi', 1, 3, 3, 2);

    expect(segmentFindingLine(tokens(['abcdef', 'plain']), 1, 'src/file.ts', [target], null).find(segment => segment.findings.length)?.text).toBe('cdef');
    expect(segmentFindingLine(tokens(['middle', 'plain']), 2, 'src/file.ts', [target], null).find(segment => segment.findings.length)?.text).toBe('middle');
    expect(segmentFindingLine(tokens(['finish', 'plain']), 3, 'src/file.ts', [target], null).find(segment => segment.findings.length)?.text).toBe('fi');
  });

  it('keeps UTF-16 column semantics used by the .NET range validator', () => {
    const result = segmentFindingLine(tokens(['a😀b', 'plain']), 1, 'src/file.ts', [finding('unicode', 1, 2, 1, 3)], 'unicode');

    expect(result.find(segment => segment.selected)?.text).toBe('😀');
    expect(result.map(segment => segment.text).join('')).toBe('a😀b');
  });

  it('clamps end-of-line ranges and retains an overlapping finding set', () => {
    const result = segmentFindingLine(tokens(['abcdef', 'plain']), 1, 'src/file.ts', [
      finding('left', 1, 2, 1, 99),
      finding('right', 1, 4, 1, 6),
    ], 'right');

    const overlap = result.find(segment => segment.findings.length === 2)!;
    expect(overlap.text).toBe('def');
    expect(overlap.selected).toBeTrue();
    expect(result.map(segment => segment.text).join('')).toBe('abcdef');
  });
});
