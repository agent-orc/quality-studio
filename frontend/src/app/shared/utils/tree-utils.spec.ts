import { rollUpPathCounts } from './tree-utils';

describe('rollUpPathCounts', () => {
  it('adds each file count to every containing folder and the repository root', () => {
    const totals = rollUpPathCounts({ 'backend/Api/Program.cs': 2, 'backend/Api/Other.cs': 1, 'frontend/src/app.ts': 4, 'README.md': 0 });
    expect(totals.get('backend/Api/Program.cs')).toBe(2);
    expect(totals.get('backend/Api')).toBe(3);
    expect(totals.get('backend')).toBe(3);
    expect(totals.get('frontend/src')).toBe(4);
    expect(totals.get('')).toBe(7);
    expect(totals.get('.')).toBe(7);
    expect(totals.has('README.md')).toBeFalse();
  });
});
