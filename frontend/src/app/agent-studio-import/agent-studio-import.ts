import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { AgentStudioImportResponse } from '../contracts';
import { Modal, ModalBackdrop } from '../dialog/modal';

/** Result surface for importing Agent Studio projects as repositories. Deferred: it is a one-off. */
@Component({
  selector: 'qs-agent-studio-import',
  imports: [Modal, ModalBackdrop],
  templateUrl: './agent-studio-import.html',
  styleUrls: ['../dialog/dialog-shell.css', './agent-studio-import.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgentStudioImport {
  readonly importing = input(false);
  readonly error = input('');
  readonly result = input<AgentStudioImportResponse | null>(null);
  readonly closed = output<void>();
  readonly retryRequested = output<void>();
}
