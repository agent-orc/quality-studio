import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiContext } from './api-context';
import {
  CustomRuleValidation, ReviewInputPreview, ReviewRuleCatalogue, RuleAuditEntry, RuleDiagnostic, RuleImportMode,
  RuleImportResult, RuleOverrideMutation, RuleWriteScope, RuleEffectivenessReport,
} from '../models/review-policy';

/** A rejected rule pool change: the API's sentence plus the located configuration problems. */
export interface RulePoolFailure { message: string; diagnostics: RuleDiagnostic[]; }

/**
 * Loaded on demand by the policy dialog, using the repository's effective backend policy. Also the
 * write path for the rule pool: every change returns the re-resolved pool, which replaces the
 * catalogue shown, and refreshes the prompt input preview it affects.
 */
@Injectable()
export class ReviewPolicyApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);
  private sequence = 0;
  readonly repositoryId = this.context.selectedRepositoryId;
  readonly catalogue = signal<ReviewRuleCatalogue | null>(null);
  readonly inputPreview = signal<ReviewInputPreview | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  /** Why the prompt preview is missing while the pool itself loaded, e.g. an invalid rule pool. */
  readonly previewError = signal('');
  /** The scope rule changes are written to; shared by the pool manager and every rule card. */
  readonly editScope = signal<RuleWriteScope>('project');
  readonly saving = signal(false);
  readonly effectiveness = signal<RuleEffectivenessReport | null>(null);
  readonly effectivenessError = signal('');

  constructor() { inject(DestroyRef).onDestroy(() => { this.sequence++; }); }

  async load(repositoryId = this.repositoryId()): Promise<void> {
    const sequence = ++this.sequence;
    this.catalogue.set(null);
    this.inputPreview.set(null);
    this.error.set('');
    this.previewError.set('');
    this.effectiveness.set(null);
    this.effectivenessError.set('');
    this.loading.set(true);
    const base = this.context.repositoryApiBase(repositoryId);
    try {
      // An invalid rule pool fails the preview (reviews fail closed) while the rules endpoint still
      // answers with its diagnostics; keep the pool visible so it can be repaired here.
      const [catalogue, preview] = await Promise.all([
        firstValueFrom(this.http.get<ReviewRuleCatalogue>(`${base}/rules`)),
        firstValueFrom(this.http.get<ReviewInputPreview>(`${base}/inputs`))
          .then(value => ({ value, error: null }), (error: unknown) => ({ value: null, error })),
      ]);
      if (sequence !== this.sequence || repositoryId !== this.repositoryId()) return;
      this.catalogue.set(catalogue);
      this.inputPreview.set(preview.value);
      if (preview.error) this.previewError.set(this.context.errorMessage(preview.error));
    } catch (error) {
      if (sequence === this.sequence && repositoryId === this.repositoryId()) this.error.set(this.context.errorMessage(error));
    } finally {
      if (sequence === this.sequence && repositoryId === this.repositoryId()) this.loading.set(false);
    }
  }

  async loadEffectiveness(): Promise<void> {
    const repositoryId = this.repositoryId();
    this.effectivenessError.set('');
    try {
      const report = await firstValueFrom(this.http.get<RuleEffectivenessReport>(
        `${this.context.repositoryApiBase(repositoryId)}/rules/effectiveness`));
      if (repositoryId === this.repositoryId()) this.effectiveness.set(report);
    } catch (error) {
      if (repositoryId === this.repositoryId()) this.effectivenessError.set(this.context.errorMessage(error));
    }
  }

  setOverride(ruleId: string, change: RuleOverrideMutation): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.put<ReviewRuleCatalogue>(
      `${base}/rules/overrides/${encodeURIComponent(ruleId)}`, change, { params: this.scopeParams() }));
  }

  removeOverride(ruleId: string, reason: string): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.delete<ReviewRuleCatalogue>(
      `${base}/rules/overrides/${encodeURIComponent(ruleId)}`, { params: this.scopeParams({ reason }) }));
  }

  validateCustomRule(content: string): Promise<CustomRuleValidation> {
    return firstValueFrom(this.http.post<CustomRuleValidation>(
      `${this.base()}/rules/custom/validate`, { content }, { params: this.scopeParams() }));
  }

  saveCustomRule(ruleId: string, content: string, reason: string): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.put<ReviewRuleCatalogue>(
      `${base}/rules/custom/${encodeURIComponent(ruleId)}`, { content, reason }, { params: this.scopeParams() }));
  }

  deleteCustomRule(ruleId: string, reason: string): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.delete<ReviewRuleCatalogue>(
      `${base}/rules/custom/${encodeURIComponent(ruleId)}`, { params: this.scopeParams({ reason }) }));
  }

  setApplicability(packs: string[], reason: string): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.put<ReviewRuleCatalogue>(
      `${base}/rules/applicability`, { packs, reason }, { params: this.scopeParams() }));
  }

  clearApplicability(reason: string): Promise<ReviewRuleCatalogue> {
    return this.mutate(base => this.http.delete<ReviewRuleCatalogue>(
      `${base}/rules/applicability`, { params: this.scopeParams({ reason }) }));
  }

  async loadAudit(): Promise<RuleAuditEntry[]> {
    const response = await firstValueFrom(this.http.get<{ scope: RuleWriteScope; entries: RuleAuditEntry[] }>(
      `${this.base()}/rules/audit`, { params: this.scopeParams({ limit: '100' }) }));
    return response.entries;
  }

  /** The scope's rule set as the file the API offers for download, with its suggested name. */
  async exportRuleSet(): Promise<{ fileName: string; text: string }> {
    const response: HttpResponse<string> = await firstValueFrom(this.http.get(`${this.base()}/rules/export`,
      { params: this.scopeParams(), observe: 'response', responseType: 'text' }));
    const disposition = response.headers.get('Content-Disposition') ?? '';
    const fileName = /filename="([^"]+)"/.exec(disposition)?.[1] ?? `rule-set-${this.editScope()}.json`;
    return { fileName, text: response.body ?? '' };
  }

  async importRuleSet(ruleSet: unknown, mode: RuleImportMode, dryRun: boolean, reason: string): Promise<RuleImportResult> {
    this.saving.set(true);
    try {
      const result = await firstValueFrom(this.http.post<RuleImportResult>(`${this.base()}/rules/import`,
        { ruleSet, mode, dryRun, reason }, { params: this.scopeParams() }));
      if (result.applied) await this.applied(result.pool);
      return result;
    } catch (error) {
      throw this.failure(error);
    } finally {
      this.saving.set(false);
    }
  }

  /** Turns an API rejection into a message and the diagnostics the API located. */
  failure(error: unknown): RulePoolFailure {
    const diagnostics = error instanceof HttpErrorResponse && Array.isArray((error.error as { diagnostics?: unknown })?.diagnostics)
      ? (error.error as { diagnostics: RuleDiagnostic[] }).diagnostics
      : [];
    return { message: this.context.errorMessage(error), diagnostics };
  }

  private async mutate(request: (base: string) => Observable<ReviewRuleCatalogue>): Promise<ReviewRuleCatalogue> {
    this.saving.set(true);
    try {
      const pool = await firstValueFrom(request(this.base()));
      await this.applied(pool);
      return pool;
    } catch (error) {
      throw this.failure(error);
    } finally {
      this.saving.set(false);
    }
  }

  // The pool response is authoritative for rules; the prompt preview is re-read because enabling
  // or re-weighting a rule changes what fits the character budget.
  private async applied(pool: ReviewRuleCatalogue): Promise<void> {
    const repositoryId = this.repositoryId();
    this.catalogue.set(pool);
    try {
      const preview = await firstValueFrom(this.http.get<ReviewInputPreview>(`${this.base()}/inputs`));
      if (repositoryId === this.repositoryId()) {
        this.inputPreview.set(preview);
        this.previewError.set('');
      }
    } catch (error) {
      if (repositoryId === this.repositoryId()) {
        this.inputPreview.set(null);
        this.previewError.set(this.context.errorMessage(error));
      }
    }
  }

  private base(): string { return this.context.repositoryApiBase(this.repositoryId()); }

  private scopeParams(extra: Record<string, string> = {}): Record<string, string> {
    return { scope: this.editScope(), ...extra };
  }
}
