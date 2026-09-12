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
