/**
 * Deep link v1 for a commit range: `?repo=<id>&view=change&base=<sha>&head=<sha>[&card=<key>][&previous=<sha>]`.
 * `base` is the merge-base of the reviewed range, never a branch tip; `previous` is the head of the
 * previous delivery round and enables "this round only". `card` is display text, never authority.
 */
export interface ChangeRoute {
  base: string;
  head: string;
  card: string | null;
  previous: string | null;
  /** False when base or head (or a given previous) is not a full lowercase 40-character SHA. */
  valid: boolean;
}

const SHA = /^[0-9a-f]{40}$/;
const CARD = /^[A-Za-z][A-Za-z0-9]{0,15}-[0-9]{1,9}$/;
const CHANGE_PARAMETERS = ['view', 'base', 'head', 'card', 'previous'];

export function readChangeRoute(search: string): ChangeRoute | null {
  const params = new URLSearchParams(search);
  if (params.get('view') !== 'change') return null;
  const base = params.get('base') ?? '';
  const head = params.get('head') ?? '';
  const previous = params.get('previous') || null;
  const card = params.get('card');
  return {
    base,
    head,
    card: card && CARD.test(card) ? card : null,
    previous,
    valid: SHA.test(base) && SHA.test(head) && (previous === null || SHA.test(previous)),
  };
}

export function writeChangeRoute(params: URLSearchParams, route: ChangeRoute | null): void {
  if (!route) {
    // Only a change link owns these; another view value (dashboard, unknown) is left for its owner.
    if (params.get('view') === 'change') CHANGE_PARAMETERS.forEach(name => params.delete(name));
    return;
  }
  params.set('view', 'change');
  params.set('base', route.base);
  params.set('head', route.head);
  if (route.card) params.set('card', route.card); else params.delete('card');
  if (route.previous) params.set('previous', route.previous); else params.delete('previous');
}
