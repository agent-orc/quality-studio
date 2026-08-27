import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

const repositoryRoot = resolve(import.meta.dirname, '..');
const cliDll = process.env.QS_CLI_DLL || resolve(repositoryRoot, 'src/quality-cli/bin/Release/net10.0/quality.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const fixture = await mkdtemp(resolve(tmpdir(), 'qs-live-review-'));

try {
  await mkdir(resolve(fixture, 'src'), { recursive: true });
  await writeFile(resolve(fixture, 'src', 'CartTotals.cs'), `namespace Checkout;

public static class CartTotals
{
    public static decimal Calculate(IEnumerable<decimal> prices, decimal taxRate)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (taxRate < 0) throw new ArgumentOutOfRangeException(nameof(taxRate));
        var subtotal = prices.Sum();
        return decimal.Round(subtotal * (1 + taxRate), 2, MidpointRounding.ToEven);
    }
}
`);
  run('git', ['init', '--quiet'], fixture);
  run('git', ['config', 'user.email', 'perf-harness@example.invalid'], fixture);
  run('git', ['config', 'user.name', 'Quality Studio Perf Harness'], fixture);
  run('git', ['add', '.'], fixture);
  run('git', ['commit', '--quiet', '-m', 'Create review fixture'], fixture);

  const started = performance.now();
  const execution = await runAsync('dotnet', [cliDll, 'review', 'src/CartTotals.cs', '--kind', 'code'], fixture);
  const wallMs = performance.now() - started;
  const usageDirectory = resolve(fixture, '.quality', 'usage');
  const ledgerFiles = await readdir(usageDirectory);
  const ledgerLines = (await readFile(resolve(usageDirectory, ledgerFiles.sort().at(-1)), 'utf8')).trim().split('\n');
  const usage = JSON.parse(ledgerLines.at(-1));
  const result = {
    measuredAt: new Date().toISOString(),
    fixture: '12-line C# file in an isolated clean Git repository',
    command: 'quality review src/CartTotals.cs --kind code (runner-default model; no override)',
    wallMs: Number(wallMs.toFixed(2)),
    modelCallMs: usage.tokens.durationMs,
    postModelAndProcessOverheadMs: Number((wallMs - usage.tokens.durationMs).toFixed(2)),
    effectiveModel: usage.model,
    cliType: usage.cliType,
    inputTokens: usage.tokens.inputTokens,
    outputTokens: usage.tokens.outputTokens,
    cachedInputTokens: usage.tokens.cachedInputTokens,
    reasoningOutputTokens: usage.tokens.reasoningOutputTokens,
    stdout: execution.stdout.trim(),
    stderr: execution.stderr.trim(),
  };
  await mkdir(resultsRoot, { recursive: true });
  await writeFile(resolve(resultsRoot, 'review-live-latency.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally {
  await rm(fixture, { recursive: true, force: true });
}

function run(executable, arguments_, cwd) {
  const result = spawnSync(executable, arguments_, { cwd, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`${executable} failed: ${result.stderr}`);
}

function runAsync(executable, arguments_, cwd) {
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(executable, arguments_, { cwd, env: process.env, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', chunk => stdout += chunk);
    child.stderr.setEncoding('utf8').on('data', chunk => stderr += chunk);
    child.once('error', rejectPromise);
    child.once('exit', code => code === 0
      ? resolvePromise({ stdout, stderr })
      : rejectPromise(new Error(`${executable} exited ${code}: ${stderr}`)));
  });
}
