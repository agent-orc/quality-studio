import { clipRangeToLine, segmentTokensByColumns } from './finding-spans';
import { TokenLine } from './syntax-types';

describe('clipRangeToLine', () => {
  const range = { start: { line: 5, column: 10 }, end: { line: 7, column: 4 } };

  it('returns null for a line outside the range', () => {
    expect(clipRangeToLine(range, 4, 40)).toBeNull();
    expect(clipRangeToLine(range, 8, 40)).toBeNull();
  });

  it('clips the first line to start column through end of line', () => {
    expect(clipRangeToLine(range, 5, 40)).toEqual({ startColumn: 10, endColumn: 40 });
  });

  it('spans a middle line in full', () => {
    expect(clipRangeToLine(range, 6, 12)).toEqual({ startColumn: 1, endColumn: 12 });
  });

  it('clips the last line to column 1 through the end column', () => {
    expect(clipRangeToLine(range, 7, 40)).toEqual({ startColumn: 1, endColumn: 4 });
  });

  it('treats a same-line range as a single clipped segment', () => {
    const sameLine = { start: { line: 3, column: 5 }, end: { line: 3, column: 9 } };
    expect(clipRangeToLine(sameLine, 3, 40)).toEqual({ startColumn: 5, endColumn: 9 });
  });

  it('clamps an end-of-line column that exceeds the line length', () => {
    const endOfLine = { start: { line: 2, column: 3 }, end: { line: 2, column: 999 } };
    expect(clipRangeToLine(endOfLine, 2, 12)).toEqual({ startColumn: 3, endColumn: 12 });
  });
});

describe('segmentTokensByColumns', () => {
  it('returns tokens unchanged with empty values when there are no ranges', () => {
    const tokens: TokenLine = [{ text: 'const ', kind: 'keyword' }, { text: 'x', kind: 'variable' }];
    expect(segmentTokensByColumns(tokens, [])).toEqual([
      { text: 'const ', kind: 'keyword', values: [] },
      { text: 'x', kind: 'variable', values: [] },
    ]);
  });

  it('splits a single-line token at a finding boundary inside it', () => {
    const tokens: TokenLine = [{ text: 'const x = 1;', kind: 'plain' }];
    const result = segmentTokensByColumns(tokens, [{ startColumn: 7, endColumn: 7, value: 'finding-a' }]);
    expect(result).toEqual([
      { text: 'const ', kind: 'plain', values: [] },
      { text: 'x', kind: 'plain', values: ['finding-a'] },
      { text: ' = 1;', kind: 'plain', values: [] },
    ]);
  });

  it('never splits across a token-kind boundary', () => {
    const tokens: TokenLine = [{ text: 'const ', kind: 'keyword' }, { text: 'x', kind: 'variable' }, { text: ';', kind: 'punctuation' }];
    const result = segmentTokensByColumns(tokens, [{ startColumn: 1, endColumn: 7, value: 'finding-a' }]);
    expect(result.map(span => span.kind)).toEqual(['keyword', 'variable', 'punctuation']);
    expect(result[0].values).toEqual(['finding-a']);
    expect(result[1].values).toEqual(['finding-a']);
    expect(result[2].values).toEqual([]);
  });

  it('marks overlapping findings with both values on the shared segment', () => {
    const tokens: TokenLine = [{ text: 'abcdefghij', kind: 'plain' }];
    const result = segmentTokensByColumns(tokens, [
      { startColumn: 1, endColumn: 5, value: 'a' },
      { startColumn: 4, endColumn: 10, value: 'b' },
    ]);
    expect(result).toEqual([
      { text: 'abc', kind: 'plain', values: ['a'] },
      { text: 'de', kind: 'plain', values: ['a', 'b'] },
      { text: 'fghij', kind: 'plain', values: ['b'] },
    ]);
  });

  it('handles a range covering the full line, including a trailing surrogate-pair character', () => {
    const text = 'emoji 😀 end';
    const tokens: TokenLine = [{ text, kind: 'plain' }];
    const result = segmentTokensByColumns(tokens, [{ startColumn: 1, endColumn: text.length, value: 'finding-a' }]);
    expect(result.map(span => span.text).join('')).toBe(text);
    expect(result.every(span => span.values.includes('finding-a'))).toBeTrue();
  });

  it('clips a range that extends past the end of the line without throwing', () => {
    const tokens: TokenLine = [{ text: 'short', kind: 'plain' }];
    const result = segmentTokensByColumns(tokens, [{ startColumn: 3, endColumn: 999, value: 'finding-a' }]);
    expect(result.map(span => span.text).join('')).toBe('short');
    expect(result.at(-1)?.values).toEqual(['finding-a']);
  });
});
