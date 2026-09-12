// Functional end-to-end check of the QS-82 /api/tree/v2 contract against a small repository.
// Verifies: one-level shape, hasChildren, snapshot pinning, cursor paging, conditional ETag, Server-Timing.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(new URL('..', import.meta.url).pathname);
const targetRoot = resolve(process.env.QS_TARGET ?? repositoryRoot);
const apiDll = resolve(repositoryRoot, 'backend/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const checks = [];
const record = (name, pass, detail) => { checks.push({ name, pass, detail }); console.log(`${pass ? 'PASS' : 'FAIL'}  ${name} — ${detail}`); };

const port = await freePort();
const tempRoot = await mkdtemp(resolve(tmpdir(), 'qs-verify-'));
await mkdir(resolve(tempRoot, '.quality-studio'), { recursive: true });
await writeFile(resolve(tempRoot, '.quality-studio/repositories.json'), JSON.stringify([{
  id: 'default', displayName: basename(targetRoot), rootPath: targetRoot,
  globalInputsDirectory: null, inputBudgetCharacters: 12000,
  enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
  defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
}], null, 2));

const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', tempRoot], {
  cwd: tempRoot,
  env: { ...process.env, QualityStudio__RepositoryRoot: targetRoot,
    QualityStudio__AllowedRoots__0: resolve(targetRoot, '..'),
    QualityStudio__Security__Mode: 'Local' },
  stdio: ['ignore', 'pipe', 'pipe'],
});
const lines = [];
for (const stream of [child.stdout, child.stderr]) stream.on('data', d => lines.push(String(d)));

try {
  const base = `http://127.0.0.1:${port}`;
  await waitForHttp(`${base}/health`, 120_000);

  // 1. Root level is one level only, with rolled-up facts.
  const rootRes = await fetch(`${base}/api/tree/v2?limit=5`);
  const root = await rootRes.json();
  record('root returns schemaVersion 2', root.schemaVersion === 2, `schemaVersion=${root.schemaVersion}`);
  record('root nodes carry no inlined descendants',
    root.nodes.every(n => Array.isArray(n.children) && n.children.length === 0),
    `${root.nodes.length} nodes, all children arrays empty`);
  record('root exposes hasChildren', root.nodes.some(n => n.hasChildren === true),
    `${root.nodes.filter(n => n.hasChildren).length}/${root.nodes.length} expandable`);
  record('root exposes a snapshot ETag', typeof root.snapshotEtag === 'string' && root.snapshotEtag.length > 0,
    `snapshotEtag=${String(root.snapshotEtag).slice(0, 24)}…`);
  record('Server-Timing reports phases', /tree-snapshot|tree-projection|tree-serialization/.test(rootRes.headers.get('server-timing') ?? ''),
    rootRes.headers.get('server-timing') ?? '(absent)');

  // 2. Cursor paging.
  record('page honours limit and yields a cursor', root.nodes.length <= 5 && (root.nextCursor === null || typeof root.nextCursor === 'string'),
    `nodes=${root.nodes.length} nextCursor=${root.nextCursor}`);
  if (root.nextCursor) {
    const page2 = await (await fetch(`${base}/api/tree/v2?limit=5&cursor=${encodeURIComponent(root.nextCursor)}`)).json();
    const overlap = page2.nodes.filter(n => root.nodes.some(r => r.id === n.id));
    record('second page does not repeat the first', overlap.length === 0,
      `page2 offset=${page2.offset}, ${page2.nodes.length} nodes, ${overlap.length} overlapping`);
  }

  // 3. Snapshot-pinned child expansion.
  const expandable = root.nodes.find(n => n.hasChildren);
  if (expandable) {
    const childRes = await fetch(`${base}/api/tree/v2?limit=50&parentId=${encodeURIComponent(expandable.id)}&snapshot=${encodeURIComponent(root.snapshotEtag)}`);
    const childPage = await childRes.json();
    record('pinned child page resolves', childRes.status === 200 && childPage.parentId === expandable.id,
      `parentId=${childPage.parentId} nodes=${childPage.nodes.length}`);
    record('child page reuses the pinned snapshot', childPage.snapshotEtag === root.snapshotEtag,
      `child snapshotEtag matches root: ${childPage.snapshotEtag === root.snapshotEtag}`);
  }

  // 4. Conditional ETag → 304.
  const etag = rootRes.headers.get('etag');
  const conditional = await fetch(`${base}/api/tree/v2?limit=5`, { headers: { 'If-None-Match': etag } });
  record('conditional request returns 304', conditional.status === 304, `status=${conditional.status} etag=${String(etag).slice(0, 20)}…`);

  // 5. Unknown parent → 404, bad cursor → 400.
  record('unknown parentId returns 404', (await fetch(`${base}/api/tree/v2?parentId=does-not-exist`)).status === 404, '404 for missing node');
  record('malformed cursor is rejected', (await fetch(`${base}/api/tree/v2?cursor=bogus`)).status >= 400, 'non-2xx for invalid cursor');

  // 6. Server-side search keeps deep links working.
  const searchRes = await fetch(`${base}/api/tree/v2/search?query=Program&limit=10`);
  const search = await searchRes.json();
  record('server search returns matches', searchRes.status === 200 && Array.isArray(search.nodes),
    `status=${searchRes.status} matches=${search.nodes?.length}`);

  // 7. Payload size: v2 root vs legacy v1 recursive root.
  const v1 = await fetch(`${base}/api/tree?path=`);
  const v1Bytes = (await v1.arrayBuffer()).byteLength;
  const v2Full = await fetch(`${base}/api/tree/v2?limit=1000`);
  const v2Bytes = (await v2Full.arrayBuffer()).byteLength;
  record('v2 root payload is far smaller than v1', v2Bytes < v1Bytes / 10,
    `v1=${v1Bytes.toLocaleString()} bytes, v2=${v2Bytes.toLocaleString()} bytes, ${(100 - (v2Bytes / v1Bytes) * 100).toFixed(2)}% smaller`);

  const failed = checks.filter(c => !c.pass);
  console.log(`\n${checks.length - failed.length}/${checks.length} checks passed on ${targetRoot}`);
  process.exitCode = failed.length ? 1 : 0;
} finally {
  child.kill('SIGTERM');
  await Promise.race([new Promise(r => child.once('exit', r)), delay(5000)]);
  if (child.exitCode === null) child.kill('SIGKILL');
}

function freePort() {
  return new Promise((res, rej) => {
    const s = createServer();
    s.on('error', rej);
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => res(port)); });
  });
}
async function waitForHttp(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { const r = await fetch(url); if (r.ok) return; } catch { /* not up yet */ }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${url}\n${lines.join('')}`);
}
