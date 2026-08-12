import { ChangeDetectionStrategy, Component, ViewEncapsulation } from '@angular/core';

/** Loads assessment and policy styles with the lazy review panel. */
@Component({
  selector: 'qs-review-policy-styles',
  template: '',
  styleUrl: './review-policy-styles.css',
  encapsulation: ViewEncapsulation.None,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPolicyStyles {}
