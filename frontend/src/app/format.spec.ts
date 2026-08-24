import { formatBytes, formatDateTime } from './format';

describe('formatDateTime', () => {
  const instant = '2026-03-05T14:30:00Z';

  it('renders a medium date and a short local time without seconds', () => {
    const formatted = formatDateTime(instant);
    const local = new Date(instant);

    expect(formatted).toContain(String(local.getFullYear()));
    expect(formatted).toContain(String(local.getDate()));
    expect(formatted).toMatch(/[A-Za-z]{3}/);
    expect(formatted).toMatch(/\d{1,2}:\d{2}/);
    expect(formatted).not.toMatch(/\d{1,2}:\d{2}:\d{2}/);
  });

  it('normalises to the instant, so an offset spelling and its UTC spelling agree', () => {
    expect(formatDateTime('2026-03-05T16:30:00+02:00')).toBe(formatDateTime(instant));
    expect(formatDateTime('2026-03-05T09:30:00-05:00')).toBe(formatDateTime(instant));
  });

  it('collapses instants that differ only in seconds but keeps minutes apart', () => {
    expect(formatDateTime('2026-03-05T14:30:59Z')).toBe(formatDateTime(instant));
    expect(formatDateTime('2026-03-05T14:31:00Z')).not.toBe(formatDateTime(instant));
  });

  it('throws instead of printing a placeholder for a value Date cannot parse', () => {
    expect(() => formatDateTime('not-a-date')).toThrowError(RangeError);
    expect(() => formatDateTime('')).toThrowError(RangeError);
  });
});

describe('formatBytes', () => {
  it('reports raw bytes below one kibibyte', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1)).toBe('1 B');
    expect(formatBytes(1023)).toBe('1023 B');
  });

  it('never reports a negative or non-numeric size', () => {
    expect(formatBytes(-5)).toBe('0 B');
    expect(formatBytes(Number.NaN)).toBe('0 B');
    expect(formatBytes(Number.NEGATIVE_INFINITY)).toBe('0 B');
  });

  it('switches unit at each 1024 boundary and drops the decimal from ten upwards', () => {
    expect(formatBytes(1024)).toBe('1.0 KB');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(1024 * 10)).toBe('10 KB');
    expect(formatBytes(1024 * 1023)).toBe('1023 KB');
    expect(formatBytes(1024 ** 2)).toBe('1.0 MB');
    expect(formatBytes(1024 ** 3)).toBe('1.0 GB');
  });

  it('stops scaling at gigabytes so a repository-sized value stays readable', () => {
    expect(formatBytes(1024 ** 4)).toBe('1024 GB');
    expect(formatBytes(5 * 1024 ** 5)).toBe(`${5 * 1024 ** 2} GB`);
    expect(formatBytes(Number.POSITIVE_INFINITY)).toBe('Infinity B');
  });
});
