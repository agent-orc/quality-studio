import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ReviewPolicyApi, RulePoolFailure } from '../../../../core/api/review-policy-api';
import {
  CustomRuleValidation, RuleAuditEntry, RuleImportMode, RuleImportResult, RuleSourceFileView, RuleWriteScope,
} from '../../../../core/models/review-policy';

type PoolPanel = 'none' | 'packs' | 'custom' | 'transfer' | 'audit';

/** Rule sets are small documents; anything larger is not one. */
const MAX_IMPORT_BYTES = 1024 * 1024;

/**
 * The rule pool's management surface: which packs decide the rule set, custom rules in the
 * selected scope, rule-set import and export, and the audit trail. Rule-level overrides are
 * edited on each rule card. Every write goes through the API, which validates the whole pool
 * before it changes a file.
 */
@Component({
  selector: 'qs-rule-pool-manager',
  imports: [DatePipe],
  templateUrl: './rule-pool-manager.html',
  styleUrl: './rule-pool.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RulePoolManager {
  readonly policy = inject(ReviewPolicyApi);
  readonly panel = signal<PoolPanel>('none');
  readonly failure = signal<RulePoolFailure | null>(null);
  readonly notice = signal('');

  readonly catalogue = this.policy.catalogue;
  readonly scope = this.policy.editScope;
  readonly managed = computed(() => !!this.catalogue()?.scopes);
  readonly scopeView = computed(() => this.catalogue()?.scopes?.[this.scope()] ?? null);
  readonly globalWritable = computed(() => this.catalogue()?.scopes?.global.writable ?? false);
  readonly diagnostics = computed(() => this.catalogue()?.diagnostics ?? []);
  readonly enabledCount = computed(() => this.catalogue()?.rules.filter(rule => rule.enabled).length ?? 0);
  readonly packs = computed(() => this.catalogue()?.packs ?? []);
  readonly applicability = computed(() => {
    const catalogue = this.catalogue();
    const applied = catalogue?.applicability;
    if (!applied) return null;
    const titles = applied.packs.map(id => this.packs().find(pack => pack.id === id)?.title ?? id);
    const from = applied.scope === 'default' ? 'shipped default' : applied.scope === 'global' ? 'set for all repositories' : 'set for this repository';
    return { titles: titles.length ? titles.join(' + ') : 'No packs: only explicitly enabled rules apply', from, reason: applied.reason };
  });
  readonly scopeLabel = computed(() => this.scope() === 'global' ? 'all repositories (global)' : 'this repository');

  readonly packSelection = signal<string[]>([]);
  readonly packReason = signal('');
  readonly packRows = computed(() => {
    const selected = new Set(this.packSelection());
    return this.packs().map(pack => ({
      pack,
      checked: selected.has(pack.id),
      meta: `${pack.origin} · v${pack.version} · selects ${pack.ruleIds.length} rules here`
        + (pack.projectTypes.length ? ` · for ${pack.projectTypes.join(', ')}` : ''),
    }));
  });
  readonly summary = computed(() => {
    const catalogue = this.catalogue();
    return catalogue
      ? `${this.enabledCount()} of ${catalogue.rules.length} rules enabled · library ${catalogue.catalogueVersion} · sources: ${catalogue.sources.join(', ')}`
      : '';
  });
  readonly canSaveApplicability = computed(() => this.packReason().trim().length > 0 && !this.policy.saving());

  readonly draftOpen = signal(false);
  readonly draftExisting = signal<string | null>(null);
  readonly draftContent = signal('');
  readonly draftReason = signal('');
  readonly validation = signal<CustomRuleValidation | null>(null);
  readonly deleteReason = signal('');
  readonly canSaveDraft = computed(() => this.validation()?.valid === true && this.draftReason().trim().length > 0
    && !this.policy.saving());
  readonly canDelete = computed(() => this.deleteReason().trim().length > 0 && !this.policy.saving());

  readonly importFileName = signal('');
  readonly importDocument = signal<unknown>(null);
  readonly importMode = signal<RuleImportMode>('merge');
  readonly importReason = signal('');
  readonly importPreview = signal<RuleImportResult | null>(null);
  readonly importChanges = computed(() => this.importPreview()?.changes.filter(change => change.change !== 'unchanged') ?? []);
  readonly canApplyImport = computed(() => this.importPreview()?.valid === true && !this.importPreview()?.applied
    && this.importReason().trim().length > 0 && !this.policy.saving());

  readonly audit = signal<RuleAuditEntry[] | null>(null);

  toggle(panel: PoolPanel): void {
    const next = this.panel() === panel ? 'none' : panel;
    this.panel.set(next);
    this.failure.set(null);
    this.notice.set('');
    if (next === 'packs') this.resetPackSelection();
    if (next === 'audit') void this.loadAudit();
  }

  setScope(value: string): void {
    const scope: RuleWriteScope = value === 'global' && this.globalWritable() ? 'global' : 'project';
    this.scope.set(scope);
    this.failure.set(null);
    this.notice.set('');
    this.importPreview.set(null);
    this.draftOpen.set(false);
    if (this.panel() === 'packs') this.resetPackSelection();
    if (this.panel() === 'audit') void this.loadAudit();
  }

  togglePack(id: string, checked: boolean): void {
    this.packSelection.update(current => checked
      ? [...current.filter(value => value !== id), id]
      : current.filter(value => value !== id));
  }

  async saveApplicability(): Promise<void> {
    if (!this.canSaveApplicability()) return;
    await this.run(() => this.policy.setApplicability(this.packSelection(), this.packReason().trim()), 'Applicability saved.');
  }

  async useDefaults(): Promise<void> {
    if (!this.packReason().trim()) return;
    await this.run(() => this.policy.clearApplicability(this.packReason().trim()), `Applicability for ${this.scopeLabel()} removed.`);
    this.resetPackSelection();
  }

  newCustomRule(): void {
    this.openDraft(null, ruleTemplate(this.scope() === 'global' ? 'HOST-GN-001' : 'TEAM-GN-001', new Date().toISOString().slice(0, 10)));
  }

  editCustomRule(file: RuleSourceFileView): void {
    this.openDraft(file.id, file.content);
  }

  setDraftContent(value: string): void {
    this.draftContent.set(value);
    this.validation.set(null);
  }

  async validateDraft(): Promise<void> {
    this.failure.set(null);
    try {
      this.validation.set(await this.policy.validateCustomRule(this.draftContent()));
    } catch (error) {
      this.failure.set(this.policy.failure(error));
    }
  }

  async saveDraft(): Promise<void> {
    const validation = this.validation();
    if (!this.canSaveDraft() || !validation) return;
    const saved = await this.run(() => this.policy.saveCustomRule(validation.id, this.draftContent(), this.draftReason().trim()),
      `Custom rule ${validation.id} saved.`);
    if (saved) this.draftOpen.set(false);
  }

  async deleteCustomRule(id: string | null): Promise<void> {
    if (!id || !this.canDelete()) return;
    await this.run(() => this.policy.deleteCustomRule(id, this.deleteReason().trim()), `Custom rule ${id} deleted.`);
  }

  async exportRuleSet(): Promise<void> {
    this.failure.set(null);
    try {
      const { fileName, text } = await this.policy.exportRuleSet();
      const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      anchor.click();
      URL.revokeObjectURL(url);
      this.notice.set(`Exported ${fileName}.`);
    } catch (error) {
      this.failure.set(this.policy.failure(error));
    }
  }

  async readImportFile(files: FileList | null): Promise<void> {
    this.importPreview.set(null);
    this.importDocument.set(null);
    this.failure.set(null);
    const file = files?.item(0);
    if (!file) return;
    this.importFileName.set(file.name);
    if (file.size > MAX_IMPORT_BYTES) {
      this.failure.set({ message: `${file.name} is larger than 1 MiB and is not a rule set.`, diagnostics: [] });
      return;
    }
    try {
      this.importDocument.set(JSON.parse(await file.text()));
    } catch {
      this.failure.set({ message: `${file.name} is not valid JSON.`, diagnostics: [] });
    }
  }

  setImportMode(value: string): void {
    this.importMode.set(value === 'replace' ? 'replace' : 'merge');
    this.importPreview.set(null);
  }

  async previewImport(): Promise<void> {
    if (this.importDocument() === null) return;
    await this.importStep(true);
  }

  async applyImport(): Promise<void> {
    if (!this.canApplyImport()) return;
    await this.importStep(false);
  }

  private async importStep(dryRun: boolean): Promise<void> {
    this.failure.set(null);
    this.notice.set('');
    try {
      const result = await this.policy.importRuleSet(this.importDocument(), this.importMode(), dryRun, this.importReason().trim());
      this.importPreview.set(result);
      if (result.applied) this.notice.set(`Imported ${this.importFileName()} into ${this.scopeLabel()}.`);
    } catch (error) {
      this.failure.set(error as RulePoolFailure);
    }
  }

  private openDraft(existing: string | null, content: string): void {
    this.draftExisting.set(existing);
    this.draftContent.set(content);
    this.draftReason.set('');
    this.validation.set(null);
    this.draftOpen.set(true);
  }

  private resetPackSelection(): void {
    const view = this.scopeView();
    this.packSelection.set(view?.applicability?.packs ?? this.catalogue()?.applicability?.packs ?? []);
    this.packReason.set(view?.applicability?.reason ?? '');
  }

  private async loadAudit(): Promise<void> {
    this.audit.set(null);
    try {
      this.audit.set(await this.policy.loadAudit());
    } catch (error) {
      this.failure.set(this.policy.failure(error));
    }
  }

  private async run(action: () => Promise<unknown>, message: string): Promise<boolean> {
    this.failure.set(null);
    this.notice.set('');
    try {
      await action();
      this.notice.set(message);
      return true;
    } catch (error) {
      this.failure.set(error as RulePoolFailure);
      return false;
    }
  }
}

/** The authored rule format with its placeholders, matching the backend's RuleMarkdown.Template. */
export function ruleTemplate(id: string, date: string): string {
  return `---
id: ${id}
version: 1.0.0
title: One imperative sentence naming the rule
technology: generic
kinds: [code]
category: maintainability
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
since: 1.0.0
---

## Statement

What to do, in one or two sentences.

## Rationale

Why it matters in this codebase: the concrete cost of not doing it.

## Detection

What a reviewer looks at, and what does not count as a violation.

## Good example

\`\`\`text
code that follows the rule
\`\`\`

## Bad example

\`\`\`text
code that violates the rule
\`\`\`

## Change history

- 1.0.0 (${date}): Initial rule.
`;
}
