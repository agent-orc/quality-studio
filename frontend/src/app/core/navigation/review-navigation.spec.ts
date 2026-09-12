import { readFindingRoute, writeFindingRoute } from './review-navigation';

describe('finding deep-link route', () => {
  it('restores fingerprint and location while rejecting an invalid location', () => {
    expect(readFindingRoute('?path=src%2FA.cs&finding=sha256%3Aabc&location=2')).toEqual({
      fingerprint: 'sha256:abc', locationIndex: 2,
    });
    expect(readFindingRoute('?finding=sha256%3Aabc&location=-3').locationIndex).toBe(0);
  });

  it('writes and clears the stable finding route without disturbing scope fields', () => {
    const params = new URLSearchParams('repo=default&path=src%2FA.cs&kind=code');
    writeFindingRoute(params, 'sha256:abc', 1);
    expect(params.get('finding')).toBe('sha256:abc');
    expect(params.get('location')).toBe('1');
    expect(params.get('path')).toBe('src/A.cs');
    writeFindingRoute(params, null, 0);
    expect(params.has('finding')).toBeFalse();
    expect(params.has('location')).toBeFalse();
    expect(params.get('kind')).toBe('code');
  });
});
