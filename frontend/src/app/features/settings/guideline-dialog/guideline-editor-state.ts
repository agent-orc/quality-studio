import { Injectable, inject, signal } from '@angular/core';
import { QualityApi } from '../../../core/api/quality-api';
import { Guideline, GuidelineDraft, GuidelineImpact, ReviewKind } from '../../../core/models/contracts';
import { ConfirmationState } from '../../../shared/dialog/confirmation-state';
import { GuidelineForm } from './guideline-form';

/** Guideline editing and validation, independent of shell navigation and layout. */
@Injectable()
export class GuidelineEditorState {
  private readonly api = inject(QualityApi);
  private readonly confirmation = inject(ConfirmationState).pending;
  readonly guidelineDialogOpen = signal(false);
  readonly editingGuidelineId = signal<string | null>(null);
  readonly guidelineError = signal('');
  readonly guidelineSaving = signal(false);
  readonly guidelineDryRunning = signal(false);
  readonly guidelineImpact = signal<GuidelineImpact | null>(null);
  guidelineForm: GuidelineForm = this.emptyGuidelineForm();

  openGuidelines(): void {
    this.guidelineDialogOpen.set(true);
    const first = this.api.guidelines()[0];
    if (first) this.editGuideline(first); else this.newGuideline();
  }

  newGuideline(): void {
    this.editingGuidelineId.set(null);
    this.guidelineForm = this.emptyGuidelineForm();
    this.guidelineError.set('');
    this.guidelineImpact.set(null);
  }

  editGuideline(guideline: Guideline): void {
    this.editingGuidelineId.set(guideline.id);
    this.guidelineForm = { id: guideline.id, enabled: guideline.enabled, priority: guideline.priority,
      kinds: guideline.kinds.join(', '), levels: guideline.levels.join(', '), content: guideline.content };
    this.guidelineError.set('');
    this.guidelineImpact.set(null);
  }

  async saveGuideline(): Promise<void> {
    this.guidelineSaving.set(true); this.guidelineError.set('');
    try {
      const draft = this.guidelineDraft();
      const existing = this.editingGuidelineId();
      const saved = existing ? await this.api.updateGuideline(existing, draft) : await this.api.createGuideline(draft);
      this.editGuideline(saved);
    } catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
    finally { this.guidelineSaving.set(false); }
  }

  deleteGuideline(): void {
    const id = this.editingGuidelineId();
    if (!id) return;
    this.confirmation.set({
      eyebrow: 'Repository policy',
      heading: `Delete guideline ${id}?`,
      message: 'The guideline file is removed from the repository. Reviews started afterwards no longer apply it.',
      confirmLabel: 'Delete file',
      danger: true,
      confirm: () => this.confirmDeleteGuideline(id),
    });
  }

  private async confirmDeleteGuideline(id: string): Promise<void> {
    try {
      await this.api.deleteGuideline(id);
      if (this.api.guidelines().length) this.editGuideline(this.api.guidelines()[0]);
      else this.newGuideline();
    } catch (error) {
      this.guidelineError.set(this.api.errorMessage(error));
    }
  }

  async installGuideline(id: string): Promise<void> {
    try { this.editGuideline(await this.api.installGuideline(id)); }
    catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
  }

  async dryRunGuideline(activeKind: ReviewKind): Promise<void> {
    const sample = this.api.file()?.path ?? this.api.allNodes().find(node => node.level === 'file')?.path;
    if (!sample) { this.guidelineError.set('Open or select a sample file first.'); return; }
    this.guidelineDryRunning.set(true); this.guidelineError.set(''); this.guidelineImpact.set(null);
    const requestedKind = this.guidelineDraft().kinds.find(kind => ['code', 'security', 'performance'].includes(kind)) as ReviewKind | undefined;
    try { this.guidelineImpact.set(await this.api.guidelineImpact(this.guidelineDraft(), [sample], requestedKind ?? activeKind)); }
    catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
    finally { this.guidelineDryRunning.set(false); }
  }

  private emptyGuidelineForm(): GuidelineForm {
    return { id: '', enabled: true, priority: 50, kinds: 'code', levels: 'file', content: '' };
  }

  private guidelineDraft(): GuidelineDraft {
    const values = (value: string) => value.split(',').map(item => item.trim().toLowerCase()).filter(Boolean);
    return { id: this.guidelineForm.id.trim(), enabled: this.guidelineForm.enabled,
      priority: Number(this.guidelineForm.priority), kinds: values(this.guidelineForm.kinds),
      levels: values(this.guidelineForm.levels), content: this.guidelineForm.content };
  }

}
