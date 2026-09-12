// One authored methodology and the existing rule catalogue feed both product surfaces.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const startMarker = '<!-- review-reference:start -->';
const endMarker = '<!-- review-reference:end -->';
const escape = value => String(value).replace(/[&<>"']/g, character =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
const sourceLink = path => `https://github.com/agent-orc/quality-studio/blob/main/${path}`;

export function validateReference(methodology, catalogue) {
  if (methodology.schemaVersion !== 1 || !methodology.version) throw new Error('Unsupported review methodology.');
  const ruleIds = new Set(catalogue.entries.map(rule => rule.id));
  const sourceIds = new Set(methodology.sources.map(source => source.id));
  for (const source of methodology.sources) {
    if (new URL(source.url).protocol !== 'https:') throw new Error(`Unsafe reference URL: ${source.id}`);
  }
  const ids = new Set();
  for (const item of [...methodology.principles, ...methodology.metrics]) {
    if (ids.has(item.id)) throw new Error(`Duplicate reference: ${item.id}`);
    ids.add(item.id);
    for (const key of ['id', 'title', 'why', 'limits']) {
      if (typeof item[key] !== 'string' || !item[key].trim()) throw new Error(`${item.id} requires ${key}.`);
    }
    for (const id of item.ruleIds ?? []) if (!ruleIds.has(id)) throw new Error(`Unknown rule ${id}.`);
    for (const id of item.sourceIds ?? []) if (!sourceIds.has(id)) throw new Error(`Unknown source ${id}.`);
  }
  for (const metric of methodology.metrics) {
    if (!metric.formula || !metric.interpretation || !metric.implementation) throw new Error(`${metric.id} requires a formula and its implementation.`);
    if (metric.implementation.includes('..') || metric.implementation.startsWith('/')) throw new Error('Source links must stay inside the repository.');
  }
}

export function renderReference(methodology, catalogue) {
  validateReference(methodology, catalogue);
  const sources = new Map(methodology.sources.map(source => [source.id, source]));
  const principles = methodology.principles.map(item => `<article class="review-reference-card"><h3>${escape(item.title)}</h3><p>${escape(item.why)}</p><dl><dt>How we check</dt><dd>${escape(item.method)}</dd><dt>Limits</dt><dd>${escape(item.limits)}</dd></dl><p class="reference-links">${item.sourceIds.map(id => { const source = sources.get(id); return `<a href="${escape(source.url)}">${escape(source.title)}</a>`; }).join(' · ')}</p></article>`).join('\n');
  const metrics = methodology.metrics.map(item => `<details class="review-reference-detail"><summary>${escape(item.title)}</summary><p>${escape(item.why)}</p><code class="metric-formula">${escape(item.formula)}</code><p>${escape(item.interpretation)}</p><p><strong>Limits:</strong> ${escape(item.limits)}</p><a href="${escape(sourceLink(item.implementation))}">Implementation</a></details>`).join('\n');
  const rules = catalogue.entries.map(rule => `<details class="review-reference-detail"><summary><span>${escape(rule.id)} · ${escape(rule.title)}</span><small>${escape(rule.technology)} · ${escape(rule.kinds.join(', '))} · ${rule.defaultOn && rule.enabled ? 'enabled by default' : 'disabled by default'}</small></summary><p>${escape(rule.statement)}</p><dl><dt>Why</dt><dd>${escape(rule.rationale)}</dd><dt>How to detect it</dt><dd>${escape(rule.detection)}</dd></dl><p><strong>Severity:</strong> ${escape(rule.severity)}. <strong>Deterministic mappings:</strong> ${escape(rule.deterministicRuleIds.join(', ') || 'Reviewer judgment; no deterministic rule mapping.')}</p><details><summary>Examples</summary><pre><code>${escape(rule.goodExample)}</code></pre><pre><code>${escape(rule.badExample)}</code></pre></details></details>`).join('\n');
  return `${startMarker}
    <section id="criteria"><div class="wrap"><div class="section-head"><div><span class="kicker">Rules, evidence and judgment</span><h2>Know what a review means.</h2></div><p>${escape(methodology.overview)}</p></div>
      <div class="review-reference-grid">${principles}</div>
      <h3 class="review-reference-heading">Metrics with their assumptions exposed</h3><p>These formulas document the implemented measures. They help prioritize investigation; none is a probability that a file is correct.</p>${metrics}
      <h3 class="review-reference-heading">The built-in rule library · ${escape(catalogue.catalogueVersion)}</h3><p>These are the shipped defaults from the same rule files used by the review engine. The tool shows your repository’s effective enablement, severity overrides and input-budget preview. Applicable rules are selected by review kind and technology; examples and rationale stay out of the prompt to preserve its budget.</p>
      <details class="review-reference-detail"><summary>Browse all ${catalogue.entries.length} named rules</summary>${rules}</details>
    </div></section>
    ${endMarker}`;
}

export async function syncReviewReference(root = repositoryRoot, check = false) {
  const methodology = JSON.parse(await readFile(join(root, 'rules', 'review-methodology.json'), 'utf8'));
  const catalogue = JSON.parse(await readFile(join(root, 'backend', 'AgentOrchestrator.CodeQuality', 'catalogues', 'rule-catalogue.v1.json'), 'utf8'));
  validateReference(methodology, catalogue);
  for (const metric of methodology.metrics) await readFile(join(root, metric.implementation));
  const frontendPath = join(root, 'frontend', 'src', 'app', 'features', 'settings', 'review-criteria', 'review-methodology.generated.ts');
  const frontend = `// Generated from rules/review-methodology.json; run npm run rules:sync.\nexport const REVIEW_METHODOLOGY = ${JSON.stringify(methodology, null, 2)} as const;\n`;
  const websitePath = join(root, 'website', 'index.html');
  const website = await readFile(websitePath, 'utf8');
  const start = website.indexOf(startMarker);
  const end = website.indexOf(endMarker);
  if (start < 0 || end < start) throw new Error('Website review-reference markers are missing.');
  const renderedWebsite = website.slice(0, start) + renderReference(methodology, catalogue) + website.slice(end + endMarker.length);
  const outputs = [[frontendPath, frontend], [websitePath, renderedWebsite]];
  for (const [path, expected] of outputs) {
    if (check) {
      const current = await readFile(path, 'utf8').catch(() => null);
      if (current !== expected) throw new Error(`Review reference drifted: ${path}. Run npm run rules:sync.`);
    } else {
      await mkdir(dirname(path), { recursive: true });
      await writeFile(path, expected);
    }
  }
  console.log(`review reference: ${methodology.metrics.length} metrics, ${catalogue.entries.length} rules, tool and website ${check ? 'match' : 'updated'}.`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await syncReviewReference(repositoryRoot, process.argv.includes('--check'));
}
