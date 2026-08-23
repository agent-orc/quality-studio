import { formatTokenCount, parseTokenCount } from './format';

describe('token count formatting', () => {
  it('renders large counts with a readable unit suffix', () => {
    expect(formatTokenCount(100_000)).toBe('100k');
    expect(formatTokenCount(1_500_000)).toBe('1.5M');
    expect(formatTokenCount(1_250)).toBe('1.25k');
  });

  it('parses plain, k, and M token notation to the same integer count', () => {
    expect(parseTokenCount('100000')).toBe(100_000);
    expect(parseTokenCount('100k')).toBe(100_000);
    expect(parseTokenCount('0.1M')).toBe(100_000);
  });

  it('rejects missing, fractional, and non-token values', () => {
    expect(parseTokenCount('')).toBeNull();
    expect(parseTokenCount('1.25')).toBeNull();
    expect(parseTokenCount('many')).toBeNull();
  });
});
