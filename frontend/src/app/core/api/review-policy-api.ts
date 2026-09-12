import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiContext } from './api-context';
import { ReviewInputPreview, ReviewRuleCatalogue } from '../models/review-policy';

/** Loaded on demand by the policy dialog, using the repository's effective backend policy. */
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

  constructor() { inject(DestroyRef).onDestroy(() => { this.sequence++; }); }

  async load(repositoryId = this.repositoryId()): Promise<void> {
    const sequence = ++this.sequence;
    this.catalogue.set(null);
    this.inputPreview.set(null);
    this.error.set('');
    this.loading.set(true);
    const base = this.context.repositoryApiBase(repositoryId);
    try {
      const [catalogue, preview] = await Promise.all([
        firstValueFrom(this.http.get<ReviewRuleCatalogue>(`${base}/rules`)),
        firstValueFrom(this.http.get<ReviewInputPreview>(`${base}/inputs`)),
      ]);
      if (sequence !== this.sequence || repositoryId !== this.repositoryId()) return;
      this.catalogue.set(catalogue);
      this.inputPreview.set(preview);
    } catch (error) {
      if (sequence === this.sequence && repositoryId === this.repositoryId()) this.error.set(this.context.errorMessage(error));
    } finally {
      if (sequence === this.sequence && repositoryId === this.repositoryId()) this.loading.set(false);
    }
  }
}
