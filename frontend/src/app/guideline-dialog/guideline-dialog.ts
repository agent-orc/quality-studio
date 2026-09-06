import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { Guideline, GuidelineCatalogueEntry, GuidelineImpact, GuidelineTrace } from '../contracts';
import { Modal, ModalBackdrop } from '../dialog/modal';
import { GuidelineForm } from './guideline-form';

/**
 * Editor for the repository's guideline files, with the starter catalogue, the finding trace of a
 * rule, and a dry run against one sample file. It is deferred: guidelines are a policy surface, not
 * part of opening a repository, and its form is the shell's only remaining use of FormsModule.
 */
@Component({
  selector: 'qs-guideline-dialog',
  imports: [FormsModule, Modal, ModalBackdrop],
  templateUrl: './guideline-dialog.html',
  styleUrls: ['../dialog/dialog-shell.css', './guideline-dialog.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GuidelineDialog {
  readonly guidelines = input.required<Guideline[]>();
  readonly catalogue = input.required<GuidelineCatalogueEntry[]>();
  readonly traces = input.required<GuidelineTrace[]>();
  readonly editingGuidelineId = input<string | null>(null);
  readonly form = input.required<GuidelineForm>();
  readonly error = input('');
  readonly saving = input(false);
  readonly dryRunning = input(false);
  readonly impact = input<GuidelineImpact | null>(null);

  readonly closed = output<void>();
  readonly newRequested = output<void>();
  readonly editRequested = output<Guideline>();
  readonly installRequested = output<string>();
  readonly saveRequested = output<void>();
  readonly deleteRequested = output<void>();
  readonly dryRunRequested = output<void>();
  readonly traceRequested = output<string>();

  trace(id: string): GuidelineTrace | undefined {
    return this.traces().find(candidate => candidate.guidelineId === id);
  }

  installed(id: string): boolean {
    return this.guidelines().some(guideline => guideline.id === id);
  }
}
