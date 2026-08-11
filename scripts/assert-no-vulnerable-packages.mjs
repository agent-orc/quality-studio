import { readFile } from 'node:fs/promises';

const reports = process.argv.slice(2);
if (reports.length === 0) {
  console.error('Usage: node scripts/assert-no-vulnerable-packages.mjs <dotnet-package-list.json>...');
  process.exit(2);
}

let vulnerabilityCount = 0;
for (const report of reports) {
  const document = JSON.parse(await readFile(report, 'utf8'));
  visit(document);
}

if (vulnerabilityCount > 0) {
  console.error(`NuGet advisory gate found ${vulnerabilityCount} vulnerable package entr${vulnerabilityCount === 1 ? 'y' : 'ies'}.`);
  process.exit(1);
}

console.log(`NuGet advisory gate passed for ${reports.length} report${reports.length === 1 ? '' : 's'}.`);

function visit(value) {
  if (Array.isArray(value)) {
    for (const item of value) visit(item);
    return;
  }
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    if (key === 'vulnerabilities' && Array.isArray(child)) {
      vulnerabilityCount += child.length;
    } else {
      visit(child);
    }
  }
}
