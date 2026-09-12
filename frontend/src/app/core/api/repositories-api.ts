import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import { AgentStudioImportResponse, RepositoryRegistration, RepositoryRegistrationRequest } from '../models/contracts';

const LEGACY_DEFAULT: RepositoryRegistration = {
  id: 'default', displayName: 'Default repository', rootPath: '', globalInputsDirectory: null,
  inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false,
  defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
};

/** The repository registry: which repositories exist, and onboarding, editing, and archiving them. */
@Injectable({ providedIn: 'root' })
export class RepositoriesApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);

  readonly repositories = signal<RepositoryRegistration[]>([]);
  readonly selectedRepository = computed(() =>
    this.repositories().find(repository => repository.id === this.context.selectedRepositoryId()) ?? null);

  async load(preferredId?: string | null): Promise<boolean> {
    // Claim the preferred repository before the round trip: a restored session must keep asking
    // for the repository it left off at even when the registry request never answers.
    if (preferredId) this.context.selectedRepositoryId.set(preferredId);
    try {
      const result = await firstValueFrom(this.http.get<{ repositories: RepositoryRegistration[]; defaultRepositoryId: string }>('/api/repos'));
      this.context.registryUnavailable.set(false);
      this.context.legacyApi.set(false);
      this.repositories.set(result.repositories);
      const selected = result.repositories.some(repository => repository.id === preferredId)
        ? preferredId!
        : result.repositories.some(repository => repository.id === this.context.selectedRepositoryId())
          ? this.context.selectedRepositoryId()
          : result.defaultRepositoryId;
      this.context.selectedRepositoryId.set(selected);
      return true;
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) {
        // A pre-registry server still exposes the legacy default endpoints.
        this.context.registryUnavailable.set(false);
        this.context.legacyApi.set(true);
        this.repositories.set([LEGACY_DEFAULT]);
        this.context.selectedRepositoryId.set('default');
        console.warn(JSON.stringify({ event: 'qs.repositories.legacy-fallback', reason: this.context.errorMessage(error) }));
        return true;
      }
      // Anything else means the registry itself is down. Pretending the single legacy repository
      // exists would hide the outage behind a repository the user never onboarded.
      this.context.registryUnavailable.set(true);
      this.context.connectionState.set('offline');
      this.context.connectionError.set(this.context.errorMessage(error));
      console.warn(JSON.stringify({ event: 'qs.repositories.unavailable', reason: this.context.errorMessage(error) }));
      return false;
    }
  }

  async create(request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const created = await firstValueFrom(this.http.post<RepositoryRegistration>('/api/repos', request));
    await this.load(created.id);
    return created;
  }

  async update(id: string, request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const updated = await firstValueFrom(this.http.put<RepositoryRegistration>(`/api/repos/${encodeURIComponent(id)}`, request));
    await this.load(id);
    return updated;
  }

  async archive(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/repos/${encodeURIComponent(id)}`));
    await this.load(id === this.context.selectedRepositoryId() ? null : this.context.selectedRepositoryId());
  }

  async importFromAgentStudio(): Promise<AgentStudioImportResponse> {
    const result = await firstValueFrom(this.http.post<AgentStudioImportResponse>('/api/repos/import-from-agent-studio', {}));
    console.info(JSON.stringify({ event: 'qs.repositories.agent-studio-import', imported: result.imported, skipped: result.skipped, failed: result.failed }));
    await this.load(this.context.selectedRepositoryId());
    return result;
  }
}
