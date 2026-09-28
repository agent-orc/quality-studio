import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { ReviewPolicyApi, RulePoolFailure } from '../../../../core/api/review-policy-api';
import { NamedReviewRule, RuleSeverity } from '../../../../core/models/review-policy';

type Enablement = 'inherit' | 'enabled' | 'disabled';

/**
 * Adjusts one rule in the selected write scope: enablement, severity and the reason the API
 * requires and records in the audit trail. The form starts from the scope's current override.
 */
@Component({
  selector: 'qs-rule-override-editor',
  templateUrl: './rule-override-editor.html',
  styleUrl: './rule-pool.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RuleOverrideEditor {
  readonly rule = input.required<NamedReviewRule>();
  readonly policy = inject(ReviewPolicyApi);
  readonly severities: RuleSeverity[] = ['critical', 'high', 'medium', 'low', 'info'];
  readonly scopeView = computed(() => this.policy.catalogue()?.scopes?.[this.policy.editScope()] ?? null);
  readonly existing = computed(() => this.scopeView()?.overrides.find(entry => entry.id === this.rule().id) ?? null);
  readonly enablement = linkedSignal<Enablement>(() => {
    const enabled = this.existing()?.enabled;
    return enabled == null ? 'inherit' : enabled ? 'enabled' : 'disabled';
  });
  readonly severity = linkedSignal<RuleSeverity | 'inherit'>(() => this.existing()?.severity ?? 'inherit');
  readonly reason = linkedSignal(() => this.existing()?.reason ?? '');
  readonly failure = signal<RulePoolFailure | null>(null);
  readonly saved = signal('');
  readonly hasReason = computed(() => this.reason().trim().length > 0);
  readonly adjusts = computed(() => this.enablement() !== 'inherit' || this.severity() !== 'inherit');
  readonly canSave = computed(() => this.adjusts() && this.hasReason() && !this.policy.saving());
  readonly scopeLabel = computed(() => this.policy.editScope() === 'global' ? 'all repositories (global)' : 'this repository');

  setEnablement(value: string): void {
    if (value === 'inherit' || value === 'enabled' || value === 'disabled') this.enablement.set(value);
  }

  setSeverity(value: string): void {
    this.severity.set(this.severities.includes(value as RuleSeverity) ? value as RuleSeverity : 'inherit');
  }

  async save(): Promise<void> {
    if (!this.canSave()) return;
    const enablement = this.enablement();
    const severity = this.severity();
    await this.run(() => this.policy.setOverride(this.rule().id, {
      enabled: enablement === 'inherit' ? null : enablement === 'enabled',
      severity: severity === 'inherit' ? null : severity,
      reason: this.reason().trim(),
    }), 'Override saved.');
  }

  async remove(): Promise<void> {
    if (!this.existing() || !this.hasReason()) return;
    await this.run(() => this.policy.removeOverride(this.rule().id, this.reason().trim()), 'Override removed.');
  }

  private async run(action: () => Promise<unknown>, message: string): Promise<void> {
    this.failure.set(null);
    this.saved.set('');
    try {
      await action();
      this.saved.set(message);
    } catch (error) {
      this.failure.set(error as RulePoolFailure);
    }
  }
}
