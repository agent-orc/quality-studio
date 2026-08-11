import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ReviewState = 'fresh' | 'stale' | 'policy-drift' | 'missing';
export interface KindState { direct: ReviewState; descendants: ReviewState; overall: ReviewState; score: number | null; band: string | null; metaPath: string | null; }
export interface ScopeExclusion { path: string; reason: string; }
export interface ScopeRuleView { index: number; action: 'include' | 'exclude'; pattern: string; reason: string | null; matchedFiles: string[]; widerPattern: boolean; }
export interface ScopeRulesResponse { schema: string; rules: ScopeRuleView[]; }
export interface ScopeRuleMutation { action: 'include' | 'exclude'; pattern: string; reason?: string | null; confirmExpansion?: boolean; }
export type CoverageState = 'current' | 'stale' | 'unknown';
export interface CoverageFact {
  state: CoverageState;
  coveredLines: number;
  totalLines: number;
  coveredBranches: number;
  totalBranches: number;
  linePercent: number | null;
  branchPercent: number | null;
  commit: string | null;
  measuredAt: string | null;
  filesWithData: number;
  uncoveredLines?: number[] | null;
  uncoveredBranchLines?: number[] | null;
}
export interface TreeNode {
  id: string;
  name: string;
  level: string;
  path: string;
  kinds: Record<string, KindState>;
  findingsCount?: number;
  findingCounts?: FindingStateCounts;
  reviewedAt?: string | null;
  sizeBytes?: number | null;
  lineCount?: number | null;
  coverage?: CoverageFact;
  excluded?: ScopeExclusion[];
  children: TreeNode[];
}
export type ReviewKind = 'code' | 'security' | 'performance';
export type FindingSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export type FindingState = 'open' | 'accepted' | 'waived' | 'false-positive' | 'resolved';
export interface FindingStateCounts { open: number; accepted: number; waived: number; falsePositive: number; resolved: number; }
export interface FindingPosition { line: number; column: number; }
export interface FindingLocation { path: string; range?: { start: FindingPosition; end: FindingPosition }; }
export interface FindingSource { kind: 'deterministic'; sensorId: string; producer: string; producerVersion?: string; runIndex?: number; }
export interface ReviewFinding { id: string; aspect: string; severity: FindingSeverity; title: string; description: string; recommendation: string; evidence?: string; fingerprint?: string; ruleId: string; source?: FindingSource; accepted?: boolean; state?: FindingState; stateAuthor?: string; stateReason?: string; stateTimestamp?: string; stateExpiresAt?: string; locations: FindingLocation[]; }
export type ThreadStatus = 'open' | 'resolved';
export type AnchorState = 'anchored' | 'healed' | 'detached';
export interface ReviewThreadAuthor { kind: 'agent' | 'human'; agent?: string; model?: string; name?: string; }
export interface ReviewThreadEntry { id: string; author: ReviewThreadAuthor; createdAt: string; body: string; replyTo?: string; }
export interface ReviewThread { id: string; anchor: { path: string; fingerprint: string; contextHash: string; lastKnownRange: { start: FindingPosition; end: FindingPosition } }; status: ThreadStatus; anchorState?: AnchorState; healedAt?: string; entries: ReviewThreadEntry[]; }
export interface TokenUsage { inputTokens: number | null; outputTokens: number | null; cachedInputTokens: number | null; reasoningOutputTokens: number | null; durationMs: number; }
export interface ReviewGrade { score: number; band: string; rationale: string; }
export interface ReviewAspect { id: string; title: string; grade: ReviewGrade; }
export interface ReviewSensorReference { id: string; version: string; resultHash: string; }
export interface SecuritySensorMetadata extends ReviewSensorReference { available: boolean; unavailableReason: string | null; verdict: SecurityVerdict; toolVersions: Record<string, string>; }
export interface SecurityReviewMetadata { verdict: SecurityVerdict; combinationRule: string; sensors: SecuritySensorMetadata[]; }
export interface SensorProvenance { sensorId: string; sensorVersion: string; scope: string; target: string; scannedAt: string; toolVersions: Record<string, string>; }
export interface DeterministicSensorResult { available: boolean; unavailableReason: string | null; findings: ReviewFinding[]; provenance: SensorProvenance; }
export interface ReviewMetaDocument { reviewedAt: string; kind: ReviewKind; reviewer: { agent: string; model: string; runId?: string; usage?: TokenUsage & { cliType: string }; sensors?: ReviewSensorReference[] }; grade: ReviewGrade; summary: string; aspects?: ReviewAspect[]; findings: ReviewFinding[]; deterministicEvidence?: DeterministicSensorResult[]; findingCounts?: FindingStateCounts; threads?: ReviewThread[]; security?: SecurityReviewMetadata; }
export interface ThreadMutationRequest { path: string; kind: ReviewKind; threadId?: string; body?: string; replyTo?: string; status?: ThreadStatus; humanName?: string; line?: number; findingFingerprint?: string; }
export interface FindingStateMutationRequest { path: string; kind: ReviewKind; fingerprint: string; state: Exclude<FindingState, 'resolved'>; author: string; reason: string; expiresAt?: string | null; expectedTimestamp?: string | null; }
export type SecurityVerdict = 'pass' | 'warn' | 'block' | 'unavailable';
export interface SecurityScanProvenance { scanner: string; version: string; mode: string; range: string | null; configPath: string | null; baselinePath: string | null; scannedAt: string; }
export interface SecurityScanCounts { filesScanned: number; newFindings: number; acceptedFindings: number; blockFindings: number; warnFindings: number; cleanFiles: number; }
export interface SecurityScanFinding extends ReviewFinding { path: string; }
export interface SecurityScanResponse {
  verdict: SecurityVerdict;
  available: boolean;
  scanner: string;
  version: string;
  mode: string;
  range: string | null;
  configPath: string | null;
  baselinePath: string | null;
  scannedAt: string;
  filesScanned: number;
  newFindings: number;
  acceptedFindings: number;
  blockFindings: number;
  warnFindings: number;
  cleanFiles: number;
  unavailableReason: string | null;
  provenance: SecurityScanProvenance;
  counts: SecurityScanCounts;
  findings: SecurityScanFinding[];
}
export type AttackCoverageVerdict = 'pass' | 'finding' | 'notApplicable' | 'notYetChecked';
export type AttackCoverageStaleness = 'boundaryChanged' | 'codeChanged' | 'catalogueChanged' | 'promptChanged';
export interface AttackEvidence { kind: string; reference: string; summary: string; }
export interface AttackReviewerIdentity { agent: string; model: string; thinkingLevel: string; }
export interface AttackTokenCost { inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; totalTokens: number; }
export interface AttackObservation {
  schemaVersion: number; assessmentId: string; boundaryId: string; attackId: string;
  verdict: Exclude<AttackCoverageVerdict, 'notYetChecked'>; reasoning: string;
  evidence: AttackEvidence[]; deterministicSensorInput: string[];
  findingId: string | null; findingFingerprint: string | null; source: 'agent' | 'deterministicSensor' | 'human';
  reviewer: AttackReviewerIdentity; promptVersion: string; promptHash: string;
  catalogueVersion: string; catalogueEntryHash: string; boundaryDefinitionHash: string;
  coveredCodeHash: string; tokenCost: AttackTokenCost; checkedAt: string;
  commit: string | null; commitRange: string | null;
}
export interface AttackHistory {
  assessmentId: string; checkedAt: string; verdict: AttackCoverageVerdict; disagreement: boolean;
  judgements: AttackObservation[]; commit: string | null; commitRange: string | null;
}
export interface AttackCoverageCell {
  boundaryId: string; attackId: string; verdict: AttackCoverageVerdict; reason: string;
  evidence: AttackEvidence[]; findingId: string | null; findingFingerprint: string | null;
  disagreement: boolean; deterministicOverride: boolean; needsHumanAttention: boolean;
  requiredJudgements: number; independentJudgements: number; confidence: string;
  checkedAt: string | null; ageDays: number | null; stalenessReasons: AttackCoverageStaleness[];
  provenance: AttackObservation[]; history: AttackHistory[];
}
export interface AttackCatalogueEntry {
  id: string; version: string; title: string; description: string;
  applicability: { boundaryKinds: string[]; directions?: string[] | null };
  evidenceRequirements: string[]; severity: FindingSeverity; severityFrame: string;
  deterministicRuleIds: string[]; deterministicPassConclusive: boolean; enabled: boolean;
}
export interface AttackCoverageRow {
  boundary: { id: string; kind: string; direction: string; name: string; transport: string; location: { path: string; line: number } };
  boundaryDefinitionHash: string; coveredCodeHash: string; codeChangeCount: number;
  oldestVerdictAt: string | null; cells: AttackCoverageCell[];
}
export interface AttackCoverageMatrix {
  schemaVersion: number; catalogueVersion: string; promptVersion: string; promptHash: string;
  generatedAt: string; scope: string; attacks: AttackCatalogueEntry[]; rows: AttackCoverageRow[];
  cellCount: number; notYetCheckedCount: number; staleCount: number; disagreementCount: number;
}
export type LineEnding = 'lf' | 'crlf' | 'mixed';
export type FileEncoding = 'utf-8' | 'utf-8-bom' | 'other';
export interface FileDocument { path: string; content: string; metaDocuments: ReviewMetaDocument[]; sizeBytes: number; lineEnding: LineEnding; encoding: FileEncoding; coverage?: CoverageFact; }
export interface RiskRow { path: string; name: string; gradeScore: number | null; gradeBand: string | null; reviewState: ReviewState; coverage: CoverageFact; changes: number; riskScore: number | null; }
export interface RiskMatrixCell { grade: string; coverage: string; files: number; changes: number; }
export interface RiskReport { days: number; currentCommit: string | null; rows: RiskRow[]; matrix: RiskMatrixCell[]; }
export interface ScanFile { relativePath: string; state: ReviewState; reviewKind: string; metaRelativePath?: string | null; }
export interface ScanReport { files: ScanFile[]; freshCount: number; staleCount: number; policyDriftCount: number; missingCount: number; }
export interface HandoverRequest { findingSummary: string; filePath: string; findingText: string; reviewKind: string; metaReference: string; }
export interface HandoverResult { dryRun: boolean; taskId: string | null; card: { title: string }; }
export interface ResolvedInput { id: string; source: string; scope: 'global' | 'project'; priority: number; includedContent: string; content: string; truncated: boolean; }
export interface InputOmission { id: string; source: string; reason: string; omittedCharacters: number; }
export interface ResolvedInputs { kind: ReviewKind; level: string; budgetCharacters: number; includedCharacters: number; complete: boolean; inputs: ResolvedInput[]; omissions: InputOmission[]; }
export interface GuidelineDraft { id: string; enabled: boolean; priority: number; kinds: string[]; levels: string[]; content: string; }
export interface Guideline extends GuidelineDraft { fileName: string; }
export interface GuidelineCatalogueEntry { id: string; title: string; technology: string; description: string; guideline: GuidelineDraft; }
export interface GuidelineTraceFinding { id: string; ruleId: string; title: string; severity: FindingSeverity; kind: ReviewKind; unitPath: string; metaPath: string; }
export interface GuidelineTrace { guidelineId: string; findingsCount: number; findings: GuidelineTraceFinding[]; }
export interface ImpactFinding { id: string; ruleId: string; severity: FindingSeverity; title: string; path: string; line: number | null; }
export interface FileGuidelineImpact { path: string; before: ImpactFinding[]; after: ImpactFinding[]; added: ImpactFinding[]; removed: ImpactFinding[]; }
export interface GuidelineImpact { guidelineId: string; kind: ReviewKind; files: FileGuidelineImpact[]; addedCount: number; removedCount: number; changed: boolean; }
export type ApiConnectionState = 'connecting' | 'live' | 'preview' | 'offline';
export interface RepositoryRegistration {
  id: string;
  displayName: string;
  rootPath: string;
  globalInputsDirectory: string | null;
  inputBudgetCharacters: number;
  enabledReviewKinds: ReviewKind[];
  archived: boolean;
  defaultReviewTokenCap: number | null;
  defaultReviewCostCap: number | null;
}
export interface RepositoryRegistrationRequest {
  id?: string;
  displayName: string;
  rootPath: string;
  globalInputsDirectory: string | null;
  inputBudgetCharacters: number;
  enabledReviewKinds: ReviewKind[];
  defaultReviewTokenCap?: number | null;
  defaultReviewCostCap?: number | null;
}
export type AgentStudioImportStatus = 'imported' | 'skipped' | 'failed';
export interface AgentStudioImportResult {
  projectId: string;
  displayName: string;
  repositoryPath: string | null;
  status: AgentStudioImportStatus;
  repositoryId: string | null;
  reason: string | null;
}
export interface AgentStudioImportResponse { results: AgentStudioImportResult[]; imported: number; skipped: number; failed: number; }
export type ReviewRunState = 'queued' | 'running' | 'paused' | 'done' | 'failed' | 'cancelled' | 'capped';
export type ReviewUnitState = ReviewRunState | 'skipped' | 'skipped-fresh';
export type ModelCapabilityTier = 'light' | 'balanced' | 'frontier';
export type ModelRoutingStatus = 'selectable' | 'fallbackOnly' | 'unsupported' | 'restricted' | 'deprecated';
export interface ReviewModelOption {
  modelId: string; aliases: string[]; cliType: string; capabilityTier: ModelCapabilityTier; suitability: string;
  routingStatus: ModelRoutingStatus; supportedThinkingLevels: string[]; provisional: boolean; evidenceStatus: string;
  note: string; priceAvailable: boolean; availableForNewRuns: boolean;
}
export interface ReviewModelCatalog {
  schemaVersion: number; policyVersion: string; evidenceAsOfDate: string; sourceRepository: string; sourceCommit: string;
  thinkingLevels: string[]; models: ReviewModelOption[];
}
export interface ReviewFileProgress { path: string; state: ReviewUnitState; startedAt: string | null; finishedAt: string | null; error: string | null; }
export interface ReviewEstimate { files: number; operations: number; promptCharacters: number; inputTokens: number; outputTokens: number; cost: number | null; currency: string | null; priceStatus: string; historySamples: number; method: string; expectedFreshSkips: number; }
export interface ReviewEstimateDeviation { inputTokensPercent: number; outputTokensPercent: number; costPercent: number | null; note: string; }
export interface ReviewModelRecommendation {
  policyVersion: string; recommendedModel: string; recommendedThinkingLevel: string; capabilityTier: ModelCapabilityTier;
  score: number; correctnessFloor: string; reason: string; selectionSource: string;
}
export interface ReviewPreflight {
  repositoryId: string; path: string; level: string; kind: ReviewKind; model: string | null; thinkingLevel: string | null;
  cliType: string; estimate: ReviewEstimate; tokenCap: number | null; costCap: number | null;
  recommendation: ReviewModelRecommendation; overrideBelowFloor: boolean;
}
export interface ReviewRun {
  id: string; repositoryId: string; path: string; level: string; kind: ReviewKind; model: string | null; thinkingLevel: string | null; cliType: string;
  state: ReviewRunState; totalFiles: number; completedFiles: number; failedFiles: number; createdAt: string;
  startedAt: string | null; finishedAt: string | null; files: ReviewFileProgress[]; errors: string[]; usageOperations: number; usage: TokenUsage;
  estimate: ReviewEstimate | null; tokenCap: number | null; costCap: number | null; costSpent: number | null; currency: string | null;
  priceStatus: string; skippedFiles: number; aggregateState: ReviewUnitState | null; stopReason: string | null;
  deviation: ReviewEstimateDeviation | null; recommendation?: ReviewModelRecommendation | null; routeOverride?: boolean;
}
export interface StartReviewRequest { path: string; kind: ReviewKind; model?: string | null; cliType?: string | null; thinkingLevel?: string | null; tokenCap?: number | null; costCap?: number | null; force?: boolean; confirmBelowFloor?: boolean; }
export interface UsageAggregate { key: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; }
export interface UsageEntry { runId: string; reviewRunId?: string | null; timestamp: string; model: string; cliType: string; tokens: TokenUsage; kind: ReviewKind; level: string; path: string; schemaVersion: number; }
export interface UsageReport { generatedAt: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; byModel: UsageAggregate[]; byKind: UsageAggregate[]; byDay: UsageAggregate[]; byReviewRun: UsageAggregate[]; recent: UsageEntry[]; }
export interface QuotaWindow { label: string; usedPct: number | null; remainingPct: number | null; used: number | null; limit: number | null; unit: string | null; resetAt: string | null; resetLabel: string | null; }
export interface QuotaProvider { provider: string; plan: string | null; fetchedAt: string; source: string | null; error: string | null; windows: QuotaWindow[]; }
export interface QuotaReport { at: string; ttlSeconds: number; providers: QuotaProvider[]; }
export interface ProjectGrade { kind: ReviewKind; state: ReviewState; score: number | null; band: string | null; path: string; }
export interface ProjectFindings { open: number; bySeverity: Record<FindingSeverity, number>; byReviewState: Record<'fresh' | 'stale', number>; path: string; }
export interface ProjectStaleness { fresh: number; stale: number; missing: number; total: number; path: string; }
export interface ProjectReviewCoverage { reviewedFiles: number; totalFiles: number; percent: number; path: string; }
export interface ProjectTestCoverage { status: 'reported' | 'invalid' | 'unavailable'; linePercent: number | null; coveredLines: number | null; totalLines: number | null; source: string | null; path: string; }
export interface ProjectLanguageMetric { language: string; files: number; lines: number; bytes: number; path: string; }
export interface ProjectDistributionBucket { label: string; count: number; }
export interface ProjectDuplicationCandidate { fingerprint: string; lines: number; bytes: number; paths: string[]; }
export interface ProjectDependencyEdge { source: string; sourcePath: string; target: string; targetPath: string; kind: string; }
export interface ProjectStructuralMetrics {
  fileCount: number; folderCount: number; bytes: number; lines: number;
  languages: ProjectLanguageMetric[];
  fileSizeDistribution: ProjectDistributionBucket[];
  folderSizeDistribution: ProjectDistributionBucket[];
  duplicationCandidates: ProjectDuplicationCandidate[];
  dependencyEdges: ProjectDependencyEdge[];
}
export interface ProjectHotspot { path: string; churn: number; grade: number | null; findings: number; findingsPerKloc: number; risk: number; }
export interface ProjectDashboard {
  generatedAt: string;
  grades: ProjectGrade[];
  findings: ProjectFindings;
  staleness: ProjectStaleness;
  reviewCoverage: ProjectReviewCoverage;
  testCoverage: ProjectTestCoverage;
  metrics: ProjectStructuralMetrics;
  hotspots: ProjectHotspot[];
}

