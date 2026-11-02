#!/usr/bin/env node
// Proves that the image's Git reads a read-only checkout owned by another uid (QSI-2).
//
//   node scripts/check-container-git.mjs [--build] [--image quality-studio:local]
//
// The checkout is created inside a Docker volume as root, so it belongs to uid 0 while the image
// runs as uid 10001 - the same mismatch as the runner checkouts mounted into the integrated stack.
// The volume is mounted read-only. Two facts are checked:
//   1. Plain `git` as the image user refuses the checkout ("dubious ownership"): the mismatch is real
//      and the image no longer trusts every directory.
//   2. The API, which trusts its registered roots, reads it: GET /api/tree reports no gitState
//      failure and lists the committed file.
// The container runs without a network; every request is made from inside it.
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const imageIndex = args.indexOf('--image');
const image = imageIndex >= 0 ? args[imageIndex + 1] : 'quality-studio:local';
const suffix = randomUUID().slice(0, 8);
const volume = `qs-git-check-${suffix}`;
const container = `qs-git-check-${suffix}`;
const token = `container-check-${randomUUID()}`;
const tokenHash = createHash('sha256').update(token).digest('hex');

function docker(argv, { allowFailure = false, quiet = false } = {}) {
  const result = spawnSync('docker', argv, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (!quiet && result.stdout) process.stdout.write(result.stdout);
  if (result.status !== 0 && !allowFailure) {
    process.stderr.write(result.stderr ?? '');
    throw new Error(`docker ${argv[0]} failed with ${result.status}`);
  }
  return result;
}

function cleanup() {
  docker(['rm', '-f', container], { allowFailure: true, quiet: true });
  docker(['volume', 'rm', volume], { allowFailure: true, quiet: true });
}

function sleep(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

try {
  if (args.includes('--build')) {
    console.log(`Building ${image} from ${repositoryRoot}`);
    const build = spawnSync('docker', ['build', '-t', image, repositoryRoot], { stdio: 'inherit' });
    if (build.status !== 0) throw new Error('docker build failed');
  }

  docker(['volume', 'create', volume], { quiet: true });
  docker(['run', '--rm', '--network', 'none', '--user', '0', '--entrypoint', 'sh', '-v', `${volume}:/srv/checkouts`, image, '-c', [
    'set -e',
    'mkdir -p /srv/checkouts/sample',
    'cd /srv/checkouts/sample',
    'git init -q',
    "printf 'namespace Sample;\\npublic sealed class Subject;\\n' > Sample.cs",
    'git add Sample.cs',
    'git -c user.name=check -c user.email=check@example.invalid commit -qm init',
    'chown -R 0:0 /srv/checkouts',
    'chmod -R a+rX /srv/checkouts',
  ].join('\n')]);

  docker(['run', '-d', '--name', container, '--network', 'none',
    '-v', `${volume}:/srv/checkouts:ro`,
    '-e', 'QualityStudio__RepositoryRoot=/srv/checkouts/sample',
    '-e', 'QualityStudio__AllowedRoots__0=/srv/checkouts',
    '-e', 'QualityStudio__Security__Clients__0__Id=container-check',
    '-e', `QualityStudio__Security__Clients__0__CredentialSha256=${tokenHash}`,
    '-e', 'QualityStudio__Security__Clients__0__Repositories__0=*',
    image], { quiet: true });

  let healthy = false;
  for (let attempt = 0; attempt < 60 && !healthy; attempt++) {
    healthy = docker(['exec', container, 'curl', '-fsS', 'http://127.0.0.1:8080/health'],
      { allowFailure: true, quiet: true }).status === 0;
    if (!healthy) sleep(1000);
  }
  if (!healthy) {
    docker(['logs', container], { allowFailure: true });
    throw new Error('The container did not become healthy.');
  }

  const owner = docker(['exec', container, 'stat', '-c', '%u', '/srv/checkouts/sample'], { quiet: true }).stdout.trim();
  const user = docker(['exec', container, 'id', '-u'], { quiet: true }).stdout.trim();
  console.log(`checkout owner uid ${owner}, image uid ${user}, mounted read-only`);
  if (owner === user) throw new Error('The checkout must belong to another uid for this check to mean anything.');

  const plain = docker(['exec', container, 'git', '-C', '/srv/checkouts/sample', 'rev-parse', 'HEAD'],
    { allowFailure: true, quiet: true });
  if (plain.status === 0 || !/dubious ownership/.test(plain.stderr)) {
    throw new Error(`Plain git was expected to refuse the foreign checkout, got status ${plain.status}: ${plain.stderr}`);
  }
  console.log('plain git as the image user: refused (dubious ownership), as expected');

  const tree = docker(['exec', container, 'curl', '-fsS', '-H', `Authorization: Bearer ${token}`,
    'http://127.0.0.1:8080/api/repos/default/tree?path='], { quiet: true }).stdout;
  const parsed = JSON.parse(tree);
  if (parsed.gitState) throw new Error(`The API could not read the checkout with git: ${JSON.stringify(parsed.gitState)}`);
  if (!tree.includes('Sample.cs')) throw new Error('The API tree does not list the committed file.');
  console.log('API through registered-root trust: git state read, Sample.cs listed');
  console.log('PASS: Git reads a read-only checkout owned by another uid inside the container.');
} catch (error) {
  console.error(`FAIL: ${error.message}`);
  process.exitCode = 1;
} finally {
  cleanup();
}
