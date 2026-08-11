import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  FindingAssessmentMutationRequest,
  FindingSuppressionPreview,
  FindingSuppressionRule,
  QualityApi,
  ReviewKind,
} from '../quality-api';

@Injectable()
export class ReviewEvidenceApi {
  private readonly http = inject(HttpClient);
  private readonly api = inject(QualityApi);

  async mutateAssessment(request: FindingAssessmentMutationRequest): Promise<void> {
    await firstValueFrom(this.http.post(this.api.repositoryEndpoint('/findings/assessment'), request));
    await this.api.loadFile(request.path);
  }

  async suppressExact(request: {
    path: string;
    kind: ReviewKind;
    fingerprint: string;
    author: string;
    reason: string;
    expiresAt?: string | null;
    expectedRevision: number;
  }): Promise<void> {
    await firstValueFrom(this.http.post(this.api.repositoryEndpoint('/findings/suppressions/exact'), request));
    await this.api.loadFile(request.path);
  }

  previewSuppression(rule: FindingSuppressionRule): Promise<FindingSuppressionPreview> {
    return firstValueFrom(this.http.post<FindingSuppressionPreview>(
      this.api.repositoryEndpoint('/findings/suppressions/preview'), { rule }));
  }

  async saveSuppression(
    rule: FindingSuppressionRule,
    expectedRevision: number,
    pathToReload: string,
  ): Promise<FindingSuppressionPreview> {
    const result = await firstValueFrom(this.http.put<FindingSuppressionPreview>(
      this.api.repositoryEndpoint(`/findings/suppressions/${encodeURIComponent(rule.id)}`),
      { rule, expectedRevision }));
    await this.api.loadFile(pathToReload);
    return result;
  }
}
