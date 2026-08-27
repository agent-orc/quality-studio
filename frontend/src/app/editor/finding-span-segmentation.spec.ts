import { FindingSpanRange, segmentLineTokens } from './finding-span-segmentation';
import { TokenLine } from './syntax-types';

function plain(text: string): TokenLine { return [{ text, kind: 'plain' }]; }

function textOf(segments: ReturnType<typeof segmentLineTokens>): string {
  return segments.map(segment => segment.text).join('');
}

describe('finding span segmentation', () => {
  it('returns the tokens unchanged when no range touches the line', () => {
    const tokens = plain('const value = 1;');
    expect(segmentLineTokens(tokens, 4, 'const value = 1;', [], null)).toBe(tokens);
  });

  it('splits a single-line range out of a wider token and marks it selected', () => {
    const text = 'const value = null;';
    const range: FindingSpanRange = { fingerprint: 'sha256:a', start: { line: 4, column: 15 }, end: { line: 4, column: 18 } };
    const segments = segmentLineTokens(plain(text), 4, text, [range], 'sha256:a');
    expect(textOf(segments)).toBe(text);
    expect(segments.map(segment => segment.state)).toEqual([undefined, 'selected', undefined]);
    expect(segments.find(segment => segment.state === 'selected')!.text).toBe('null');
  });

  it('selects the start, middle, and end lines of a multi-line range', () => {
    const range: FindingSpanRange = { fingerprint: 'sha256:b', start: { line: 8, column: 5 }, end: { line: 10, column: 3 } };
    const startLine = '    string? Model = null,';
    const middleLine = '    string? CliType = null,';
    const endLine = '    long? TokenCap = null,';

    const startSegments = segmentLineTokens(plain(startLine), 8, startLine, [range], 'sha256:b');
    expect(textOf(startSegments)).toBe(startLine);
    expect(startSegments.at(-1)!.state).toBe('selected');
    expect(startSegments.at(-1)!.text).toBe(startLine.slice(4));
    expect(startSegments[0].state).toBeUndefined();

    const middleSegments = segmentLineTokens(plain(middleLine), 9, middleLine, [range], 'sha256:b');
    expect(middleSegments.every(segment => segment.state === 'selected')).toBeTrue();
    expect(textOf(middleSegments)).toBe(middleLine);

    const endSegments = segmentLineTokens(plain(endLine), 10, endLine, [range], 'sha256:b');
    expect(endSegments[0].state).toBe('selected');
    expect(endSegments[0].text).toBe(endLine.slice(0, 3));
    expect(endSegments.at(-1)!.state).toBeUndefined();
  });

  it('uses UTF-16 code-unit columns consistently for Unicode source', () => {
    const text = 'const label = "café 日本語";';
    const range: FindingSpanRange = { fingerprint: 'sha256:c', start: { line: 1, column: 16 }, end: { line: 1, column: 19 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:c');
    expect(textOf(segments)).toBe(text);
    expect(segments.find(segment => segment.state === 'selected')!.text).toBe('café');
  });

  it('handles a surrogate pair using the server-compatible UTF-16 columns', () => {
    const text = 'const icon = "🔎";';
    const emojiStart = text.indexOf('🔎');
    const range: FindingSpanRange = {
      fingerprint: 'sha256:emoji',
      start: { line: 1, column: emojiStart + 1 },
      end: { line: 1, column: emojiStart + 2 },
    };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:emoji');
    expect(textOf(segments)).toBe(text);
    expect(segments.find(segment => segment.state === 'selected')!.text).toBe('🔎');
  });

  it('clamps a range whose end column runs past the end of the line', () => {
    const text = 'short';
    const range: FindingSpanRange = { fingerprint: 'sha256:d', start: { line: 1, column: 3 }, end: { line: 1, column: 999 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range], 'sha256:d');
    expect(textOf(segments)).toBe(text);
    expect(segments.at(-1)!.state).toBe('selected');
    expect(segments.at(-1)!.text).toBe('ort');
  });

  it('marks only the intersection of distinct overlapping findings as overlap', () => {
    const text = 'if (a && b) return risky(a, b);';
    const outer: FindingSpanRange = { fingerprint: 'sha256:outer', start: { line: 1, column: 1 }, end: { line: 1, column: 12 } };
    const inner: FindingSpanRange = { fingerprint: 'sha256:inner', start: { line: 1, column: 5 }, end: { line: 1, column: 20 } };
    const segments = segmentLineTokens(plain(text), 1, text, [outer, inner], 'sha256:outer');

    expect(textOf(segments)).toBe(text);
    expect(segments.find(segment => segment.state === 'overlap')!.text).toBe('a && b) ');
    expect(segments.find(segment => segment.state === 'selected')!.text).toBe('if (');
  });

  it('does not call duplicate locations for one finding an overlap', () => {
    const text = 'duplicate location';
    const range: FindingSpanRange = { fingerprint: 'sha256:same', start: { line: 1, column: 1 }, end: { line: 1, column: 9 } };
    const segments = segmentLineTokens(plain(text), 1, text, [range, range], 'sha256:same');
    expect(segments.some(segment => segment.state === 'overlap')).toBeFalse();
    expect(segments.find(segment => segment.state === 'selected')!.text).toBe('duplicate');
  });

  it('preserves the original syntax kind for every produced sub-segment', () => {
    const tokens: TokenLine = [{ text: '  ', kind: 'plain' }, { text: '"a whole string literal"', kind: 'string' }, { text: ';', kind: 'punctuation' }];
    const text = tokens.map(token => token.text).join('');
    const range: FindingSpanRange = { fingerprint: 'sha256:e', start: { line: 1, column: 10 }, end: { line: 1, column: 16 } };
    const segments = segmentLineTokens(tokens, 1, text, [range], 'sha256:e');
    expect(textOf(segments)).toBe(text);
    expect(segments.some(segment => segment.kind === 'string' && segment.state === 'selected')).toBeTrue();
    expect(segments.filter(segment => segment.kind !== 'string').every(segment => segment.state === undefined)).toBeTrue();
  });

  it('returns an empty-line token list untouched', () => {
    const range: FindingSpanRange = { fingerprint: 'sha256:f', start: { line: 1, column: 1 }, end: { line: 2, column: 1 } };
    expect(segmentLineTokens(plain(''), 1, '', [range], 'sha256:f')).toEqual([{ text: '', kind: 'plain' }]);
  });
});
