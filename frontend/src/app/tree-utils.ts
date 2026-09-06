import { ReviewState, TreeNode } from './contracts';

export type FlatNode = TreeNode & { depth: number; state: ReviewState; decorations: { kind: string; state: ReviewState; label: string }[] };

export function flattenTree(nodes: TreeNode[], expanded: Set<string>, all = false, depth = 0): FlatNode[] {
  const result: FlatNode[] = [];
  for (const node of nodes) {
    const state = (node.kinds['code']?.overall ?? Object.values(node.kinds)[0]?.overall ?? 'missing') as ReviewState;
    const decorations = Object.entries(node.kinds).map(([kind, value]) => ({ kind, state: value.overall, label: `${kind}: ${value.band ? `${value.band}, ` : ''}${value.overall}` }));
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
