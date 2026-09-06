import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';

import { ApiAccess } from '../api-access';

/**
 * Settings surface for the hosted API's bearer credential. A local API needs nothing here; the
 * dialog exists so a hosted deployment can be reached, and so a 401 has somewhere to send the
 * reader instead of failing silently.
 */
@Component({
  selector: 'qs-api-access-dialog',
  templateUrl: './api-access-dialog.html',
  styleUrl: './api-access-dialog.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApiAccessDialog {
  private readonly access = inject(ApiAccess);
  /** Set when the dialog was opened by a rejected request rather than from the menu. */
  readonly rejected = input(false);
  readonly closed = output<void>();

  readonly draft = signal(this.access.token());
  readonly revealed = signal(false);
  readonly configured = computed(() => this.access.configured());
  readonly saved = signal(false);

  setDraft(value: string): void {
    this.draft.set(value);
    this.saved.set(false);
  }

  save(): void {
    this.access.setToken(this.draft());
    this.saved.set(true);
  }

  clear(): void {
    this.access.clear();
    this.draft.set('');
    this.saved.set(false);
  }
}
