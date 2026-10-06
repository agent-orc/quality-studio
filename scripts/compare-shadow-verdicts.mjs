#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

// Input: [{taskId, baseSha, headSha, studioCodeQuality, qsVerdictPath}].
// Each QS artifact must be the response from POST /api/repos/{id}/change-review.
const [inputPath, outputPath] = process.argv.slice(2);
if (!inputPath || !outputPath) {
  console.error('Usage: node scripts/compare-shadow-verdicts.mjs <20-pairs.json> <report.json>');
  process.exit(2);
}
try {
  const pairs = JSON.parse(readFileSync(inputPath, 'utf8'));
  if (!Array.isArray(pairs) || pairs.length !== 20 ||
      new Set(pairs.map(pair => pair.taskId)).size !== 20) {
    throw new Error('The cohort must contain exactly 20 distinct Agent Studio tasks.');
  }
  const rows = pairs.map(pair => {
    const qs = JSON.parse(readFileSync(resolve(dirname(inputPath), pair.qsVerdictPath), 'utf8'));
    if (qs.schemaVersion !== 1 || qs.schema !== 'https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json' ||
        qs.baseSha !== pair.baseSha || qs.headSha !== pair.headSha) {
      throw new Error(`${pair.taskId}: QS artifact does not bind the paired base and head SHAs.`);
    }
    if (!/^sha256:[0-9a-f]{64}$/.test(qs.policyHash) ||
        !/^sha256:[0-9a-f]{64}$/.test(qs.ruleSetHash) || !Array.isArray(qs.findings)) {
      throw new Error(`${pair.taskId}: QS artifact is missing policy, rule-set, or finding evidence.`);
    }
    if (!['pass', 'concerns', 'block'].includes(pair.studioCodeQuality)) {
      throw new Error(`${pair.taskId}: Studio code-quality aspect verdict is missing or unsupported.`);
    }
    if (!['pass', 'concerns', 'block'].includes(qs.verdict)) {
      throw new Error(`${pair.taskId}: QS returned ${qs.verdict}; infrastructure results cannot enter agreement.`);
    }
    return {
      taskId: pair.taskId,
      baseSha: pair.baseSha,
      headSha: pair.headSha,
      studioCodeQuality: pair.studioCodeQuality,
      qualityStudio: qs.verdict,
      agreement: pair.studioCodeQuality === qs.verdict,
      policyHash: qs.policyHash,
      ruleSetHash: qs.ruleSetHash,
      model: qs.model,
      thinkingLevel: qs.thinkingLevel,
      findings: qs.findings.map(finding => ({ id: finding.id, ruleId: finding.ruleId,
        severity: finding.severity, path: finding.path, side: finding.side, line: finding.line })),
    };
  });
  const agreements = rows.filter(row => row.agreement).length;
  const report = {
    schemaVersion: 1,
    comparedAspect: 'code-quality',
    cohortSize: rows.length,
    agreements,
    agreementRate: agreements / rows.length,
    pinning: {
      policyHashes: [...new Set(rows.map(row => row.policyHash))],
      ruleSetHashes: [...new Set(rows.map(row => row.ruleSetHash))],
      models: [...new Set(rows.map(row => row.model))],
      thinkingLevels: [...new Set(rows.map(row => row.thinkingLevel))],
    },
    disagreements: rows.filter(row => !row.agreement),
    rows,
    note: 'Shadow comparison only. Agent Studio policy and gates retain authority.',
  };
  writeFileSync(outputPath, JSON.stringify(report, null, 2) + '\n');
  console.log(`${agreements}/20 agree (${(report.agreementRate * 100).toFixed(1)}%); ${report.disagreements.length} disagreements`);
} catch (error) {
  console.error(error.message);
  process.exit(2);
}
