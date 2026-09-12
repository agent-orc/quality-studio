/**
 * Wire contracts shared by the Quality Studio API services and the components that render them.
 * The file holds types only: it emits no runtime code, so importing it never pulls a service in.
 */
export type ReviewState = 'fresh' | 'stale' | 'policy-drift' | 'missing' | 'invalid';
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
  parentId?: string | null;
  /** The v2 level contract states this without shipping the descendants. */
  hasChildren?: boolean;
  childCount?: number;
  /** False while a container's children are still one request away. */
  childrenLoaded?: boolean;
  children: TreeNode[];
}
/** One level of the versioned lazy tree contract served at `/api/.../tree/v2`. */
export interface TreeLevelResponse {
  schemaVersion: 2;
  parentId: string | null;
  path: string;
  snapshotEtag?: string;
  offset: number;
  limit: number;
  nextCursor: string | null;
  nodes: TreeNode[];
}
export type ReviewKind = 'code' | 'security' | 'performance';
export type FindingSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export type FindingState = 'open' | 'accepted' | 'waived' | 'false-positive' | 'resolved';
export interface FindingStateCounts { open: number; accepted: number; waived: number; falsePositive: number; resolved: number; }
export interface FindingPosition { line: number; column: number; }
export interface FindingLocation { path: string; range?: { start: FindingPosition; end: FindingPosition }; }
export interface FindingSource { kind: 'deterministic'; sensorId: string; producer: string; producerVersion?: string; runIndex?: number; }
/** The active ignore-list rule muting a finding, projected onto the finding itself by the API. */
export interface FindingSuppression { id: string; reason: string; author: string; createdAt: string; expiresAt: string | null; }
export interface FindingSuppressionRule extends FindingSuppression { enabled: boolean; match: { fingerprint: string }; effect: 'suppress'; path: string | null; ruleId: string | null; title: string | null; }
export interface FindingSuppressionsResponse { schemaVersion: 1; revision: number; rules: FindingSuppressionRule[]; }
export interface FindingSuppressionMutation { path: string; kind: ReviewKind; fingerprint: string; author: string; reason: string; expiresAt?: string | null; expectedRevision?: number | null; }
export interface ReviewFinding { id: string; aspect: string; severity: FindingSeverity; title: string; description: string; recommendation: string; evidence?: string; fingerprint?: string; ruleId: string; source?: FindingSource; accepted?: boolean; state?: FindingState; stateAuthor?: string; stateReason?: string; stateTimestamp?: string; stateExpiresAt?: string; suppression?: FindingSuppression | null; locations: FindingLocation[]; }
export type ThreadStatus = 'open' | 'resolved';
export type AnchorState = 'anchored' | 'healed' | 'detached';
export interface ReviewThreadAuthor { kind: 'agent' | 'human'; agent?: string; model?: string; name?: string; }
export interface ReviewThreadEntry { id: string; author: ReviewThreadAuthor; createdAt: string; body: string; replyTo?: string; }
export interface ReviewThread { id: string; anchor: { path: string; fingerprint: string; contextHash: string; lastKnownRange: { start: FindingPosition; end: FindingPosition } }; status: ThreadStatus; anchorState?: AnchorState; healedAt?: string; entries: ReviewThreadEntry[]; }
export interface TokenUsage { inputTokens: number | null; outputTokens: number | null; cachedInputTokens: number | null; reasoningOutputTokens: number | null; durationMs: number; }
/** What a priced operation cost. `total` is null exactly when `status` says the price is unknown. */
export interface UsageCost { total: number | null; currency: string | null; status: string; }
/** How the run's model was chosen: named by the operator, resolved from policy, or left to the CLI. */
export type ReviewModelSource = 'explicit' | 'policy-default' | 'runner-default';
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
export interface ScanReport { files: ScanFile[]; freshCount: number; staleCount: number; policyDriftCount: number; missingCount: number; invalidCount: number; }
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
  recommendation: ReviewModelRecommendation; overrideBelowFloor: boolean; modelSource?: ReviewModelSource | null;
}
export interface ReviewRun {
  id: string; repositoryId: string; path: string; level: string; kind: ReviewKind; model: string | null; thinkingLevel: string | null; cliType: string;
  state: ReviewRunState; totalFiles: number; completedFiles: number; failedFiles: number; createdAt: string;
  startedAt: string | null; finishedAt: string | null; files: ReviewFileProgress[]; errors: string[]; usageOperations: number; usage: TokenUsage;
  estimate: ReviewEstimate | null; tokenCap: number | null; costCap: number | null; costSpent: number | null; currency: string | null;
  priceStatus: string; skippedFiles: number; aggregateState: ReviewUnitState | null; stopReason: string | null;
  deviation: ReviewEstimateDeviation | null; recommendation?: ReviewModelRecommendation | null; routeOverride?: boolean;
  modelSource?: ReviewModelSource | null;
}
export type RunReportFormat = 'html' | 'markdown' | 'sarif' | 'json';
export interface QualityRunFinding {
  id: string; ruleId: string; aspect: string; severity: FindingSeverity; state: FindingState;
  title: string; description: string; recommendation: string; evidence: string | null; fingerprint: string;
  locations: { path: string; startLine: number | null; startColumn: number | null; endLine: number | null; endColumn: number | null }[];
  source: 'agent' | 'deterministic'; sensorId: string | null; producer: string | null;
}
export interface QualityRunObservation {
  unitId: string; level: string; path: string; outcome: ReviewUnitState; producedByRun: boolean;
  sidecarPath: string | null; sidecarSha256: string | null; capturedAt: string | null;
  reviewedHash: string | null; providerRunId: string | null; grade: ReviewGrade | null; summary: string | null;
  findings: QualityRunFinding[];
}
export interface QualityRunEstimate {
  files: number; operations: number; inputTokens: number; outputTokens: number; cost: number | null;
  currency: string | null; historySamples: number; method: string;
}
export interface QualityRunReport {
  $schema: string; schemaVersion: number;
  run: { id: string; revision: number; repositoryId: string; repositoryName: string; kind: ReviewKind; scopeUnitId: string; level: string; path: string; state: ReviewRunState; completeness: 'complete' | 'partial'; createdAt: string; startedAt: string | null; finishedAt: string | null; model: string; thinkingLevel: string; cliType: string; force: boolean };
  subject: { manifestHash: string; targets: { unitId: string; name: string; path: string; subjectHash: string }[] };
  execution: { reviewed: number; reusedFresh: number; failed: number; skipped: number; cancelled: number; aggregateOutcome: ReviewUnitState | null; errors: string[]; usage: TokenUsage & { operations: number; cost: number | null; currency: string | null; priceStatus: string; inputEstimateDeviationPercent: number | null; outputEstimateDeviationPercent: number | null; costEstimateDeviationPercent: number | null }; cap: { tokenLimit: number | null; costLimit: number | null; outcome: string; reason: string | null }; estimate: QualityRunEstimate | null };
  observations: QualityRunObservation[];
  delta: { status: 'available' | 'unavailable'; priorRunId: string | null; reason: string | null; new: string[]; persisting: string[]; resolved: string[]; stateChanged: string[] };
  summary: { score: number | null; grade: string | null; findings: { total: number; bySeverity: Record<string, number>; byState: Record<string, number> }; highestSeverity: FindingSeverity | null; partialReason: string | null };
}
export interface QualityRunTrendPoint {
  runId: string; revision: number; finishedAt: string; state: ReviewRunState; completeness: 'complete' | 'partial';
  comparable: boolean; comparisonReason: string | null; score: number | null; grade: string | null;
  activeFindings: number; newFindings: number; persistingFindings: number; resolvedFindings: number; stateChangedFindings: number;
  reviewed: number; reusedFresh: number; failed: number; skipped: number; inputTokens: number | null; outputTokens: number | null;
  cost: number | null; currency: string | null;
}
export interface QualityRunTrendPage { points: QualityRunTrendPoint[]; nextCursor: string | null; }
export interface QualityRunComparisonFinding {
  fingerprint: string; severity: FindingSeverity; title: string; ruleId: string;
  baselineState: FindingState | null; candidateState: FindingState | null;
  locations: { path: string; startLine: number | null; startColumn: number | null; endLine: number | null; endColumn: number | null }[];
}
export interface QualityRunComparison {
  baselineRunId: string; candidateRunId: string;
  route: { compatible: boolean; differences: string[] };
  new: QualityRunComparisonFinding[]; unchanged: QualityRunComparisonFinding[];
  resolved: QualityRunComparisonFinding[]; dispositionChanged: QualityRunComparisonFinding[];
}
export interface ReviewRunCompareSnapshot { runId: string; status: 'found' | 'missing' | 'corrupt'; error: string | null; }
export interface ReviewRunCompareResult {
  status: 'available' | 'unavailable';
  baseline: ReviewRunCompareSnapshot; candidate: ReviewRunCompareSnapshot;
  comparison: QualityRunComparison | null;
}
export interface ReviewRunRetention { snapshotCount: number; totalBytes: number; averageBytes: number; pinnedCount: number; retentionKeep: number; }
export interface StartReviewRequest { path: string; kind: ReviewKind; model?: string | null; cliType?: string | null; thinkingLevel?: string | null; tokenCap?: number | null; costCap?: number | null; force?: boolean; confirmBelowFloor?: boolean; }
export interface UsageAggregate { key: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; }
export interface UsageEntry { runId: string; reviewRunId?: string | null; timestamp: string; model: string; cliType: string; tokens: TokenUsage; kind: ReviewKind; level: string; path: string; schemaVersion: number; modelSource?: ReviewModelSource | null; cost?: UsageCost | null; }
export interface UsageReport { generatedAt: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; byModel: UsageAggregate[]; byKind: UsageAggregate[]; byDay: UsageAggregate[]; byReviewRun: UsageAggregate[]; recent: UsageEntry[]; estimatedCost?: number | null; costCurrency?: string | null; unpricedRuns?: number; }
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

/**
 * Why the file endpoint could not deliver the requested document. The editor renders this instead
 * of substituting foreign content, so a failed lookup can never be mistaken for repository data.
 */
export type FileErrorKind = 'unauthorized' | 'forbidden' | 'out-of-scope' | 'too-large' | 'unavailable';
export interface FileError {
  path: string;
  kind: FileErrorKind;
  status: number | null;
  title: string;
  detail: string;
  retryable: boolean;
}
