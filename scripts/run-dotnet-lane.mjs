import { mkdir } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dotnetProjects, testLanes } from './test-lanes.mjs';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));

export async function runDotnetLane(arguments_) {
  const options = parseArguments(arguments_);
  const lane = testLanes[options.lane];
  const projects = lane.projects
    ? dotnetProjects.filter(project => lane.projects.includes(project.id))
    : dotnetProjects;
  const inventory = [];

  for (const project of projects) {
    const output = await run('dotnet', testArguments(project.path, lane.filter, options, true), true);
    const count = countListedTests(output);
    if (count === 0) throw new Error(`${options.lane} lane selected zero tests for ${project.id}.`);
    inventory.push(`${project.id}=${count}`);
  }

  console.log(`lane inventory: ${options.lane} (${lane.purpose}); ${inventory.join(', ')}`);
  if (options.listOnly) return;

  for (const project of projects) {
    const args = testArguments(project.path, lane.filter, options, false);
    if (options.coverageRoot) {
      const resultsDirectory = resolve(options.coverageRoot, project.id);
      await mkdir(resultsDirectory, { recursive: true });
      args.push('--collect:XPlat Code Coverage', '--results-directory', resultsDirectory);
    }
    await run('dotnet', args, false);
  }
}

function parseArguments(arguments_) {
  const [lane, ...rest] = arguments_;
  if (!testLanes[lane]) {
    throw new Error(`First argument must be one of: ${Object.keys(testLanes).join(', ')}.`);
  }

  const options = { lane, configuration: 'Release', noBuild: false, listOnly: false, coverageRoot: null };
  for (let index = 0; index < rest.length; index++) {
    const argument = rest[index];
    if (argument === '--configuration' && rest[index + 1]) {
      options.configuration = rest[++index];
    } else if (argument === '--no-build') {
      options.noBuild = true;
    } else if (argument === '--list-only') {
      options.listOnly = true;
    } else if (argument === '--coverage-root' && rest[index + 1]) {
      options.coverageRoot = rest[++index];
    } else {
      throw new Error(`Unknown or incomplete test-lane argument: ${argument}`);
    }
  }
  return options;
}

function testArguments(project, filter, options, listOnly) {
  const args = [
    'test',
    project,
    '--configuration',
    options.configuration,
    '--filter',
    filter,
    '--verbosity',
    listOnly ? 'quiet' : 'minimal',
  ];
  if (options.noBuild) args.push('--no-build');
  if (listOnly) args.push('--list-tests');
  return args;
}

function countListedTests(output) {
  const marker = 'The following Tests are available:';
  const markerIndex = output.lastIndexOf(marker);
  if (markerIndex < 0) throw new Error('dotnet test did not emit a test inventory.');
  return output
    .slice(markerIndex + marker.length)
    .split(/\r?\n/)
    .filter(line => /^ {4}\S/.test(line)).length;
}

async function run(command, args, capture) {
  console.log(`> ${command} ${args.map(formatArgument).join(' ')}`);
  return await new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      env: process.env,
      stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
      windowsHide: true,
    });
    let stdout = '';
    let stderr = '';
    if (capture) {
      child.stdout.on('data', chunk => stdout += chunk.toString('utf8'));
      child.stderr.on('data', chunk => stderr += chunk.toString('utf8'));
    }
    child.once('error', rejectPromise);
    child.once('exit', code => {
      if (code === 0) resolvePromise(stdout);
      else rejectPromise(new Error(`${command} exited ${code}.\n${stdout}${stderr}`.trim()));
    });
  });
}

function formatArgument(argument) {
  return /\s|&/.test(argument) ? JSON.stringify(argument) : argument;
}

if (fileURLToPath(import.meta.url) === resolve(process.argv[1] ?? '')) {
  await runDotnetLane(process.argv.slice(2));
}
