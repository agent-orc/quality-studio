import { readChangeRoute, writeChangeRoute } from './change-route';

const BASE = '0123456789abcdef0123456789abcdef01234567';
const HEAD = 'fedcba9876543210fedcba9876543210fedcba98';

describe('change route (deep link v1)', () => {
  it('reads view=change with base, head, card and previous', () => {
    const route = readChangeRoute(`?repo=proj-002&view=change&base=${BASE}&head=${HEAD}&card=AGT-3352&previous=${BASE}`);

    expect(route).toEqual({ base: BASE, head: HEAD, card: 'AGT-3352', previous: BASE, valid: true });
  });

  it('ignores other views and marks branch names or short SHAs invalid', () => {
    expect(readChangeRoute('?repo=proj-002')).toBeNull();
    expect(readChangeRoute('?repo=proj-002&view=dashboard')).toBeNull();
    expect(readChangeRoute(`?view=change&base=main&head=${HEAD}`)?.valid).toBeFalse();
    expect(readChangeRoute(`?view=change&base=${BASE}&head=${HEAD.slice(0, 7)}`)?.valid).toBeFalse();
    expect(readChangeRoute(`?view=change&base=${BASE.toUpperCase()}&head=${HEAD}`)?.valid).toBeFalse();
  });

  it('treats card as display text and drops a value that is not a task key', () => {
    expect(readChangeRoute(`?view=change&base=${BASE}&head=${HEAD}&card=<script>`)?.card).toBeNull();
  });

  it('writes and clears only the change parameters', () => {
    const params = new URLSearchParams('repo=proj-002&path=src');
    writeChangeRoute(params, { base: BASE, head: HEAD, card: null, previous: null, valid: true });
    expect(params.get('view')).toBe('change');
    expect(params.has('card')).toBeFalse();

    writeChangeRoute(params, null);
    expect(params.toString()).toBe('repo=proj-002&path=src');
  });
});
