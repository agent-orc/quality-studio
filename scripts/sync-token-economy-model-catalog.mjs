import { createHash } from 'node:crypto';
import { copyFile, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const targetDirectory = join(repositoryRoot, 'src', 'AgentOrchestrator.CodeQuality', 'catalogues');
const snapshotPath = join(targetDirectory, 'token-economy-model-catalog.snapshot.json');
const files = {
  'model-routing-policy.json': 'token-economy-model-routing-policy.json',
  'model-prices.json': 'token-economy-model-prices.json',
};

const args = process.argv.slice(2);
const checkOnly = args.includes('--check');
const sourceIndex = args.indexOf('--source');
const sourceRoot = sourceIndex >= 0
  ? resolve(args[sourceIndex + 1] ?? '')
  : process.env.TOKEN_ECONOMY_REPOSITORY
    ? resolve(process.env.TOKEN_ECONOMY_REPOSITORY)
    : null;

function fail(message) {
  console.error(message);
  process.exitCode = 1;
}

function sha256(content) {
  return createHash('sha256').update(content).digest('hex');
}

function git(source, ...command) {
  const result = spawnSync('git', ['-C', source, ...command], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(result.stderr.trim() || `git ${command.join(' ')} failed`);
  return result.stdout.trim();
}

async function readCatalog(path) {
  const content = await readFile(path);
  return { content, json: JSON.parse(content.toString('utf8')), hash: sha256(content) };
}

async function validateSnapshot(snapshot) {
  const local = {};
  for (const [upstreamName, targetName] of Object.entries(files)) {
    local[upstreamName] = await readCatalog(join(targetDirectory, targetName));
    if (local[upstreamName].hash !== snapshot.files[upstreamName]) {
      fail(`${targetName} does not match snapshot hash ${snapshot.files[upstreamName]}. Run catalog:sync.`);
    }
  }

  const policy = local['model-routing-policy.json'].json;
  const prices = local['model-prices.json'].json;
  if (policy.policyVersion !== snapshot.policyVersion) {
    fail(`Routing policy version ${policy.policyVersion} does not match snapshot ${snapshot.policyVersion}.`);
  }
  const priced = new Set(prices.map(item => item.modelId));
  for (const model of policy.models) {
    if (!priced.has(model.priceCatalogId)) fail(`Routing model ${model.canonicalId} has no price-catalog row.`);
  }
  const routed = new Set(policy.models.map(model => model.priceCatalogId));
  for (const price of prices) {
    if (!routed.has(price.modelId)) fail(`Price model ${price.modelId} has no routing-policy row.`);
  }
  return local;
}

const snapshot = JSON.parse(await readFile(snapshotPath, 'utf8'));
await validateSnapshot(snapshot);

if (sourceRoot) {
  const sourceDirectory = join(sourceRoot, 'src', 'TokenEconomy', 'catalog');
  const dirty = git(sourceRoot, 'status', '--porcelain', '--',
    'src/TokenEconomy/catalog/model-routing-policy.json',
    'src/TokenEconomy/catalog/model-prices.json');
  if (dirty) throw new Error('Token Economy catalog files have uncommitted changes; commit them before syncing.');

  const commit = git(sourceRoot, 'rev-parse', 'HEAD');
  const source = {};
  for (const upstreamName of Object.keys(files)) {
    source[upstreamName] = await readCatalog(join(sourceDirectory, upstreamName));
  }
  const policyVersion = source['model-routing-policy.json'].json.policyVersion;

  if (checkOnly) {
    for (const upstreamName of Object.keys(files)) {
      if (source[upstreamName].hash !== snapshot.files[upstreamName]) {
        fail(`Token Economy ${upstreamName} drifted from the checked-in Quality Studio snapshot.`);
      }
    }
    if (commit !== snapshot.upstreamCommit) fail(`Token Economy source commit is ${commit}; snapshot records ${snapshot.upstreamCommit}.`);
    if (policyVersion !== snapshot.policyVersion) fail(`Token Economy policy is ${policyVersion}; snapshot records ${snapshot.policyVersion}.`);
  } else {
    for (const [upstreamName, targetName] of Object.entries(files)) {
      await copyFile(join(sourceDirectory, upstreamName), join(targetDirectory, targetName));
    }
    const updated = {
      schemaVersion: 1,
      upstreamRepository: 'agent-orc/token-economy',
      upstreamCommit: commit,
      policyVersion,
      files: Object.fromEntries(Object.keys(files).map(name => [name, source[name].hash])),
    };
    await writeFile(snapshotPath, `${JSON.stringify(updated, null, 2)}\n`);
    console.log(`Synchronized Token Economy model catalogs at ${commit} (policy ${policyVersion}).`);
  }
} else if (!checkOnly) {
  fail('catalog:sync requires --source <token-economy-repository> or TOKEN_ECONOMY_REPOSITORY.');
}

if (!process.exitCode && checkOnly) {
  console.log(sourceRoot
    ? `Catalog snapshot matches Token Economy ${snapshot.upstreamCommit}.`
    : `Catalog snapshot integrity is valid at Token Economy ${snapshot.upstreamCommit}; set TOKEN_ECONOMY_REPOSITORY for upstream drift comparison.`);
}
