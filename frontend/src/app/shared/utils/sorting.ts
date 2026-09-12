export type SortDirection = 'asc' | 'desc';

/**
 * Builds the comparator every sortable table in the editor uses: missing values last whatever the
 * direction, numbers numerically, text naturally, and a caller-supplied tiebreak so equal rows keep
 * a stable order. The folder table and the risk table each carried their own copy of this.
 */
export function bySortValue<T>(
  value: (item: T) => string | number | null | undefined,
  tiebreak: (left: T, right: T) => number,
  direction: SortDirection,
): (left: T, right: T) => number {
  const factor = direction === 'asc' ? 1 : -1;
  return (left, right) => {
    const leftValue = value(left);
    const rightValue = value(right);
    if (leftValue == null && rightValue == null) return tiebreak(left, right);
    if (leftValue == null) return 1;
    if (rightValue == null) return -1;
    const comparison = typeof leftValue === 'number' && typeof rightValue === 'number'
      ? leftValue - rightValue
      : String(leftValue).localeCompare(String(rightValue), undefined, { numeric: true, sensitivity: 'base' });
    return (comparison || tiebreak(left, right)) * factor;
  };
}
