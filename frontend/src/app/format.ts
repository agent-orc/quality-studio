export function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 1024) return `${Math.max(0, bytes || 0)} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) { value /= 1024; unitIndex++; }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unitIndex]}`;
}

/** The same byte scale for a value the API may not have measured. */
export function formatOptionalBytes(bytes: number | null | undefined): string {
  return bytes === null || bytes === undefined ? '—' : formatBytes(bytes);
}

/** Formats token quantities for compact operator controls without hiding the unit scale. */
export function formatTokenCount(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '';
  if (value < 1_000) return String(Math.round(value));
  const divisor = value >= 1_000_000 ? 1_000_000 : 1_000;
  const suffix = divisor === 1_000_000 ? 'M' : 'k';
  const scaled = value / divisor;
  const precision = scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
  return `${scaled.toFixed(precision).replace(/\.0+$|(?<=\.[0-9])0+$/, '')}${suffix}`;
}

/** Accepts plain token counts and the compact k/M notation shown by the UI. */
export function parseTokenCount(value: string | number | null | undefined): number | null {
  if (typeof value === 'number') {
    return Number.isSafeInteger(value) && value > 0 ? value : null;
  }
  const normalized = String(value ?? '').trim().replaceAll(',', '').replaceAll('_', '');
  const match = /^(\d+(?:\.\d+)?)\s*([km])?$/i.exec(normalized);
  if (!match) return null;
  const multiplier = match[2]?.toLowerCase() === 'm' ? 1_000_000 : match[2]?.toLowerCase() === 'k' ? 1_000 : 1;
  const tokens = Number(match[1]) * multiplier;
  return Number.isSafeInteger(tokens) && tokens > 0 ? tokens : null;
}

/**
 * Formats a monetary amount with the currency the API reported. Never guesses a currency and never
 * shows a number for an unpriced operation; the reader has to be able to tell the two apart.
 */
export function formatCost(total: number | null | undefined, currency: string | null | undefined): string {
  if (total === null || total === undefined || !Number.isFinite(total)) return 'unpriced';
  // Sub-cent review costs are common, so small amounts keep four digits rather than reading as zero.
  const digits = Math.abs(total) >= 0.1 ? 2 : 4;
  return `${total.toFixed(digits)} ${currency ?? ''}`.trim();
}

/** Turns the API's lowerCamel price status into readable words, for example "unknownModel". */
export function formatPriceStatus(status: string | null | undefined): string {
  if (!status) return 'unknown';
  return status.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
}

/** Names how the run's model was chosen so a policy or runner default is never read as a choice. */
export function formatModelSource(source: string | null | undefined): string {
  if (source === 'explicit') return 'chosen';
  if (source === 'policy-default') return 'policy default';
  if (source === 'runner-default') return 'runner default';
  return 'source unrecorded';
}
