import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  assertNpmAudit,
  assertNugetAudit,
  assertSbom,
  assertTrackedConfiguration,
} from '../scripts/security-policy.mjs';

test('npm policy accepts a clean audit and rejects production high or critical advisories', () => {
  const clean = {
    auditReportVersion: 2,
    metadata: { vulnerabilities: { info: 0, low: 0, moderate: 6, high: 0, critical: 0, total: 6 } },
  };
  assert.doesNotThrow(() => assertNpmAudit(clean));
  assert.throws(() => assertNpmAudit({
    ...clean,
    metadata: { vulnerabilities: { ...clean.metadata.vulnerabilities, high: 1, total: 7 } },
  }), /1 high and 0 critical/);
});

test('NuGet policy accepts an empty advisory result and rejects a vulnerable package', () => {
  assert.doesNotThrow(() => assertNugetAudit({ version: 1, projects: [{ path: 'safe.csproj' }] }));
  assert.throws(() => assertNugetAudit({
    version: 1,
    projects: [{ frameworks: [{ topLevelPackages: [{ id: 'Unsafe', vulnerabilities: [{ severity: 'High' }] }] }] }],
  }), /1 vulnerable package entry/);
});

test('SBOM policy rejects a manifest that silently omitted detected dependencies', () => {
  const rootPackage = { SPDXID: 'SPDXRef-RootPackage', name: 'QualityStudio' };
  assert.doesNotThrow(() => assertSbom({
    spdxVersion: 'SPDX-2.2',
    packages: [rootPackage, { SPDXID: 'SPDXRef-Package', name: 'dependency' }],
  }));
  assert.throws(() => assertSbom({
    spdxVersion: 'SPDX-2.2',
    packages: [rootPackage],
  }), /no detected dependency packages/);
});

test('CI validates the generated SBOM before retaining it as evidence', async () => {
  const workflow = await readFile(new URL('../.github/workflows/build.yml', import.meta.url), 'utf8');
  assert.match(
    workflow,
    /sbom-tool" generate[^\n]+\n\s+node scripts\/security-policy\.mjs sbom "\$RUNNER_TEMP\/quality-studio-evidence\/sbom\/_manifest\/spdx_2\.2\/manifest\.spdx\.json"/,
  );
});

test('tracked configuration rejects a QS-53-style root and cleartext credentials', () => {
  const safe = {
    QualityStudio: {
      AllowedRoots: ['../..'],
      AnalyzerProfiles: [],
      Security: { Clients: [] },
    },
  };
  assert.doesNotThrow(() => assertTrackedConfiguration(safe));
  assert.throws(() => assertTrackedConfiguration({
    QualityStudio: { ...safe.QualityStudio, AllowedRoots: ['../../../agent-taskboard-devspace'] },
  }), /machine-specific AllowedRoots/);
  assert.throws(() => assertTrackedConfiguration({
    QualityStudio: { ...safe.QualityStudio, AgentToken: 'do-not-track-this' },
  }), /cleartext credential field/);
});
