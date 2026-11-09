import { readDeepLink, readFindingRoute, writeFindingRoute } from './review-navigation';

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

describe('Deep link v1', () => {
  it('opens the repository dashboard for view=dashboard and for views it does not know', () => {
    expect(readDeepLink('?repo=proj-002&view=dashboard&path=src/A.cs').dashboard).toBeTrue();
    expect(readDeepLink('?repo=proj-002&view=change&base=a&head=b').dashboard).toBeTrue();
    expect(readDeepLink('?repo=proj-002&view=').dashboard).toBeTrue();
  });

  it('keeps the position of explorer, finding and view-less links', () => {
    expect(readDeepLink('?repo=proj-002&view=explorer&path=src/A.cs').dashboard).toBeFalse();
    expect(readDeepLink('?repo=proj-002&view=finding&finding=sha256%3Aa').dashboard).toBeFalse();
    expect(readDeepLink('?repo=proj-002&path=src/A.cs').dashboard).toBeFalse();
  });

  it('accepts only a card key as card', () => {
    expect(readDeepLink('?card=AGT-3352').card).toBe('AGT-3352');
    expect(readDeepLink('?card=%3Cscript%3E').card).toBeNull();
    expect(readDeepLink('?card=https%3A%2F%2Fevil.example').card).toBeNull();
    expect(readDeepLink('?repo=proj-002').card).toBeNull();
  });
});
