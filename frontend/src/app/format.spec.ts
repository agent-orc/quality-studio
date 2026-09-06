import { formatBytes, formatCost, formatModelSource, formatOptionalBytes, formatPriceStatus, formatTokenCount, parseTokenCount } from './format';
import { bySortValue } from './sorting';

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

describe('cost formatting', () => {
  it('shows the currency the API reported and never invents one', () => {
    expect(formatCost(12.5, 'EUR')).toBe('12.50 EUR');
    expect(formatCost(0.0342, 'USD')).toBe('0.0342 USD');
    expect(formatCost(0.42, 'EUR')).toBe('0.42 EUR');
    expect(formatCost(1.5, null)).toBe('1.50');
  });

  it('says unpriced rather than showing zero', () => {
    expect(formatCost(null, 'USD')).toBe('unpriced');
    expect(formatCost(undefined, 'USD')).toBe('unpriced');
  });

  it('reads the price status and the model source as words', () => {
    expect(formatPriceStatus('unknownModel')).toBe('unknown model');
    expect(formatPriceStatus(null)).toBe('unknown');
    expect(formatModelSource('policy-default')).toBe('policy default');
    expect(formatModelSource('runner-default')).toBe('runner default');
    expect(formatModelSource('explicit')).toBe('chosen');
    expect(formatModelSource(null)).toBe('source unrecorded');
  });
});

describe('byte formatting', () => {
  it('uses one scale everywhere, including for a size the API did not report', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(5_000)).toBe('4.9 KB');
    expect(formatBytes(20_000)).toBe('20 KB');
    expect(formatBytes(5_000_000)).toBe('4.8 MB');
    expect(formatOptionalBytes(null)).toBe('—');
    expect(formatOptionalBytes(undefined)).toBe('—');
    expect(formatOptionalBytes(5_000)).toBe(formatBytes(5_000));
  });
});

describe('table sorting', () => {
  interface Row { name: string; score: number | null }
  const rows: Row[] = [
    { name: 'b', score: 2 },
    { name: 'a', score: null },
    { name: 'c', score: 1 },
    { name: 'd', score: 1 },
  ];
  const byName = <T extends { name: string }>(left: T, right: T) => left.name.localeCompare(right.name);

  it('keeps missing values last in both directions', () => {
    const ascending = [...rows].sort(bySortValue(row => row.score, byName, 'asc')).map(row => row.name);
    const descending = [...rows].sort(bySortValue(row => row.score, byName, 'desc')).map(row => row.name);

    expect(ascending).toEqual(['c', 'd', 'b', 'a']);
    // The tiebreak follows the direction, so equal scores read c, d ascending and d, c descending.
    expect(descending).toEqual(['b', 'd', 'c', 'a']);
  });

  it('breaks ties with the caller-supplied stable order', () => {
    const ordered = [...rows].sort(bySortValue(row => row.score, byName, 'asc'));
    expect(ordered.slice(0, 2).map(row => row.name)).toEqual(['c', 'd']);
  });

  it('compares text naturally', () => {
    const files = [{ name: 'File10.cs' }, { name: 'File2.cs' }];
    expect([...files].sort(bySortValue(file => file.name, byName, 'asc')).map(file => file.name))
      .toEqual(['File2.cs', 'File10.cs']);
  });
});
