import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import {
  Guideline, GuidelineCatalogueEntry, GuidelineDraft, GuidelineImpact, GuidelineTrace, ReviewKind,
  ScopeRuleMutation, ScopeRulesResponse, ScopeRuleView,
} from './contracts';

/**
 * What future reviews will look at and be judged by: the repository scope rules and the guideline
 * files. Every mutation changes which units are reviewable, so the shell is told through
 * `onScopeChanged` to re-read the hierarchy.
 */
@Injectable({ providedIn: 'root' })
export class ScopeApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);

  readonly scopeRules = signal<ScopeRulesResponse>({ schema: '', rules: [] });
  readonly guidelines = signal<Guideline[]>([]);
  readonly guidelineCatalogue = signal<GuidelineCatalogueEntry[]>([]);
  readonly guidelineTraces = signal<GuidelineTrace[]>([]);

  /** Called after a scope or guideline change, because it moves what the tree contains. */
  onScopeChanged: (() => Promise<void>) | null = null;

  async loadScopeRules(): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.get<ScopeRulesResponse>(`${this.context.repositoryApiBase()}/scope/rules`));
    this.scopeRules.set(response);
    return response;
  }

  previewScopeRule(request: ScopeRuleMutation): Promise<ScopeRuleView> {
    return firstValueFrom(this.http.post<ScopeRuleView>(`${this.context.repositoryApiBase()}/scope/rules/preview`, request));
  }

  async addScopeRule(request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.post<ScopeRulesResponse>(`${this.context.repositoryApiBase()}/scope/rules`, request));
    this.scopeRules.set(response);
    await this.onScopeChanged?.();
    return response;
  }

  async updateScopeRule(index: number, request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.put<ScopeRulesResponse>(
      `${this.context.repositoryApiBase()}/scope/rules/${index}`, request));
    this.scopeRules.set(response);
    await this.onScopeChanged?.();
    return response;
  }

  async deleteScopeRule(index: number): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.delete<ScopeRulesResponse>(`${this.context.repositoryApiBase()}/scope/rules/${index}`));
    this.scopeRules.set(response);
    await this.onScopeChanged?.();
    return response;
  }

  async createGuideline(draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.context.repositoryApiBase()}/guidelines`, draft));
    await this.onScopeChanged?.();
    return guideline;
  }

  async updateGuideline(existingId: string, draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.put<Guideline>(`${this.context.repositoryApiBase()}/guidelines/${encodeURIComponent(existingId)}`, draft));
    await this.onScopeChanged?.();
    return guideline;
  }

  async deleteGuideline(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`${this.context.repositoryApiBase()}/guidelines/${encodeURIComponent(id)}`));
    await this.onScopeChanged?.();
  }

  async installGuideline(catalogueId: string): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.context.repositoryApiBase()}/guidelines/catalog/${encodeURIComponent(catalogueId)}/install`, {}));
    await this.onScopeChanged?.();
    return guideline;
  }

  guidelineImpact(guideline: GuidelineDraft, samplePaths: string[], kind: ReviewKind): Promise<GuidelineImpact> {
    return firstValueFrom(this.http.post<GuidelineImpact>(`${this.context.repositoryApiBase()}/guidelines/impact`, { guideline, samplePaths, kind }));
  }
}
