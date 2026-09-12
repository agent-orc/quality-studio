import { FindingSpanRange, segmentLineTokens } from './finding-span-segmentation';
import { TokenLine } from './syntax-types';

function plain(text: string): TokenLine { return [{ text, kind: 'plain' }]; }

function textOf(segments: ReturnType<typeof segmentLineTokens>): string {
  return segments.map(segment => segment.text).join('');
}

describe('finding span segmentation', () => {
  it('returns the tokens unchanged, tagged plain, when no range touches the line', () => {
    const tokens = plain('const value = 1;');
    const segments = segmentLineTokens(tokens, 4, 'const value = 1;', [], null);
    expect(segments).toEqual(tokens.map(token => ({ ...token, state: 'plain', fingerprints: [] })));
  });

  it('splits a single-line range out of a wider token and marks it selected', () => {
    const text = 'const value = null;';
    const range: FindingSpanRange = { fingerprint: 'sha256:a', start: { line: 4, column: 15 }, end: { line: 4, column: 18 } };
    const segments = segmentLineTokens(plain(text), 4, text, [range], 'sha256:a');
    expect(textOf(segments)).toBe(text);
    expect(segments.map(segment => segment.state)).toEqual(['plain', 'selected', 'plain']);
    const selected = segments.find(segment => segment.state === 'selected')!;
    expect(selected.text).toBe('null');
    expect(selected.fingerprints).toEqual(['sha256:a']);
  });

  it('selects to end of line on the start line and from column 1 on the end line of a multi-line range', () => {
    const range: FindingSpanRange = { fingerprint: 'sha256:b', start: { line: 8, column: 5 }, end: { line: 10, column: 3 } };
    const startLine = '    string? Model = null,';
    const middleLine = '    string? CliType = null,';
    const endLine = '    long? TokenCap = null,';

    const startSegments = segmentLineTokens(plain(startLine), 8, startLine, [range], 'sha256:b');
    expect(textOf(startSegments)).toBe(startLine);
    expect(startSegments.at(-1)!.state).toBe('selected');
    expect(startSegments.at(-1)!.text).toBe(startLine.slice(4));
    expect(startSegments[0].state).toBe('plain');

    const middleSegments = segmentLineTokens(plain(middleLine), 9, middleLine, [range], 'sha256:b');
    expect(middleSegments.every(segment => segment.state === 'selected')).toBeTrue();
    expect(textOf(middleSegments)).toBe(middleLine);

    const endSegments = segmentLineTokens(plain(endLine), 10, endLine, [range], 'sha256:b');
    expect(endSegments[0].state).toBe('selected');
    expect(endSegments[0].text).toBe(endLine.slice(0, 3));
    expect(endSegments.at(-1)!.state).toBe('plain');
  });

  it('indexes columns as UTF-16 code units so multi-byte characters segment at the right boundary', () => {
    const text = 'const label = "café 日本語";';
    // Column 16 is the opening quote content start; select the 4-character "café" word (indices 15..19).
    const range: FindingSpanRange = { fingerprint: 'sha256:c', start: { line: 1, column: 16 }, end: { line: 1, column: 19 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:c');
    expect(textOf(segments)).toBe(text);
    const selected = segments.find(segment => segment.state === 'selected')!;
    expect(selected.text).toBe('café');
  });

  it('clamps a range whose end column runs past the end of the line', () => {
    const text = 'short';
    const range: FindingSpanRange = { fingerprint: 'sha256:d', start: { line: 1, column: 3 }, end: { line: 1, column: 999 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:d');
    expect(textOf(segments)).toBe(text);
    expect(segments.at(-1)!.state).toBe('selected');
    expect(segments.at(-1)!.text).toBe('ort');
  });

  it('marks the intersection of two overlapping findings as overlap and keeps the non-overlapping remainder selected/plain', () => {
    const text = 'if (a && b) return risky(a, b);';
    const outer: FindingSpanRange = { fingerprint: 'sha256:outer', start: { line: 1, column: 1 }, end: { line: 1, column: 12 } };
    const inner: FindingSpanRange = { fingerprint: 'sha256:inner', start: { line: 1, column: 5 }, end: { line: 1, column: 20 } };
    const segments = segmentLineTokens(plain(text), 1, text, [outer, inner], 'sha256:outer');

    expect(textOf(segments)).toBe(text);
    const overlap = segments.find(segment => segment.state === 'overlap')!;
    expect(overlap.text).toBe('a && b) ');
    expect(overlap.fingerprints).toEqual(['sha256:outer', 'sha256:inner']);
    const selectedOnly = segments.find(segment => segment.state === 'selected')!;
    expect(selectedOnly.text).toBe('if (');
  });

  it('preserves the original token kind for every produced sub-segment', () => {
    const tokens: TokenLine = [{ text: '  ', kind: 'plain' }, { text: '"a whole string literal"', kind: 'string' }, { text: ';', kind: 'punctuation' }];
    const text = tokens.map(token => token.text).join('');
    const range: FindingSpanRange = { fingerprint: 'sha256:e', start: { line: 1, column: 10 }, end: { line: 1, column: 16 } };
    const segments = segmentLineTokens(tokens, 1, text, [range], 'sha256:e');
    expect(textOf(segments)).toBe(text);
    for (const segment of segments) {
      if (segment.kind === 'string') continue;
      expect(segment.state).toBe('plain');
    }
    expect(segments.some(segment => segment.kind === 'string' && segment.state === 'selected')).toBeTrue();
  });

  it('returns an empty-line token list untouched', () => {
    const range: FindingSpanRange = { fingerprint: 'sha256:f', start: { line: 1, column: 1 }, end: { line: 2, column: 1 } };
    const segments = segmentLineTokens(plain(''), 1, '', [range], 'sha256:f');
    expect(segments).toEqual([{ text: '', kind: 'plain', state: 'plain', fingerprints: [] }]);
  });

  it('reads start.column as 1-based, so column 1 selects from the first character', () => {
    const text = 'return null;';
    const range: FindingSpanRange = { fingerprint: 'sha256:g', start: { line: 1, column: 1 }, end: { line: 1, column: 6 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:g');

    expect(textOf(segments)).toBe(text);
    expect(segments[0].state).toBe('selected');
    // A zero-based reading would start one character late and select "eturn ".
    expect(segments[0].text).toBe('return');
  });

  it('reads end.column the way the server slices its evidence excerpt', () => {
    const text = 'var total = 0;';
    const range: FindingSpanRange = { fingerprint: 'sha256:h', start: { line: 1, column: 5 }, end: { line: 1, column: 9 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:h');
    const selected = segments.find(segment => segment.state === 'selected')!;

    // FindingIdentity.ExtractSnippet takes line[(startColumn - 1)..endColumn] for the same range.
    expect(selected.text).toBe(text.slice(range.start.column - 1, range.end.column));
    expect(selected.text).toBe('total');
  });
});
