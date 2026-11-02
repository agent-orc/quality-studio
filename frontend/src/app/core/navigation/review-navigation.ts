export interface FindingRoute {
  fingerprint: string | null;
  locationIndex: number;
}

export function readFindingRoute(search: string): FindingRoute {
  const params = new URLSearchParams(search);
  const fingerprint = params.get('finding');
  const parsed = Number(params.get('location') ?? 0);
  return { fingerprint, locationIndex: Number.isInteger(parsed) && parsed >= 0 ? parsed : 0 };
}

export function writeFindingRoute(params: URLSearchParams, fingerprint: string | null, locationIndex: number): void {
  if (fingerprint) {
    params.set('finding', fingerprint);
    params.set('location', String(Math.max(0, Math.floor(locationIndex))));
  } else {
    params.delete('finding');
    params.delete('location');
  }
}

/** Deep link v1 views whose position this shell restores from path and finding; see docs/api.md. */
const POSITIONED_VIEWS = new Set(['explorer', 'finding']);
/** An Agent Studio card key, for example AGT-3352. Display only, never authorization. */
const CARD_KEY = /^[A-Za-z][A-Za-z0-9]{0,15}-[0-9]{1,9}$/;

export interface DeepLink {
  /** True when the link names the repository dashboard: view=dashboard or a view this version does not know. */
  dashboard: boolean;
  card: string | null;
}

/**
 * Reads the deep-link v1 parameters Agent Studio builds links from. `view=dashboard` and every
 * unknown view open the repository dashboard; `explorer`, `finding` or no view keep the
 * position that `path` and `finding` describe.
 */
export function readDeepLink(search: string): DeepLink {
  const params = new URLSearchParams(search);
  const view = params.get('view');
  const card = params.get('card');
  return {
    dashboard: view !== null && !POSITIONED_VIEWS.has(view),
    card: card && CARD_KEY.test(card) ? card : null,
  };
}
