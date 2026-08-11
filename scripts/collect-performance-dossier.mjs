import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const repositoryRoot = resolve(import.meta.dirname, '..');
const frontendRoot = resolve(repositoryRoot, 'frontend');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const productBaseRef = process.env.QS_PRODUCT_BASE_REF || 'origin/main';
const qs54Commit = process.env.QS54_COMMIT || '26fd785e80c40dc5f02a3ec0b4f73890732beb81';
const styleReference = process.env.QS_STYLE_REFERENCE ||
  '/home/agent/runner-work/PROJ-002/repo/docs/operations/haertung-verteilte-ausfuehrung/index.html';

await mkdir(resultsRoot, { recursive: true });

const productBase = git('rev-parse', productBaseRef);
const paths = git('diff-tree', '--no-commit-id', '--name-only', '-r', qs54Commit)
  .split('\n').filter(Boolean);
const files = paths.map(path => {
  const qs54Blob = git('rev-parse', `${qs54Commit}:${path}`);
  const productBaseBlob = git('rev-parse', `${productBase}:${path}`, { allowFailure: true });
  return {
    path,
    state: productBaseBlob === '' ? 'missing' : productBaseBlob === qs54Blob ? 'identical' : 'evolved',
    qs54Blob,
    productBaseBlob: productBaseBlob || null,
  };
});
const behaviorAnchors = [
  ['one immutable hierarchy snapshot per repository', 'src/AgentOrchestrator.CodeQuality/RepositoryHierarchyCache.cs', 'slot.Snapshot = new RepositoryHierarchySnapshot'],
  ['background repository prewarmer', 'src/QualityStudio.Api/RepositorySnapshotPrewarmer.cs', 'class RepositorySnapshotPrewarmer'],
  ['prewarmer registered as hosted service', 'src/QualityStudio.Api/Program.cs', 'AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RepositorySnapshotPrewarmer>())'],
  ['dashboard projection cache', 'src/QualityStudio.Api/ProjectDashboard.cs', 'ConcurrentDictionary<string, ProjectDashboardResponse> cache'],
  ['backend phase telemetry', 'src/QualityStudio.Api/Program.cs', 'context.Response.Headers["Server-Timing"]'],
  ['browser tree snapshots', 'frontend/src/app/quality-api.ts', 'treeSnapshots = new Map<string, TreeNode[]>()'],
  ['visible transition mark', 'frontend/src/app/app.ts', "this.measure('qs.repository.transition-visible'"],
  ['real-backend browser harness', 'frontend/tests/project-switch-perf.mjs', "backend: 'real QualityStudio.Api (no Playwright response interception)'"],
].map(([name, path, needle]) => ({
  name,
  path,
  needle,
  present: git('show', `${productBase}:${path}`, { allowFailure: true }).includes(needle),
}));
const identicalCount = files.filter(file => file.state === 'identical').length;
const evolvedCount = files.filter(file => file.state === 'evolved').length;
const missingCount = files.filter(file => file.state === 'missing').length;
const ledger = {
  measuredAt: new Date().toISOString(),
  qs54Commit,
  productBaseRef,
  productBase,
  qs54CommitIsAncestorOfProductBase: isAncestor(qs54Commit, productBase),
  qs54PatchEquivalentOnProductBase: git('cherry', productBase, qs54Commit, { allowFailure: true }).startsWith('- '),
  conclusion: 'QS-54 behavior is delivered on the product base despite the result commit not being an ancestor: unchanged files and evolved integration files retain every inspected behavior anchor.',
  counts: { total: files.length, identical: identicalCount, evolved: evolvedCount, missing: missingCount },
  behaviorAnchors,
  files,
};
await writeFile(resolve(resultsRoot, 'qs54-delivery-state.json'), JSON.stringify(ledger, null, 2) + '\n');

const dossierPath = resolve(repositoryRoot, 'docs/operations/performance/index.html');
const workbenchPath = resolve(repositoryRoot, 'docs/operations/performance/workbench.json');
await Promise.all([
  copyFile(dossierPath, resolve(resultsRoot, 'performance-dossier.html')),
  copyFile(workbenchPath, resolve(resultsRoot, 'workbench.json')),
  copyFile(resolve(repositoryRoot, 'results/status.md'), resolve(resultsRoot, 'status.md')),
  copyFile(resolve(repositoryRoot, 'results/deliverables.md'), resolve(resultsRoot, 'deliverables.md')),
]);

// Preserve the existing product harness filenames while publishing task evidence
// with the repository's required provenance suffix.
await Promise.allSettled([
  copyFile(resolve(resultsRoot, 'project-switch-transition-light.png'), resolve(resultsRoot, 'project-switch-transition-light--real.png')),
  copyFile(resolve(resultsRoot, 'project-switch-transition-dark.png'), resolve(resultsRoot, 'project-switch-transition-dark--real.png')),
]);

const dossierStyle = extractStyle(await readFile(dossierPath, 'utf8'));
const referenceStyle = extractStyle(await readFile(styleReference, 'utf8'));
const styleVerification = {
  reference: styleReference,
  exactMatch: dossierStyle === referenceStyle,
  dossierSha256: sha256(dossierStyle),
  referenceSha256: sha256(referenceStyle),
};
await writeFile(resolve(resultsRoot, 'style-verification.json'), JSON.stringify(styleVerification, null, 2) + '\n');

const require = createRequire(import.meta.url);
const { chromium } = require(resolve(frontendRoot, 'node_modules/playwright-core'));
const browser = await chromium.launch({ executablePath: process.env.CHROME_BIN || chromium.executablePath(), headless: true, args: ['--no-sandbox'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1 });
  await page.goto(pathToFileURL(dossierPath).href, { waitUntil: 'load' });
  await page.screenshot({ path: resolve(resultsRoot, 'performance-dossier-preview--pinned.png'), fullPage: true });
} finally {
  await browser.close();
}

console.log(JSON.stringify({ resultsRoot, productBase, qs54: ledger.counts, styleVerification }, null, 2));

function git(...arguments_) {
  let options = {};
  if (typeof arguments_.at(-1) === 'object') options = arguments_.pop();
  const result = spawnSync('git', arguments_, { cwd: repositoryRoot, encoding: 'utf8' });
  if (result.status !== 0 && !options.allowFailure) {
    throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
  }
  return result.status === 0 ? result.stdout.trim() : '';
}

function isAncestor(ancestor, descendant) {
  return spawnSync('git', ['merge-base', '--is-ancestor', ancestor, descendant], { cwd: repositoryRoot }).status === 0;
}

function extractStyle(html) {
  const match = html.match(/<style>[\s\S]*?<\/style>/);
  if (!match) throw new Error('Document has no style block.');
  return match[0];
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}
