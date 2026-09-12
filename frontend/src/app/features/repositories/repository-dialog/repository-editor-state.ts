import { Injectable, inject, signal } from '@angular/core';
import { QualityApi } from '../../../core/api/quality-api';
import { RepositoryRegistration, RepositoryRegistrationRequest, ReviewKind } from '../../../core/models/contracts';
import { formatTokenCount, parseTokenCount } from '../../../shared/utils/format';

/** Repository form state and mutually exclusive token/cost budgets. */
@Injectable()
export class RepositoryEditorState {
  private readonly api = inject(QualityApi);
  readonly editingRepositoryId = signal<string | null>(null);
  readonly repositoryError = signal('');
  readonly repositoryTokenCapError = signal('');
  readonly repositorySaving = signal(false);
  readonly repositoryDialogOpen = signal(false);
  repositoryForm: RepositoryRegistrationRequest = this.emptyRepositoryForm();
  repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);

  reset(): void {
    this.repositoryForm = this.emptyRepositoryForm();
    this.repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);
    this.editingRepositoryId.set(null);
    this.repositoryError.set('');
    this.repositoryTokenCapError.set('');
  }

  editRepository(repository: RepositoryRegistration): void {
    this.editingRepositoryId.set(repository.id);
    this.repositoryForm = {
      id: repository.id,
      displayName: repository.displayName,
      rootPath: repository.rootPath,
      globalInputsDirectory: repository.globalInputsDirectory,
      inputBudgetCharacters: repository.inputBudgetCharacters,
      enabledReviewKinds: [...repository.enabledReviewKinds],
      defaultReviewTokenCap: repository.defaultReviewTokenCap,
      defaultReviewCostCap: repository.defaultReviewCostCap,
    };
    this.repositoryTokenCapText = formatTokenCount(repository.defaultReviewTokenCap);
    this.repositoryError.set('');
    this.repositoryTokenCapError.set('');
  }

  toggleReviewKind(kind: ReviewKind, enabled: boolean): void {
    this.repositoryForm.enabledReviewKinds = enabled
      ? [...new Set([...this.repositoryForm.enabledReviewKinds, kind])]
      : this.repositoryForm.enabledReviewKinds.filter(existing => existing !== kind);
  }

  setDefaultTokenCap(value: string): void {
    this.repositoryTokenCapText = value;
    if (!value.trim()) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapError.set('');
      return;
    }
    const parsed = parseTokenCount(value);
    if (parsed === null || parsed > 1_000_000_000) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapError.set('Enter 1 to 1B tokens, for example 100k or 0.1M.');
      return;
    }
    this.repositoryForm.defaultReviewTokenCap = parsed;
    this.repositoryTokenCapError.set('');
    this.repositoryForm.defaultReviewCostCap = null;
  }

  normalizeDefaultTokenCap(): void {
    if (this.repositoryForm.defaultReviewTokenCap !== null) {
      this.repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);
    }
  }

  setDefaultCostCap(value: number | null): void {
    this.repositoryForm.defaultReviewCostCap = value;
    if (value !== null) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapText = '';
      this.repositoryTokenCapError.set('');
    }
  }

  private emptyRepositoryForm(): RepositoryRegistrationRequest {
    return { displayName: '', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], defaultReviewTokenCap: 100000, defaultReviewCostCap: null };
  }

  async saveRepository(onCreated: (id: string) => Promise<void>): Promise<void> {
    this.repositorySaving.set(true);
    this.repositoryError.set('');
    try {
      const editingId = this.editingRepositoryId();
      const saved = editingId
        ? await this.api.updateRepository(editingId, this.repositoryForm)
        : await this.api.createRepository(this.repositoryForm);
      if (!editingId) await onCreated(saved.id);
      this.repositoryDialogOpen.set(false);
    } catch (error) {
      this.repositoryError.set(this.api.errorMessage(error));
    } finally {
      this.repositorySaving.set(false);
    }
  }

}
