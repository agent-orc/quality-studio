import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ChangeReviewDocument, ChangeReviewRange } from '../models/contracts';

/**
 * The change-review store of one repository. Reading a range never spends; `runReview` is the
 * spend route and answers from the store when the range's key already holds a verdict.
 */
@Injectable({ providedIn: 'root' })
export class ChangeReviewsApi {
  private readonly http = inject(HttpClient);

  range(repositoryId: string, base: string, head: string, previous: string | null): Promise<ChangeReviewRange> {
    const params: Record<string, string> = { base, head };
    if (previous) params['previous'] = previous;
    return firstValueFrom(this.http.get<ChangeReviewRange>(
      `/api/v1/repos/${encodeURIComponent(repositoryId)}/change-reviews`, { params }));
  }

  runReview(repositoryId: string, base: string, head: string): Promise<ChangeReviewDocument> {
    return firstValueFrom(this.http.post<ChangeReviewDocument>(
      `/api/repos/${encodeURIComponent(repositoryId)}/change-review`, { baseSha: base, headSha: head }));
  }
}
