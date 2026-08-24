import { KindState, ReviewState, TreeNode } from './quality-api';
import { ancestorIds, flattenTree } from './tree-utils';

function kindState(overall: ReviewState, band: string | null = null): KindState {
  return { direct: overall, descendants: overall, overall, score: null, band, metaPath: null };
}

function node(id: string, path: string, kinds: Record<string, KindState>, children: TreeNode[] = []): TreeNode {
  return { id, name: path.split('/').at(-1) ?? id, level: children.length ? 'folder' : 'file', path, kinds, children };
}

const leaf = node('program', 'src/api/Program.cs', { code: kindState('fresh') });
const project = node('api', 'src/api', { code: kindState('stale') }, [leaf]);
const folder = node('src', 'src', { code: kindState('stale') }, [project]);
const tests = node('tests', 'tests', { code: kindState('missing') });
const repository = node('root', '.', { code: kindState('stale') }, [folder, tests]);
const tree = [repository];

describe('flattenTree', () => {
  it('produces nothing for an empty tree', () => {
    expect(flattenTree([], new Set())).toEqual([]);
    expect(flattenTree([], new Set(['root']), true)).toEqual([]);
  });

  it('hides collapsed children and expands only the ids in the set', () => {
    expect(flattenTree(tree, new Set()).map(row => row.path)).toEqual(['.']);
    expect(flattenTree(tree, new Set(['root'])).map(row => row.path)).toEqual(['.', 'src', 'tests']);
    expect(flattenTree(tree, new Set(['root', 'src'])).map(row => row.path))
      .toEqual(['.', 'src', 'src/api', 'tests']);
  });

  it('keys expansion on node id, not on path', () => {
    expect(flattenTree(tree, new Set(['.'])).map(row => row.path)).toEqual(['.']);
  });

  it('walks the whole tree depth-first with a depth per level when all is set', () => {
    const rows = flattenTree(tree, new Set(), true);

    expect(rows.map(row => row.path)).toEqual(['.', 'src', 'src/api', 'src/api/Program.cs', 'tests']);
    expect(rows.map(row => row.depth)).toEqual([0, 1, 2, 3, 1]);
  });

  it('starts numbering at the requested depth offset', () => {
    expect(flattenTree([project], new Set(), true, 5).map(row => row.depth)).toEqual([5, 6]);
  });

  it('prefers the code state, falls back to another kind, then to missing', () => {
    const rows = flattenTree([
      node('a', 'a', { security: kindState('fresh'), code: kindState('policy-drift') }),
      node('b', 'b', { performance: kindState('stale'), security: kindState('fresh') }),
      node('c', 'c', {}),
    ], new Set());

    expect(rows.map(row => row.state)).toEqual(['policy-drift', 'stale', 'missing']);
  });

  it('decorates every kind and only prefixes the band when one is graded', () => {
    const [row] = flattenTree([
      node('a', 'a', { code: kindState('fresh', 'A'), security: kindState('missing') }),
    ], new Set());

    expect(row.decorations).toEqual([
      { kind: 'code', state: 'fresh', label: 'code: A, fresh' },
      { kind: 'security', state: 'missing', label: 'security: missing' },
    ]);
  });

  it('carries the original node through and never mutates the source tree', () => {
    const rows = flattenTree(tree, new Set(), true);
    const flatLeaf = rows.find(row => row.path === leaf.path)!;

    expect(flatLeaf.id).toBe('program');
    expect(flatLeaf.name).toBe('Program.cs');
    expect(flatLeaf.level).toBe('file');
    expect(flatLeaf.children).toBe(leaf.children);
    expect(repository.children.length).toBe(2);
    expect((leaf as Partial<{ depth: number }>).depth).toBeUndefined();
  });

  it('does not recurse into a childless node that happens to be expanded', () => {
    expect(flattenTree([tests], new Set(['tests'])).map(row => row.path)).toEqual(['tests']);
  });
});

describe('ancestorIds', () => {
  it('returns the id chain from the root down to the matching path', () => {
    expect(ancestorIds(tree, 'src/api/Program.cs')).toEqual(['root', 'src', 'api', 'program']);
    expect(ancestorIds(tree, 'src')).toEqual(['root', 'src']);
    expect(ancestorIds(tree, '.')).toEqual(['root']);
  });

  it('returns an empty chain for an unknown path or an empty tree', () => {
    expect(ancestorIds(tree, 'src/api/Missing.cs')).toEqual([]);
    expect(ancestorIds(tree, '')).toEqual([]);
    expect(ancestorIds([], 'src')).toEqual([]);
  });

  it('does not match on a path prefix', () => {
    expect(ancestorIds(tree, 'src/ap')).toEqual([]);
    expect(ancestorIds(tree, 'src/api/')).toEqual([]);
  });

  it('takes the first depth-first match when a path repeats', () => {
    const duplicated = [
      node('first', 'top', {}, [node('first-child', 'shared', {})]),
      node('second', 'shared', {}),
    ];

    expect(ancestorIds(duplicated, 'shared')).toEqual(['first', 'first-child']);
  });
});
