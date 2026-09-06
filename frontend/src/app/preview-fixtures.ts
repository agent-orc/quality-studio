/**
 * Offline preview fixtures.
 *
 * These are demonstration values, not repository data. They are only served while the API is
 * unreachable (connection state `preview`), always behind a visible banner, and the module is
 * loaded through a dynamic import so none of it ships in the editor's first-content bundle.
 */
import { KindState, ReviewMetaDocument, ReviewState, TreeNode } from './contracts';

export interface PreviewFixtures {
  tree: TreeNode[];
  content: string;
  sizeBytes: number;
  metaDocuments: ReviewMetaDocument[];
}

const previewContent = `using System.Diagnostics;
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
const previewSizeBytes = new TextEncoder().encode(previewContent).length;

const state = (overall: ReviewState, score: number | null, band: string | null): KindState => ({ direct: overall, descendants: overall, overall, score, band, metaPath: score === null ? null : 'preview.review-meta.json' });
const kind = (code: ReviewState): Record<string, KindState> => ({
  code: state(code, code === 'fresh' ? 91 : code === 'stale' ? 72 : null, code === 'fresh' ? 'A' : code === 'stale' ? 'C' : null),
  security: state(code === 'fresh' ? 'fresh' : 'missing', code === 'fresh' ? 86 : null, code === 'fresh' ? 'B' : null),
  performance: state(code === 'missing' ? 'missing' : 'stale', code === 'missing' ? null : 72, code === 'missing' ? null : 'C'),
});
const previewTree: TreeNode[] = [{ id: 'quality-studio', name: 'Quality Studio', level: 'repository', path: '.', kinds: kind('stale'), children: [
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

const previewMeta: ReviewMetaDocument[] = [
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

export const previewFixtures: PreviewFixtures = {
  tree: previewTree,
  content: previewContent,
  sizeBytes: previewSizeBytes,
  metaDocuments: previewMeta,
};
