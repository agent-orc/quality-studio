import { ChangeDetectionStrategy, Component, ViewEncapsulation } from '@angular/core';

/** Loads detail-only global styles with the lazy review panel instead of the application shell. */
@Component({
  selector: 'qs-review-detail-styles',
  template: '',
  styleUrl: './review-detail-styles.css',
  encapsulation: ViewEncapsulation.None,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewDetailStyles {}
