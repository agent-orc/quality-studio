import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { localMutationProtectionInterceptor } from './local-mutation-protection';

describe('localMutationProtectionInterceptor', () => {
  let client: HttpClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([localMutationProtectionInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('obtains one server nonce before forwarding a local mutation', async () => {
    const result = firstValueFrom(client.post('/api/repos', { displayName: 'Fixture' }));
    http.expectOne('/api/security/csrf').flush({
      token: 'issued-test-token',
      expiresAt: '2026-08-11T15:00:00Z',
    });
    await new Promise((resolve) => setTimeout(resolve));

    const mutation = http.expectOne('/api/repos');
    expect(mutation.request.headers.get('X-Quality-CSRF-Token')).toBe('issued-test-token');
    mutation.flush({ id: 'fixture' });
    await result;
  });
});
