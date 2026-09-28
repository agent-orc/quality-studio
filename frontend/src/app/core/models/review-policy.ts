import { ResolvedInputs, ReviewKind } from './contracts';

/** Where a rule is defined: the built-in library, the host-wide global folder, or this repository. */
export type RuleOrigin = 'built-in' | 'global' | 'project';
/** The two scopes the rule pool API writes to. */
export type RuleWriteScope = 'project' | 'global';
export type RuleSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';

export interface NamedReviewRule {
  id: string; version: string; title: string; technology: string; category: string; kinds: ReviewKind[];
  statement: string; rationale: string; detection: string; goodExample: string; badExample: string;
  severity: string; authoredSeverity: string; enabled: boolean; defaultOn: boolean; autofixable: boolean;
  deterministicRuleIds: string[]; relatedGuideline: string | null; since: string;
  origin?: RuleOrigin; selectedBy?: string[];
}
export interface ReviewRuleTrace {
  id: string; source: string; enabled: boolean; severityOverridden: boolean; reason: string | null;
  kinds: ReviewKind[]; technology: string; adapters: string[];
  origin?: RuleOrigin; selectedBy?: string[]; applicabilityScope?: string;
}
export interface RuleDiagnostic { scope: string; source: string; subject: string | null; message: string; }
export interface RuleOverrideView { id: string; enabled: boolean | null; severity: RuleSeverity | null; reason: string; }
export interface RuleSourceFileView { id: string | null; fileName: string; content: string; }
export interface RuleApplicabilityView { packs: string[]; reason: string; }
export interface RuleScopeView {
  writable: boolean; location: string; overrides: RuleOverrideView[]; applicability: RuleApplicabilityView | null;
  customRules: RuleSourceFileView[]; packs: RuleSourceFileView[];
}
export interface RulePackSelector {
  ids?: string[]; technologies?: string[]; kinds?: ReviewKind[]; categories?: string[]; defaultOn?: boolean;
}
export interface RulePackView {
  id: string; version: string; title: string; description: string; projectTypes: string[];
  include: RulePackSelector[]; origin: RuleOrigin; ruleIds: string[]; selected: boolean;
}
export interface ReviewRuleCatalogue {
  catalogueVersion: string; rules: NamedReviewRule[]; traces: ReviewRuleTrace[]; sources: string[];
  valid?: boolean; diagnostics?: RuleDiagnostic[];
  applicability?: { scope: 'default' | RuleWriteScope; packs: string[]; reason: string | null };
  packs?: RulePackView[];
  scopes?: { project: RuleScopeView; global: RuleScopeView; sharedGlobal: RuleScopeView };
}
export interface ReviewInputPreview { level: string; kinds: Partial<Record<ReviewKind, ResolvedInputs>>; }

export interface RuleOverrideMutation { enabled: boolean | null; severity: RuleSeverity | null; reason: string; }
export interface CustomRuleValidation {
  valid: boolean; id: string; rule: NamedReviewRule | null; diagnostics: RuleDiagnostic[];
}
export interface RuleAuditEntry {
  schemaVersion: number; at: string; actor: string; scope: RuleWriteScope; action: string; target: string; reason: string;
  before?: unknown; after?: unknown;
}
export interface RuleImportChange { kind: string; id: string; change: 'added' | 'updated' | 'removed' | 'unchanged'; }
export interface RuleImportResult {
  valid: boolean; applied: boolean; modifiedSinceExport: boolean; changes: RuleImportChange[];
  diagnostics: RuleDiagnostic[]; pool: ReviewRuleCatalogue;
}
export type RuleImportMode = 'merge' | 'replace';
