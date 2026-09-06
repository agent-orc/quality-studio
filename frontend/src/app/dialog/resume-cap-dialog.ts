import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { ReviewRun } from '../contracts';
import { formatTokenCount, parseTokenCount } from '../format';
import { Modal } from './modal';

export interface ResumeCap {
  tokenCap?: number | null;
  costCap?: number | null;
}

/**
 * Raises the cap of a capped run and resumes it. The review launcher and the review panel both
 * offered this through window.prompt with slightly different wording; they now share this dialog,
 * which also works in the iframe embed where native prompts are ignored.
 */
@Component({
  selector: 'qs-resume-cap-dialog',
  imports: [Modal],
  templateUrl: './resume-cap-dialog.html',
  styleUrl: './resume-cap-dialog.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResumeCapDialog {
  readonly run = input.required<ReviewRun>();
  readonly cancelled = output<void>();
  readonly submitted = output<ResumeCap>();

  /** A token-capped run raises tokens; anything else raises the cost cap. */
  readonly byTokens = computed(() => this.run().tokenCap !== null);
  readonly currency = computed(() => this.run().currency ?? 'USD');
  readonly suggestion = computed(() => {
    const current = this.run().tokenCap ?? this.run().costCap;
    if (current === null || current === undefined) return '';
    return this.byTokens() ? formatTokenCount(current * 2) : String(current * 2);
  });
  readonly value = signal('');
  readonly error = signal('');
  readonly parsed = computed(() => parseCap(this.entered(), this.byTokens()));

  private readonly touched = signal(false);

  entered(): string {
    return this.touched() ? this.value() : this.suggestion();
  }

  setValue(value: string): void {
    this.touched.set(true);
    this.value.set(value);
    this.error.set('');
  }

  submit(): void {
    const cap = this.parsed();
    if (cap === null) {
      this.error.set(this.byTokens()
        ? 'Enter a positive token cap, for example 200k or 0.2M.'
        : `Enter a positive cost cap in ${this.currency()}.`);
      return;
    }
    this.submitted.emit(this.byTokens() ? { tokenCap: cap } : { costCap: cap });
  }
}

/** Shared by the dialog and its spec: the one reading of an entered cap. */
export function parseCap(entered: string, byTokens: boolean): number | null {
  if (byTokens) return parseTokenCount(entered);
  const cost = Number(entered);
  return Number.isFinite(cost) && cost > 0 ? cost : null;
}
