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

  it('writes an override to the selected scope and refreshes the prompt preview', async () => {
    api.editScope.set('global');
    const saving = api.setOverride('QS-CS-004', { enabled: false, severity: null, reason: 'Host has no tests.' });
    const request = http.expectOne(candidate => candidate.url === '/api/repos/default/rules/overrides/QS-CS-004');
    expect(request.request.method).toBe('PUT');
    expect(request.request.params.get('scope')).toBe('global');
    expect(api.saving()).toBeTrue();
    request.flush({ catalogueVersion: '1.5.0', rules: [], traces: [], sources: ['built-in', 'global'] });
    await Promise.resolve();
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: {} });
    await saving;
    expect(api.catalogue()?.sources).toEqual(['built-in', 'global']);
    expect(api.inputPreview()?.level).toBe('file');
    expect(api.saving()).toBeFalse();
  });

  it('rejects with the API message and the located diagnostics', async () => {
    const saving = api.setApplicability(['missing'], 'Try.');
    http.expectOne(candidate => candidate.url === '/api/repos/default/rules/applicability').flush(
      { detail: 'The change would make the rule pool invalid.', diagnostics: [{ scope: 'project', source: 'x', subject: null, message: 'bad' }] },
      { status: 400, statusText: 'Bad Request' });
    await expectAsync(saving).toBeRejectedWith({
      message: 'The change would make the rule pool invalid.',
      diagnostics: [{ scope: 'project', source: 'x', subject: null, message: 'bad' }],
    });
    expect(api.saving()).toBeFalse();
  });

  it('exports the rule set under the file name the API suggests', async () => {
    const exporting = api.exportRuleSet();
    http.expectOne(candidate => candidate.url === '/api/repos/default/rules/export')
      .flush('{"schemaVersion":1}', { headers: { 'Content-Disposition': 'attachment; filename="rule-set-default-20260928.json"' } });
    expect(await exporting).toEqual({ fileName: 'rule-set-default-20260928.json', text: '{"schemaVersion":1}' });
  });

  it('keeps an invalid rule pool visible for repair when only the prompt preview fails', async () => {
    const loading = api.load();
    http.expectOne('/api/repos/default/rules').flush({ catalogueVersion: '1.6.0', rules: [], traces: [], sources: [], valid: false,
      diagnostics: [{ scope: 'project', source: '.quality/rules/overrides.json', subject: 'QS-CS-999', message: 'unknown' }] });
    http.expectOne('/api/repos/default/inputs').flush({ title: 'Review input is invalid' }, { status: 422, statusText: 'Unprocessable' });
    await loading;
    expect(api.catalogue()?.valid).toBeFalse();
    expect(api.inputPreview()).toBeNull();
    expect(api.previewError()).toBe('Review input is invalid');
    expect(api.error()).toBe('');
  });
});
