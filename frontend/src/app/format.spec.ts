import { formatCost, formatModelSource, formatPriceStatus, formatTokenCount, parseTokenCount } from './format';

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
