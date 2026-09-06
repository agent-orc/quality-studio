import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Modal } from './modal';

/**
 * In-app confirmation for a consequential action. Replaces window.confirm, which is unstyled,
 * untestable, and simply ignored when the shell runs inside an iframe embed.
 */
@Component({
  selector: 'qs-confirm-dialog',
  imports: [Modal],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  readonly eyebrow = input('Confirm');
  readonly heading = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input('Confirm');
  readonly danger = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
}
