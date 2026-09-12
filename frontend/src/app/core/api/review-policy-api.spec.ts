import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ReviewPolicyApi } from './review-policy-api';

describe('ReviewPolicyApi', () => {
  let api: ReviewPolicyApi;
  let http: HttpTestingController;
  beforeEach(() => {
    localStorage.removeItem('qs-last-repository');
    TestBed.configureTestingModule({ providers: [ReviewPolicyApi, provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ReviewPolicyApi);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('loads the effective repository rules and actual resolved input preview together', async () => {
    const loading = api.load();
    http.expectOne('/api/repos/default/rules').flush({ catalogueVersion: '1.4.0', rules: [], traces: [], sources: ['project'] });
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
    await loading;
    expect(api.catalogue()?.sources).toEqual(['project']);
    expect(api.inputPreview()?.level).toBe('file');
    expect(api.loading()).toBeFalse();
    expect(api.error()).toBe('');
  });

  it('does not replace a new repository policy with late responses from the previous repository', async () => {
    const previous = api.load();
    api.repositoryId.set('other');
    const current = api.load();
    http.expectOne('/api/repos/other/rules').flush({ catalogueVersion: 'new', rules: [], traces: [], sources: [] });
    http.expectOne('/api/repos/other/inputs').flush({ level: 'file', kinds: {} });
    await current;
    http.expectOne('/api/repos/default/rules').flush({ catalogueVersion: 'old', rules: [], traces: [], sources: [] });
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
    await previous;
    expect(api.catalogue()?.catalogueVersion).toBe('new');
  });

  it('keeps policy unavailable rather than showing partial or invented rules after a failed request', async () => {
    const loading = api.load();
    http.expectOne('/api/repos/default/rules').flush({ detail: 'Policy could not be read.' }, { status: 500, statusText: 'Failed' });
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
    await loading;
    expect(api.catalogue()).toBeNull();
    expect(api.inputPreview()).toBeNull();
    expect(api.error()).toBe('Policy could not be read.');
    expect(api.loading()).toBeFalse();
  });
});
