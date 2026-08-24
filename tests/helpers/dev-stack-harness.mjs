// Shared fixtures for the root dev-stack integration suite.
//
// The suite drives real child processes, so every fixture here has to be
// platform neutral and free of fixed ports: the same five cases run on the
// Linux and Windows host-integration jobs. Failures must carry the captured
// launcher transcript, otherwise an early child exit is indistinguishable from
// a readiness timeout.

import assert from 'node:assert/strict';
import { chmod, mkdtemp, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));

export const launcherPath = resolve(repoRoot, 'scripts', 'dev-stack.mjs');
export const launcherCwd = repoRoot;

export async function createSandbox(prefix) {
  return await mkdtemp(join(tmpdir(), prefix));
}

/**
 * Reserves `count` distinct loopback ports by holding them all open at once and
 * releasing them together. Reserving them one at a time can hand out the same
 * port twice, which is exactly the collision the fixed 5127x/4207x ports caused.
 */
export async function reserveFreePorts(count) {
  const servers = await Promise.all(
    Array.from({ length: count }, () =>
      new Promise((resolvePromise, rejectPromise) => {
        const server = createServer();
        server.once('error', rejectPromise);
        server.listen(0, '127.0.0.1', () => resolvePromise(server));
      }),
    ),
  );

  const ports = servers.map(server => server.address().port);
  await Promise.all(servers.map(server => new Promise(done => server.close(done))));
  return ports;
}

/**
 * Writes an executable npm stand-in that records every invocation in
 * `QUALITY_STUDIO_MARKER_FILE`. The launcher spawns the npm command directly on
 * POSIX (no shell), so the stub must be a real executable there rather than a
 * Windows batch file.
 */
export async function createNpmStub(sandbox, name = 'npm-stub') {
  const script = join(sandbox, `${name}.mjs`);
  await writeFile(
    script,
    [
      "import { appendFileSync } from 'node:fs';",
      'const marker = process.env.QUALITY_STUDIO_MARKER_FILE;',
      "if (marker) appendFileSync(marker, 'ci\\n');",
      'process.exit(0);',
      '',
    ].join('\n'),
  );

  if (process.platform === 'win32') {
    const command = join(sandbox, `${name}.cmd`);
    await writeFile(command, `@echo off\r\n"${process.execPath}" "${script}" %*\r\nexit /b %ERRORLEVEL%\r\n`);
    return command;
  }

  const command = join(sandbox, name);
  await writeFile(command, `#!/bin/sh\nexec "${process.execPath}" "${script}" "$@"\n`);
  await chmod(command, 0o755);
  return command;
}

/**
 * Starts the launcher and returns a session whose assertions carry the captured
 * transcript.
 *
 * `expect` selects the awaited outcome:
 *   'ready'       wait for the ready line, then terminate the launcher
 *   'ready-keep'  wait for the ready line and leave the launcher running
 *   'failure'     wait for a non-zero exit
 */
export async function runLauncher({ args, env = process.env, expect = 'ready', readyTimeoutMs = 20000 }) {
  const child = spawn(process.execPath, [launcherPath, ...args], {
    cwd: launcherCwd,
    env,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  const transcript = { stdout: '', stderr: '' };
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', chunk => (transcript.stdout += chunk));
  child.stderr.on('data', chunk => (transcript.stderr += chunk));

  const session = {
    child,
    get stdout() {
      return transcript.stdout;
    },
    get stderr() {
      return transcript.stderr;
    },
    diagnostics(headline) {
      return [
        headline,
        `launcher: ${process.execPath} ${launcherPath} ${args.join(' ')}`,
        '--- launcher stdout ---',
        transcript.stdout.trimEnd() || '(empty)',
        '--- launcher stderr ---',
        transcript.stderr.trimEnd() || '(empty)',
      ].join('\n');
    },
  };

  if (expect === 'failure') {
    const exitCode = await new Promise(done => child.once('exit', code => done(code ?? 0)));
    assert.notEqual(exitCode, 0, session.diagnostics('Expected the launcher to exit non-zero.'));
    return session;
  }

  try {
    await waitForReady(session, readyTimeoutMs);
  } catch (error) {
    // Without this the launcher survives the failed assertion, its pipes keep the test
    // process alive, and a readiness flake turns into a job timeout with no message.
    await terminate(child);
    throw error;
  }

  if (expect === 'ready') await terminate(child);
  return session;
}

async function waitForReady(session, readyTimeoutMs) {
  await new Promise((resolvePromise, rejectPromise) => {
    const settleFailure = headline => {
      clearTimeout(timeout);
      rejectPromise(new Error(session.diagnostics(headline)));
    };
    const timeout = setTimeout(
      () => settleFailure(`Launcher did not report readiness within ${readyTimeoutMs} ms.`),
      readyTimeoutMs,
    );

    if (session.stdout.includes('ready:')) {
      clearTimeout(timeout);
      resolvePromise();
      return;
    }

    session.child.stdout.on('data', () => {
      if (!session.stdout.includes('ready:')) return;
      clearTimeout(timeout);
      resolvePromise();
    });
    session.child.once('error', error => settleFailure(`Launcher failed to start: ${error.message}`));
    session.child.once('exit', code => settleFailure(`Launcher exited early with code ${code ?? 'null'}.`));
  });
}

export function expectMatch(session, value, pattern, what) {
  assert.match(value, pattern, session.diagnostics(`Expected ${what} to match ${pattern}.`));
}

export async function terminate(child) {
  if (child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGINT');
  await new Promise(done => child.once('exit', done));
}

export async function fetchText(url) {
  const response = await fetch(url);
  assert.equal(response.status, 200, `Expected 200 from ${url}, received ${response.status}.`);
  return await response.text();
}