export interface RepositoryTransition {
  repositoryId: string;
  hasSnapshot: boolean;
}

const emptyUsageReport = (): UsageReport => ({ generatedAt: '', runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] });
const unknownCoverage = (): CoverageFact => ({ state: 'unknown', coveredLines: 0, totalLines: 0, coveredBranches: 0, totalBranches: 0, linePercent: null, branchPercent: null, commit: null, measuredAt: null, filesWithData: 0 });

const demoFile = `using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<RepositoryAccess>();

var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/api/tree", (RepositoryAccess repository) =>
{
    var stopwatch = Stopwatch.StartNew();
    var projects = RepositoryHierarchyBuilder.BuildDotNet(repository.Root);
    return Results.Ok(projects);
});

app.MapGet("/api/file", async (string path) =>
{
    var content = await File.ReadAllTextAsync(path);
    return Results.Ok(content);
});

app.Run();`;
const demoFileSizeBytes = new TextEncoder().encode(demoFile).length;

const state = (overall: ReviewState, score: number | null, band: string | null): KindState => ({ direct: overall, descendants: overall, overall, score, band, metaPath: score === null ? null : 'preview.review-meta.json' });
const kind = (code: ReviewState): Record<string, KindState> => ({
  code: state(code, code === 'fresh' ? 91 : code === 'stale' ? 72 : null, code === 'fresh' ? 'A' : code === 'stale' ? 'C' : null),
  security: state(code === 'fresh' ? 'fresh' : 'missing', code === 'fresh' ? 86 : null, code === 'fresh' ? 'B' : null),
  performance: state(code === 'missing' ? 'missing' : 'stale', code === 'missing' ? null : 72, code === 'missing' ? null : 'C'),
});
const demoTree: TreeNode[] = [{ id: 'quality-studio', name: 'Quality Studio', level: 'repository', path: '.', kinds: kind('stale'), children: [
  { id: 'src', name: 'src', level: 'folder', path: 'src', kinds: kind('stale'), children: [
    { id: 'api', name: 'QualityStudio.Api', level: 'project', path: 'src/QualityStudio.Api', kinds: kind('fresh'), children: [
      { id: 'program', name: 'Program.cs', level: 'file', path: 'src/QualityStudio.Api/Program.cs', kinds: kind('fresh'), children: [] },
      { id: 'contracts', name: 'ApiContracts.cs', level: 'file', path: 'src/QualityStudio.Api/ApiContracts.cs', kinds: kind('stale'), children: [] },
      { id: 'settings', name: 'appsettings.json', level: 'file', path: 'src/QualityStudio.Api/appsettings.json', kinds: kind('missing'), children: [] },
    ]},
    { id: 'core', name: 'AgentOrchestrator.CodeQuality', level: 'project', path: 'src/AgentOrchestrator.CodeQuality', kinds: kind('stale'), children: [
      { id: 'runner', name: 'ReviewRunner.cs', level: 'file', path: 'src/AgentOrchestrator.CodeQuality/ReviewRunner.cs', kinds: kind('stale'), children: [] },
      { id: 'state', name: 'ReviewState.cs', level: 'file', path: 'src/AgentOrchestrator.CodeQuality/ReviewState.cs', kinds: kind('fresh'), children: [] },
    ]},
  ]},
  { id: 'tests', name: 'tests', level: 'folder', path: 'tests', kinds: kind('missing'), children: [] },
  { id: 'docs', name: 'docs', level: 'folder', path: 'docs', kinds: kind('fresh'), children: [] },
]}];

