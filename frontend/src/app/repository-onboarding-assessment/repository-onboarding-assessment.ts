import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RepositoryOnboardingAssessment } from '../quality-api';

@Component({
  selector: 'qs-repository-onboarding-assessment',
  templateUrl: './repository-onboarding-assessment.html',
  styleUrl: './repository-onboarding-assessment.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RepositoryOnboardingAssessmentView {
  readonly assessment = input.required<RepositoryOnboardingAssessment>();
}
