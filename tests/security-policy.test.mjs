import test from 'node:test';
import assert from 'node:assert/strict';
import { assertNugetAudit, assertTrackedConfiguration } from '../scripts/security-policy.mjs';

test('NuGet policy accepts an empty advisory result and rejects a vulnerable package', () => {
  assert.doesNotThrow(() => assertNugetAudit({ version: 1, projects: [{ path: 'safe.csproj' }] }));
  assert.throws(() => assertNugetAudit({
    version: 1,
    projects: [{ frameworks: [{ topLevelPackages: [{ id: 'Unsafe', vulnerabilities: [{ severity: 'High' }] }] }] }],
  }), /1 vulnerable package entry/);
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