const demoMeta: ReviewMetaDocument[] = [
  { reviewedAt: '2026-07-11T16:20:00.000Z', kind: 'code', reviewer: { agent: 'quality-reviewer', model: 'gpt-5' }, grade: { score: 91, band: 'A', rationale: 'Clear request boundaries and consistent error handling.' }, summary: 'The API entry point is compact and readable. One low-risk diagnostic gap remains.', findings: [{ id: 'route-timing', ruleId: 'dotnet-api-safety', aspect: 'observability', severity: 'low', title: 'File route has no timing event', description: 'The user-visible file read is not timed, making slow repository access difficult to diagnose.', recommendation: 'Record a structured duration for the file-read path.', evidence: 'The route awaits File.ReadAllTextAsync and returns without a timing log.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 17, column: 1 }, end: { line: 21, column: 3 } } }] }] },
  { reviewedAt: '2026-07-09T10:05:00.000Z', kind: 'performance', reviewer: { agent: 'perf-reviewer', model: 'gpt-5' }, grade: { score: 72, band: 'C', rationale: 'Repository hierarchy work is repeated on the request path.' }, summary: 'The endpoint is correct, but the stored review predates the current file and should be rerun.', findings: [{ id: 'rebuild-tree', ruleId: 'built-in:performance', aspect: 'request-path', severity: 'high', title: 'Hierarchy rebuilt for every request', description: 'A full project hierarchy build runs synchronously whenever the tree endpoint is requested.', recommendation: 'Cache the derived hierarchy and invalidate it from repository scan events.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 10, column: 1 }, end: { line: 15, column: 3 } } }] }] },
  {
    reviewedAt: '2026-07-25T13:40:00.000Z',
    kind: 'security',
    reviewer: {
      agent: 'security-reviewer',
      model: 'gpt-5',
      sensors: [{ id: 'gitleaks', version: '8.24.2', resultHash: `sha256:${'a'.repeat(64)}` }],
    },
    grade: { score: 59, band: 'F', rationale: 'Machine sensors reported blocking security evidence. Agent judgement: request boundaries are otherwise constrained.' },
    summary: 'Machine sensors reported blocking security evidence. One planted credential must be removed and rotated.',
    aspects: [
      { id: 'secrets', title: 'Secrets', grade: { score: 59, band: 'F', rationale: 'A high-confidence secret was detected.' } },
      { id: 'authentication-authorization', title: 'Authentication / authorization', grade: { score: 86, band: 'B', rationale: 'Repository access is constrained.' } },
    ],
    security: {
      verdict: 'block',
      combinationRule: 'security-sensor-agent-v1',
      sensors: [{
        id: 'gitleaks',
        version: '8.24.2',
        resultHash: `sha256:${'a'.repeat(64)}`,
        available: true,
        unavailableReason: null,
        verdict: 'block',
        toolVersions: { gitleaks: '8.24.2' },
      }],
    },
    findingCounts: { open: 1, accepted: 0, waived: 0, falsePositive: 0, resolved: 0 },
    findings: [{
      id: 'gitleaks-secret-demo',
      ruleId: 'generic-api-key',
      aspect: 'secrets',
      severity: 'high',
      title: 'Hard-coded API token',
      description: 'Gitleaks detected a high-confidence credential in the reviewed unit.',
      recommendation: 'Revoke the credential, remove it from history, and load the replacement from a secret store.',
      fingerprint: `sha256:${'b'.repeat(64)}`,
      evidence: JSON.stringify({ source: 'machine-sensor', sensorId: 'gitleaks', sensorVersion: '8.24.2', resultHash: `sha256:${'a'.repeat(64)}`, fact: null }, null, 2),
      locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 6, column: 1 }, end: { line: 6, column: 38 } } }],
    }],
  },
];

@Injectable({ providedIn: 'root' })
export class QualityApi {
  private readonly http = inject(HttpClient);
  private legacyApi = false;
  readonly tree = signal<TreeNode[]>(demoTree);
  readonly file = signal<FileDocument | null>(null);
  readonly scan = signal<ScanReport>({ files: [], freshCount: 8, staleCount: 4, policyDriftCount: 0, missingCount: 3 });
  readonly security = signal<SecurityScanResponse | null>(null);
  readonly attackCoverage = signal<AttackCoverageMatrix | null>(null);
  readonly attackCoverageLoading = signal(false);
  readonly attackCoverageError = signal('');
  readonly risk = signal<RiskReport>({ days: 90, currentCommit: null, rows: [], matrix: [] });
  readonly project = signal<ProjectDashboard | null>(null);
  readonly projectLoading = signal(true);
  readonly projectError = signal('');
  readonly repositoryTransition = signal<RepositoryTransition | null>(null);
  readonly connectionState = signal<ApiConnectionState>('connecting');
  readonly connected = computed(() => this.connectionState() === 'live');
  readonly connectionLabel = computed(() => {
    const state = this.connectionState();
    return state === 'live'
      ? 'Repository connected'
      : state === 'preview'
        ? 'API offline, preview data'
        : state === 'offline'
          ? 'API offline'
          : 'Connecting to API';
  });
  readonly loading = signal(false);
  readonly handoverConfigured = signal(false);
  readonly handoverDryRun = signal(true);
  readonly inputs = signal<Partial<Record<ReviewKind, ResolvedInputs>>>({});
  readonly guidelines = signal<Guideline[]>([]);
  readonly guidelineCatalogue = signal<GuidelineCatalogueEntry[]>([]);
  readonly guidelineTraces = signal<GuidelineTrace[]>([]);
  readonly repositories = signal<RepositoryRegistration[]>([]);
  readonly selectedRepositoryId = signal('default');
  readonly selectedRepository = computed(() => this.repositories().find(repository => repository.id === this.selectedRepositoryId()) ?? null);
  readonly modelCatalog = signal<ReviewModelCatalog>({ schemaVersion: 1, policyVersion: '', evidenceAsOfDate: '', sourceRepository: 'agent-orc/token-economy', sourceCommit: '', thinkingLevels: [], models: [] });
  readonly reviewRuns = signal<ReviewRun[]>([]);
  readonly scopeRules = signal<ScopeRulesResponse>({ schema: '', rules: [] });
  readonly usage = signal<UsageReport>(emptyUsageReport());
  readonly quotas = signal<QuotaReport>({ at: '', ttlSeconds: 0, providers: [] });
  readonly reviewError = signal('');
  readonly focusedThreadId = signal<string | null>(null);
  private readonly treeSnapshots = new Map<string, TreeNode[]>();
  private readonly projectSnapshots = new Map<string, ProjectDashboard>();
  private repositorySelectionSequence = 0;
  private reviewPollTimer: ReturnType<typeof setTimeout> | null = null;

  async loadRepositories(preferredId?: string | null): Promise<void> {
    try {
      const result = await firstValueFrom(this.http.get<{ repositories: RepositoryRegistration[]; defaultRepositoryId: string }>('/api/repos'));
      this.legacyApi = false;
      this.repositories.set(result.repositories);
      const selected = result.repositories.some(repository => repository.id === preferredId)
        ? preferredId!
        : result.repositories.some(repository => repository.id === this.selectedRepositoryId())
          ? this.selectedRepositoryId()
          : result.defaultRepositoryId;
      this.selectedRepositoryId.set(selected);
    } catch (error) {
      // A pre-registry server still exposes the legacy default endpoints.
      this.legacyApi = true;
      this.repositories.set([{ id: 'default', displayName: 'Default repository', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null }]);
      this.selectedRepositoryId.set('default');
      console.warn(JSON.stringify({ event: 'qs.repositories.legacy-fallback', reason: this.errorMessage(error) }));
    }
  }

  async selectRepository(id: string): Promise<void> {
    const started = performance.now();
    const sequence = ++this.repositorySelectionSequence;
    this.selectedRepositoryId.set(id);
    this.connectionState.set('connecting');
    this.file.set(null);
    this.attackCoverage.set(null);
    const treeSnapshot = this.treeSnapshots.get(id);
    const projectSnapshot = this.projectSnapshots.get(id);
    this.tree.set(treeSnapshot ?? []);
    this.project.set(projectSnapshot ?? null);
    this.repositoryTransition.set({ repositoryId: id, hasSnapshot: projectSnapshot !== undefined });
    this.usage.set(emptyUsageReport());
    await Promise.all([this.loadProjectDashboard(id), this.loadTree(id, false)]);
    if (sequence !== this.repositorySelectionSequence) return;
    const detailsLoading = Promise.all([
      this.loadRepositoryDetails(id),
      this.loadReviewRuns(id),
      this.loadUsage(undefined, undefined, id),
    ]);
    void detailsLoading.finally(() => {
      const remaining = Math.max(0, 250 - (performance.now() - started));
      setTimeout(() => {
        if (sequence === this.repositorySelectionSequence) this.repositoryTransition.set(null);
      }, remaining);
    });
    console.info(JSON.stringify({ event: 'qs.repository.selected', repositoryId: id }));
  }

  async createRepository(request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const created = await firstValueFrom(this.http.post<RepositoryRegistration>('/api/repos', request));
    await this.loadRepositories(created.id);
    return created;
  }

  async updateRepository(id: string, request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const updated = await firstValueFrom(this.http.put<RepositoryRegistration>(`/api/repos/${encodeURIComponent(id)}`, request));
    await this.loadRepositories(id);
    return updated;
  }

  async archiveRepository(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/repos/${encodeURIComponent(id)}`));
    await this.loadRepositories(id === this.selectedRepositoryId() ? null : this.selectedRepositoryId());
  }

  async importFromAgentStudio(): Promise<AgentStudioImportResponse> {
    const result = await firstValueFrom(this.http.post<AgentStudioImportResponse>('/api/repos/import-from-agent-studio', {}));
    console.info(JSON.stringify({ event: 'qs.repositories.agent-studio-import', imported: result.imported, skipped: result.skipped, failed: result.failed }));
    await this.loadRepositories(this.selectedRepositoryId());
    return result;
  }

  async loadTree(repositoryId = this.selectedRepositoryId(), waitForDetails = true): Promise<void> {
    const base = this.repositoryApiBase(repositoryId);
    const detailsLoading = waitForDetails ? this.loadRepositoryDetails(repositoryId) : null;
    try {
      const tree = await firstValueFrom(this.http.get<{ nodes: TreeNode[] }>(`${base}/tree?path=`));
      this.treeSnapshots.set(repositoryId, tree.nodes);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.tree.set(tree.nodes); this.connectionState.set('live');
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', nodeCount: tree.nodes.length, source: 'api' }));
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        this.connectionState.set('preview');
        console.warn(JSON.stringify({ event: 'qs.data.demo-fallback', reason: error instanceof Error ? error.message : 'API unavailable' }));
      }
    }
    if (detailsLoading) await detailsLoading;
  }

  async loadAttackCoverage(scope = 'src/QualityStudio.Api'): Promise<AttackCoverageMatrix> {
    this.attackCoverageLoading.set(true);
    this.attackCoverageError.set('');
    try {
      const matrix = await firstValueFrom(this.http.get<AttackCoverageMatrix>(
        `${this.repositoryApiBase()}/security/attack-coverage`, { params: { path: scope } }));
      this.attackCoverage.set(matrix);
      console.info(JSON.stringify({ event: 'qs.security.attack-coverage-loaded', scope, cells: matrix.cellCount, stale: matrix.staleCount, deferred: matrix.notYetCheckedCount, disagreements: matrix.disagreementCount }));
      return matrix;
    } catch (error) {
      this.attackCoverageError.set(this.errorMessage(error));
      throw error;
    } finally {
      this.attackCoverageLoading.set(false);
    }
  }

  async startReview(request: StartReviewRequest): Promise<ReviewRun> {
    this.reviewError.set('');
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review`, request));
      this.reviewRuns.update(runs => [run, ...runs.filter(candidate => candidate.id !== run.id)]);
      this.scheduleReviewPoll();
      console.info(JSON.stringify({ event: 'qs.review.queued', runId: run.id, path: run.path, kind: run.kind, fileCount: run.totalFiles }));
      return run;
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
      throw error;
    }
  }

  async loadModelCatalog(): Promise<void> {
    try {
      this.modelCatalog.set(await firstValueFrom(this.http.get<ReviewModelCatalog>('/api/models')));
    } catch (error) {
      console.warn(JSON.stringify({ event: 'qs.models.unavailable', reason: this.errorMessage(error) }));
    }
  }

  async estimateReview(request: StartReviewRequest): Promise<ReviewPreflight> {
    this.reviewError.set('');
    try {
      return await firstValueFrom(this.http.post<ReviewPreflight>(`${this.repositoryApiBase()}/review/estimate`, request));
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
      throw error;
    }
  }

  async loadReviewRuns(repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (!this.connected() || repositoryId !== this.selectedRepositoryId()) return;
    try {
      const before = new Map(this.reviewRuns().map(run => [run.id, run.state]));
      const result = await firstValueFrom(this.http.get<{ runs: ReviewRun[] }>(`${this.repositoryApiBase(repositoryId)}/review/runs`));
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.reviewRuns.set(result.runs);
      const completed = result.runs.some(run => ['done', 'failed', 'cancelled', 'capped'].includes(run.state) && ['queued', 'running'].includes(before.get(run.id) ?? ''));
      if (completed) {
        const openPath = this.file()?.path;
        await this.loadTree();
        void this.loadProjectDashboard();
        if (openPath) await this.loadFile(openPath);
        await Promise.all([this.loadUsage(), this.loadQuotas()]);
      }
      if (result.runs.some(run => run.state === 'queued' || run.state === 'running')) this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async loadUsage(since?: string, kind?: ReviewKind, repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (!this.connected() || repositoryId !== this.selectedRepositoryId()) return;
    try {
      const params: Record<string, string> = {};
      if (since) params['since'] = since;
      if (kind) params['kind'] = kind;
      const usage = await firstValueFrom(this.http.get<UsageReport>(`${this.repositoryApiBase(repositoryId)}/usage`, { params }));
      if (repositoryId === this.selectedRepositoryId()) this.usage.set(usage);
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        this.usage.set(emptyUsageReport());
        console.warn(JSON.stringify({ event: 'qs.usage.unavailable', reason: this.errorMessage(error) }));
      }
    }
  }

  async loadQuotas(): Promise<void> {
    try {
      this.quotas.set(await firstValueFrom(this.http.get<QuotaReport>('/api/quotas')));
    } catch (error) {
      this.quotas.set({ at: new Date().toISOString(), ttlSeconds: 0, providers: [] });
      console.warn(JSON.stringify({ event: 'qs.quotas.unavailable', reason: this.errorMessage(error) }));
    }
  }

  async cancelReview(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.delete<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}`));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
      const openPath = this.file()?.path;
      await this.loadTree();
      void this.loadProjectDashboard();
      if (openPath) await this.loadFile(openPath);
      this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async pauseReview(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pause`, {}));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async resumeReview(id: string, cap: { tokenCap?: number | null; costCap?: number | null } = {}): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/resume`, cap));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
      this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  private scheduleReviewPoll(): void {
    if (this.reviewPollTimer !== null) return;
    this.reviewPollTimer = setTimeout(() => {
      this.reviewPollTimer = null;
      void this.loadReviewRuns();
    }, 1500);
  }

  async loadFile(path: string): Promise<void> {
    this.loading.set(true);
    try {
      const file = await firstValueFrom(this.http.get<FileDocument>(`${this.repositoryApiBase()}/file`, { params: { path } }));
      this.file.set(file); this.connectionState.set('live');
    } catch (error) {
      this.file.set({ path, content: demoFile, metaDocuments: demoMeta, sizeBytes: demoFileSizeBytes, lineEnding: 'lf', encoding: 'utf-8', coverage: unknownCoverage() });
      if (this.connectionState() !== 'live') this.connectionState.set('preview');
      console.warn(JSON.stringify({ event: 'qs.data.file-demo-fallback', path, reason: error instanceof Error ? error.message : 'API unavailable' }));
    } finally { this.loading.set(false); }
  }

  clearFile(): void { this.file.set(null); }

  async loadProjectDashboard(repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (repositoryId === this.selectedRepositoryId()) {
      this.projectLoading.set(true);
      this.projectError.set('');
    }
    const start = performance.now();
    try {
      const project = await firstValueFrom(this.http.get<ProjectDashboard>(`${this.repositoryApiBase(repositoryId)}/project`));
      this.projectSnapshots.set(repositoryId, project);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.project.set(project);
      requestAnimationFrame(() => {
        if (repositoryId !== this.selectedRepositoryId()) return;
        const duration = performance.now() - start;
        performance.measure('qs.project.first-interactive', { start, end: performance.now(), detail: { budget: 150, repositoryId } });
        console.info(JSON.stringify({ event: 'qs.project.first-interactive', repositoryId, durationMs: +duration.toFixed(2), budgetMs: 150, withinBudget: duration < 150 }));
      });
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        if (!this.projectSnapshots.has(repositoryId)) this.project.set(null);
        this.projectError.set(this.errorMessage(error));
        console.warn(JSON.stringify({ event: 'qs.project.unavailable', repositoryId, reason: this.errorMessage(error) }));
      }
    } finally {
      if (repositoryId === this.selectedRepositoryId()) this.projectLoading.set(false);
    }
  }

  async createTask(request: HandoverRequest): Promise<HandoverResult> {
    return firstValueFrom(this.http.post<HandoverResult>(`${this.repositoryApiBase()}/handover`, request));
  }

  async mutateThread(request: ThreadMutationRequest): Promise<ReviewThread> {
    const thread = await firstValueFrom(this.http.post<ReviewThread>(`${this.repositoryApiBase()}/threads`, request));
    await this.loadFile(request.path);
    console.info(JSON.stringify({ event: 'qs.thread.mutated', threadId: thread.id, path: request.path, status: thread.status, hasEntry: !!request.body }));
    return thread;
  }

  async mutateFindingState(request: FindingStateMutationRequest): Promise<ReviewFinding | null> {
    await firstValueFrom(this.http.post(`${this.repositoryApiBase()}/findings/state`, request));
    await Promise.all([this.loadFile(request.path), this.loadTree()]);
    console.info(JSON.stringify({ event: 'qs.finding.state-mutated', fingerprint: request.fingerprint, path: request.path, state: request.state }));
    return this.file()?.metaDocuments.find(meta => meta.kind === request.kind)?.findings
      .find(finding => finding.fingerprint === request.fingerprint) ?? null;
  }

  async loadScopeRules(): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.get<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules`));
    this.scopeRules.set(response);
    return response;
  }

  previewScopeRule(request: ScopeRuleMutation): Promise<ScopeRuleView> {
    return firstValueFrom(this.http.post<ScopeRuleView>(`${this.repositoryApiBase()}/scope/rules/preview`, request));
  }

  async addScopeRule(request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.post<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules`, request));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async updateScopeRule(index: number, request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.put<ScopeRulesResponse>(
      `${this.repositoryApiBase()}/scope/rules/${index}`, request));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async deleteScopeRule(index: number): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.delete<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules/${index}`));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async createGuideline(draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.repositoryApiBase()}/guidelines`, draft));
    await this.loadTree();
    return guideline;
  }

  async updateGuideline(existingId: string, draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.put<Guideline>(`${this.repositoryApiBase()}/guidelines/${encodeURIComponent(existingId)}`, draft));
    await this.loadTree();
    return guideline;
  }

  async deleteGuideline(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`${this.repositoryApiBase()}/guidelines/${encodeURIComponent(id)}`));
    await this.loadTree();
  }

  async installGuideline(catalogueId: string): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.repositoryApiBase()}/guidelines/catalog/${encodeURIComponent(catalogueId)}/install`, {}));
    await this.loadTree();
    return guideline;
  }

  async guidelineImpact(guideline: GuidelineDraft, samplePaths: string[], kind: ReviewKind): Promise<GuidelineImpact> {
    return firstValueFrom(this.http.post<GuidelineImpact>(`${this.repositoryApiBase()}/guidelines/impact`, { guideline, samplePaths, kind }));
  }

  private async loadRepositoryDetails(repositoryId: string): Promise<void> {
    const base = this.repositoryApiBase(repositoryId);
    try {
      const [scan, inputs, guidelines, risk] = await Promise.all([
        firstValueFrom(this.http.get<ScanReport>(`${base}/scan`)),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>(`${base}/inputs`)),
        firstValueFrom(this.http.get<{ guidelines: Guideline[]; catalogue: GuidelineCatalogueEntry[]; traces: GuidelineTrace[] }>(`${base}/guidelines`)),
        firstValueFrom(this.http.get<RiskReport>(`${base}/risk?days=90`)),
      ]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.scan.set(scan); this.inputs.set(inputs.kinds);
      this.guidelines.set(guidelines.guidelines); this.guidelineCatalogue.set(guidelines.catalogue); this.guidelineTraces.set(guidelines.traces);
      this.risk.set(risk);
      await this.loadHandoverConfiguration();
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        console.warn(JSON.stringify({ event: 'qs.repository.details-unavailable', repositoryId, reason: this.errorMessage(error) }));
      }
    }
  }

  private async loadHandoverConfiguration(): Promise<void> {
    try {
      const configuration = await firstValueFrom(this.http.get<{ targetConfigured: boolean; dryRun: boolean }>(`${this.repositoryApiBase()}/handover`));
      this.handoverConfigured.set(configuration.targetConfigured);
      this.handoverDryRun.set(configuration.dryRun);
    } catch {
      this.handoverConfigured.set(false);
    }
  }

  errorMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      return error.error?.detail || error.error?.title || error.message;
    }
    return error instanceof Error ? error.message : 'The repository request failed.';
  }

  private repositoryApiBase(repositoryId = this.selectedRepositoryId()): string {
    return this.legacyApi ? '/api' : `/api/repos/${encodeURIComponent(repositoryId)}`;
  }
}
