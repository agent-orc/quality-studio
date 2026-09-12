import { Injectable, signal } from '@angular/core';

export interface PendingConfirmation {
  eyebrow: string;
  heading: string;
  message: string;
  confirmLabel: string;
  danger: boolean;
  confirm: () => void | Promise<void>;
}

/** One explicit user decision, owned by the shell and shared by its feature editors. */
@Injectable()
export class ConfirmationState {
  readonly pending = signal<PendingConfirmation | null>(null);

  async run(): Promise<void> {
    const decision = this.pending();
    this.pending.set(null);
    await decision?.confirm();
  }
}
