#!/usr/bin/env node
// Mints one hosted-mode API credential and prints the environment block for it.
//
//   node scripts/new-api-token.mjs agent-studio --registrar
//   node scripts/new-api-token.mjs reporting --repositories payments,billing --index 1
//
// The token is printed once and never stored. Only its SHA-256 goes into the host configuration:
// QualityStudio__Security__Clients__<index>__CredentialSha256.

import { createHash, randomBytes } from 'node:crypto';

const TOKEN_BYTES = 32;

function parseArguments(argv) {
  const positional = [];
  const options = { repositories: [], registrar: false, index: 0 };
  for (let cursor = 0; cursor < argv.length; cursor++) {
    const argument = argv[cursor];
    if (argument === '--registrar') {
      options.registrar = true;
    } else if (argument === '--repositories') {
      options.repositories = String(argv[++cursor] ?? '')
        .split(',')
        .map((value) => value.trim().toLowerCase())
        .filter((value) => value.length > 0);
    } else if (argument === '--index') {
      options.index = Number.parseInt(argv[++cursor] ?? '', 10);
    } else if (argument === '--help' || argument === '-h') {
      options.help = true;
    } else if (argument.startsWith('-')) {
      throw new Error(`Unknown option: ${argument}`);
    } else {
      positional.push(argument);
    }
  }
  options.id = positional[0];
  return options;
}

function usage() {
  return [
    'Usage: node scripts/new-api-token.mjs <client-id> [options]',
    '',
    '  --repositories <a,b>  Repository ids this client may reach (default: *).',
    '  --registrar           Allow POST /api/repos and PUT/DELETE /api/repos/{id}. Implies "*".',
    '  --index <n>           Position in QualityStudio__Security__Clients__<n>__ (default: 0).',
  ].join('\n');
}

function main() {
  const options = parseArguments(process.argv.slice(2));
  if (options.help || !options.id) {
    process.stdout.write(`${usage()}\n`);
    process.exitCode = options.help ? 0 : 2;
    return;
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(options.id)) {
    throw new Error('Client id must be 1-128 characters of letters, digits, dot, underscore or hyphen.');
  }
  if (!Number.isInteger(options.index) || options.index < 0) {
    throw new Error('--index must be a non-negative integer.');
  }

  // A registrar may configure repository roots, so it must hold wildcard access; the API refuses
  // any other combination at startup.
  const repositories = options.registrar
    ? ['*']
    : options.repositories.length > 0
      ? options.repositories
      : ['*'];

  const token = randomBytes(TOKEN_BYTES).toString('base64url');
  const digest = createHash('sha256').update(token, 'utf8').digest('hex');
  const prefix = `QualityStudio__Security__Clients__${options.index}__`;
  const lines = [
    `${prefix}Id=${options.id}`,
    `${prefix}CredentialSha256=${digest}`,
    ...repositories.map((repository, position) => `${prefix}Repositories__${position}=${repository}`),
  ];
  if (options.registrar) lines.push(`${prefix}CanRegisterRepositories=true`);

  process.stdout.write(
    [
      'Token (shown once, store it in the calling client):',
      `  ${token}`,
      '',
      'Host configuration:',
      ...lines.map((line) => `  ${line}`),
      '',
      'The client sends it as:',
      `  Authorization: Bearer ${token}`,
      `  X-Client-Id: ${options.id}      (required on POST, PUT, PATCH and DELETE)`,
      '',
    ].join('\n'),
  );
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
