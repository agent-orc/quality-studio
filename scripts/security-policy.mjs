import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export function assertNugetAudit(document, source = 'NuGet audit') {
  if (document?.version !== 1 || !Array.isArray(document.projects)) {
    throw new Error(`${source} is not a recognized dotnet vulnerable-package result.`);
  }
  const vulnerabilities = [];
  visit(document, '$', vulnerabilities);
  if (vulnerabilities.length > 0) {
    throw new Error(`${source} contains ${vulnerabilities.length} vulnerable package entr${vulnerabilities.length === 1 ? 'y' : 'ies'}.`);
  }
}

export function assertTrackedConfiguration(document, source = 'tracked appsettings') {
  const qualityStudio = document?.QualityStudio;
  if (!qualityStudio || !Array.isArray(qualityStudio.AllowedRoots)) {
    throw new Error(`${source} must declare QualityStudio.AllowedRoots.`);
  }
  const invalidRoot = qualityStudio.AllowedRoots.find(root => root !== '../..');
  if (invalidRoot !== undefined) {
    throw new Error(`${source} contains a machine-specific AllowedRoots entry; use deployment configuration instead.`);
  }
  if ((qualityStudio.Security?.Clients?.length ?? 0) !== 0) {
    throw new Error(`${source} must not contain hosted client credentials; use a secret-backed deployment source.`);
  }
  if ((qualityStudio.AnalyzerProfiles?.length ?? 0) !== 0) {
    throw new Error(`${source} must not contain host analyzer profiles; use deployment configuration instead.`);
  }
  rejectCleartextCredentialFields(document, source);
}

function visit(value, path, vulnerabilities) {
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index += 1) visit(value[index], `${path}[${index}]`, vulnerabilities);
    return;
  }
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    if (key.toLowerCase() === 'vulnerabilities' && Array.isArray(child)) {
      vulnerabilities.push(...child.map(entry => ({ path: `${path}.${key}`, entry })));
    } else {
      visit(child, `${path}.${key}`, vulnerabilities);
    }
  }
}

function rejectCleartextCredentialFields(value, source, path = '$') {
  if (Array.isArray(value)) {
    value.forEach((entry, index) => rejectCleartextCredentialFields(entry, source, `${path}[${index}]`));
    return;
  }
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    const normalized = key.toLowerCase();
    const credentialField = /(credential|password|secret|token)/.test(normalized) &&
      normalized !== 'credentialsha256';
    if (credentialField && typeof child === 'string' && child.trim().length > 0) {
      throw new Error(`${source} contains a cleartext credential field at ${path}.${key}.`);
    }
    rejectCleartextCredentialFields(child, source, `${path}.${key}`);
  }
}

async function main(args) {
  const [command, ...paths] = args;
  if (!command || paths.length === 0) {
    throw new Error('Usage: security-policy.mjs <nuget|config> <json-path> [json-path...]');
  }
  for (const path of paths) {
    const document = JSON.parse(await readFile(path, 'utf8'));
    if (command === 'nuget') assertNugetAudit(document, path);
    else if (command === 'config') assertTrackedConfiguration(document, path);
    else throw new Error(`Unknown security policy command '${command}'.`);
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
