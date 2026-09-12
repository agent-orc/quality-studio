import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiContext } from './api-context';
import { FindingsApi } from './findings-api';

describe('FindingsApi file request ownership', () => {
  let api: FindingsApi;
  let context: ApiContext;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(FindingsApi);
    context = TestBed.inject(ApiContext);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('retains file B when the older request for A succeeds after B', async () => {
    const first = api.loadFile('A.cs');
    const pendingA = http.expectOne('/api/repos/default/file?path=A.cs');
    const second = api.loadFile('B.cs');
    http.expectOne('/api/repos/default/file?path=B.cs').flush({ path: 'B.cs', content: 'File B' });
    await second;
    context.connectionState.set('offline');

    pendingA.flush({ path: 'A.cs', content: 'File A' });
    await first;

    expect(api.file()?.path).toBe('B.cs');
    expect(api.file()?.content).toBe('File B');
    expect(api.fileError()).toBeNull();
    expect(api.loading()).toBeFalse();
    expect(context.connectionState()).withContext('stale success cannot restore connectivity').toBe('offline');
  });

  it('ignores an old failure while the newer file is still loading', async () => {
    context.connectionState.set('live');
    const first = api.loadFile('A.cs');
    const pendingA = http.expectOne('/api/repos/default/file?path=A.cs');
    const second = api.loadFile('B.cs');
    const pendingB = http.expectOne('/api/repos/default/file?path=B.cs');

    pendingA.error(new ProgressEvent('error'));
    await first;
    expect(api.loading()).toBeTrue();
    expect(api.fileError()).toBeNull();
    expect(context.connectionState()).toBe('live');

    pendingB.flush({ path: 'B.cs', content: 'File B' });
    await second;
    expect(api.loading()).toBeFalse();
    expect(api.file()?.path).toBe('B.cs');
  });

  for (const failure of [false, true]) {
    it('invalidates a pending file request when cleared: ' + (failure ? 'error' : 'success'), async () => {
      const loading = api.loadFile('A.cs');
      const pending = http.expectOne('/api/repos/default/file?path=A.cs');
      api.clearFile();
      context.connectionState.set('connecting');

      if (failure) pending.error(new ProgressEvent('error'));
      else pending.flush({ path: 'A.cs', content: 'File A' });
      await loading;

      expect(api.file()).toBeNull();
      expect(api.fileError()).toBeNull();
      expect(api.loading()).toBeFalse();
      expect(context.connectionState()).toBe('connecting');
    });
  }

  it('rejects a prior repository response even before a new file request starts', async () => {
    const loading = api.loadFile('A.cs');
    const pending = http.expectOne('/api/repos/default/file?path=A.cs');
    context.selectedRepositoryId.set('other');
    context.connectionState.set('connecting');
    pending.flush({ path: 'A.cs', content: 'Wrong repository' });
    await loading;

    expect(api.file()).toBeNull();
    expect(api.fileError()).toBeNull();
    expect(api.loading()).withContext('old completion does not change the new selection loading state').toBeTrue();
    expect(context.connectionState()).toBe('connecting');
    api.clearFile();
  });
});
