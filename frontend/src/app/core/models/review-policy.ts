import { ResolvedInputs, ReviewKind } from './contracts';

export interface NamedReviewRule {
  id: string; version: string; title: string; technology: string; category: string; kinds: ReviewKind[];
  statement: string; rationale: string; detection: string; goodExample: string; badExample: string;
  severity: string; authoredSeverity: string; enabled: boolean; defaultOn: boolean; autofixable: boolean;
  deterministicRuleIds: string[]; relatedGuideline: string | null; since: string;
}
export interface ReviewRuleTrace {
  id: string; source: string; enabled: boolean; severityOverridden: boolean; reason: string | null;
  kinds: ReviewKind[]; technology: string; adapters: string[];
}
export interface ReviewRuleCatalogue {
  catalogueVersion: string; rules: NamedReviewRule[]; traces: ReviewRuleTrace[]; sources: string[];
}
export interface ReviewInputPreview { level: string; kinds: Partial<Record<ReviewKind, ResolvedInputs>>; }
