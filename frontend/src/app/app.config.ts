import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';

import { apiInterceptor } from './core/api/api-interceptor';

/**
 * The shell has a single view and keeps its position in query parameters, which it writes and
 * restores itself (see App.syncUrl / App.onPopState). No router is configured: the route table was
 * empty, so the router only added weight to the initial bundle without owning any URL state.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(withInterceptors([apiInterceptor])),
  ],
};
