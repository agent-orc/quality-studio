import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ReviewState = 'fresh' | 'stale' | 'missing';
export interface KindState { direct: ReviewState; descendants: ReviewState; overall: ReviewState; score: number | null; band: string | null; metaPath: string | null; }
export interface TreeNode { id: string; name: string; level: string; path: string; kinds: Record<string, KindState>; children: TreeNode[]; }
export type ReviewKind = 'code' | 'security' | 'performance';
export type FindingSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export interface FindingPosition { line: number; column: number; }
export interface FindingLocation { path: string; range?: { start: FindingPosition; end: FindingPosition }; }
export interface ReviewFinding { id: string; aspect: string; severity: FindingSeverity; title: string; description: string; recommendation: string; evidence?: string; fingerprint?: string; ruleId?: string; accepted?: boolean; locations: FindingLocation[]; }
export interface ReviewMetaDocument { reviewedAt: string; kind: ReviewKind; reviewer: { agent: string; model: string }; grade: { score: number; band: string; rationale: string }; summary: string; findings: ReviewFinding[]; }
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
export interface FileDocument { path: string; content: string; metaDocuments: ReviewMetaDocument[]; }
export interface ScanReport { files: unknown[]; freshCount: number; staleCount: number; missingCount: number; }
export interface HandoverRequest { findingSummary: string; filePath: string; findingText: string; reviewKind: string; metaReference: string; }
export interface HandoverResult { dryRun: boolean; taskId: string | null; card: { title: string }; }
export interface ResolvedInput { id: string; source: string; scope: 'global' | 'project'; priority: number; includedContent: string; content: string; truncated: boolean; }
export interface InputOmission { id: string; source: string; reason: string; omittedCharacters: number; }
export interface ResolvedInputs { kind: ReviewKind; level: string; budgetCharacters: number; includedCharacters: number; complete: boolean; inputs: ResolvedInput[]; omissions: InputOmission[]; }

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
  { reviewedAt: '2026-07-11T16:20:00.000Z', kind: 'code', reviewer: { agent: 'quality-reviewer', model: 'gpt-5' }, grade: { score: 91, band: 'A', rationale: 'Clear request boundaries and consistent error handling.' }, summary: 'The API entry point is compact and readable. One low-risk diagnostic gap remains.', findings: [{ id: 'route-timing', aspect: 'observability', severity: 'low', title: 'File route has no timing event', description: 'The user-visible file read is not timed, making slow repository access difficult to diagnose.', recommendation: 'Record a structured duration for the file-read path.', evidence: 'The route awaits File.ReadAllTextAsync and returns without a timing log.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 17, column: 1 }, end: { line: 21, column: 3 } } }] }] },
  { reviewedAt: '2026-07-09T10:05:00.000Z', kind: 'performance', reviewer: { agent: 'perf-reviewer', model: 'gpt-5' }, grade: { score: 72, band: 'C', rationale: 'Repository hierarchy work is repeated on the request path.' }, summary: 'The endpoint is correct, but the stored review predates the current file and should be rerun.', findings: [{ id: 'rebuild-tree', aspect: 'request-path', severity: 'high', title: 'Hierarchy rebuilt for every request', description: 'A full project hierarchy build runs synchronously whenever the tree endpoint is requested.', recommendation: 'Cache the derived hierarchy and invalidate it from repository scan events.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 10, column: 1 }, end: { line: 15, column: 3 } } }] }] },
  { reviewedAt: '2026-07-10T13:40:00.000Z', kind: 'security', reviewer: { agent: 'gitleaks', model: '8.24.2' }, grade: { score: 86, band: 'B', rationale: 'Repository access is constrained by the API service.' }, summary: 'No exploitable issue was identified in this file.', findings: [] },
];

const demoSecurity: SecurityScanResponse = {
  verdict: 'pass',
  available: true,
  scanner: 'gitleaks',
  version: '8.24.2',
  mode: 'repository',
  range: null,
  configPath: null,
  baselinePath: null,
  scannedAt: '2026-07-11T16:20:00.000Z',
  filesScanned: 1,
  newFindings: 0,
  acceptedFindings: 0,
  blockFindings: 0,
  warnFindings: 0,
  cleanFiles: 1,
  unavailableReason: null,
  provenance: { scanner: 'gitleaks', version: '8.24.2', mode: 'repository', range: null, configPath: null, baselinePath: null, scannedAt: '2026-07-11T16:20:00.000Z' },
  counts: { filesScanned: 1, newFindings: 0, acceptedFindings: 0, blockFindings: 0, warnFindings: 0, cleanFiles: 1 },
  findings: [],
};

@Injectable({ providedIn: 'root' })
export class QualityApi {
  private readonly http = inject(HttpClient);
  readonly tree = signal<TreeNode[]>(demoTree);
  readonly file = signal<FileDocument | null>(null);
  readonly scan = signal<ScanReport>({ files: [], freshCount: 8, staleCount: 4, missingCount: 3 });
  readonly security = signal<SecurityScanResponse | null>(null);
  readonly connected = signal(false);
  readonly loading = signal(false);
  readonly handoverConfigured = signal(false);
  readonly handoverDryRun = signal(true);
  readonly inputs = signal<Partial<Record<ReviewKind, ResolvedInputs>>>({});

  async loadTree(): Promise<void> {
    try {
      const [tree, scan, security, inputs] = await Promise.all([
        firstValueFrom(this.http.get<{ nodes: TreeNode[] }>('/api/tree?path=')),
        firstValueFrom(this.http.get<ScanReport>('/api/scan')),
        firstValueFrom(this.http.get<SecurityScanResponse>('/api/security/scan')),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>('/api/inputs')),
      ]);
      this.tree.set(tree.nodes); this.scan.set(scan); this.security.set(security); this.inputs.set(inputs.kinds); this.connected.set(true);
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', nodeCount: tree.nodes.length, source: 'api' }));
    } catch (error) {
      this.security.set(demoSecurity);
      console.warn(JSON.stringify({ event: 'qs.data.demo-fallback', reason: error instanceof Error ? error.message : 'API unavailable' }));
    }
    await this.loadHandoverConfiguration();
  }

  async loadFile(path: string): Promise<void> {
    this.loading.set(true);
    try {
      const file = await firstValueFrom(this.http.get<FileDocument>('/api/file', { params: { path } }));
      this.file.set(file); this.connected.set(true);
    } catch (error) {
      this.file.set({ path, content: demoFile, metaDocuments: demoMeta });
      console.warn(JSON.stringify({ event: 'qs.data.file-demo-fallback', path, reason: error instanceof Error ? error.message : 'API unavailable' }));
    } finally { this.loading.set(false); }
  }

  async createTask(request: HandoverRequest): Promise<HandoverResult> {
    return firstValueFrom(this.http.post<HandoverResult>('/api/handover', request));
  }

  private async loadHandoverConfiguration(): Promise<void> {
    try {
      const configuration = await firstValueFrom(this.http.get<{ targetConfigured: boolean; dryRun: boolean }>('/api/handover'));
      this.handoverConfigured.set(configuration.targetConfigured);
      this.handoverDryRun.set(configuration.dryRun);
    } catch {
      this.handoverConfigured.set(false);
    }
  }
}
