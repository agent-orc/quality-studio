import { ReviewState, TreeNode } from './contracts';

export type FlatNode = TreeNode & { depth: number; state: ReviewState; decorations: { kind: string; state: ReviewState; label: string }[] };

/** Why a review state is what it is, where the state name alone would not say it. */
const STATE_REASONS: Partial<Record<ReviewState, string>> = { invalid: 'unreadable sidecar' };

function decorationLabel(kind: string, state: ReviewState, band: string | null): string {
  const reason = STATE_REASONS[state];
  return `${kind}: ${band ? `${band}, ` : ''}${state}${reason ? ` (${reason})` : ''}`;
}

export function flattenTree(nodes: TreeNode[], expanded: ReadonlySet<string>, all = false, depth = 0): FlatNode[] {
  const result: FlatNode[] = [];
  for (const node of nodes) {
    const state = (node.kinds['code']?.overall ?? Object.values(node.kinds)[0]?.overall ?? 'missing') as ReviewState;
    const decorations = Object.entries(node.kinds).map(([kind, value]) =>
      ({ kind, state: value.overall, label: decorationLabel(kind, value.overall, value.band) }));
    result.push({ ...node, depth, state, decorations });
    if ((all || expanded.has(node.id)) && node.children.length) result.push(...flattenTree(node.children, expanded, all, depth + 1));
  }
  return result;
}

export function ancestorIds(nodes: TreeNode[], path: string): string[] {
  for (const node of nodes) {
    if (node.path === path) return [node.id];
    const below = ancestorIds(node.children, path);
    if (below.length) return [node.id, ...below];
  }
  return [];
}
