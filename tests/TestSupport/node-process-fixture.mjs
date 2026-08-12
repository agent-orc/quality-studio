import { createServer } from 'node:net';

export function npmInstallStub() {
  return `
import { appendFileSync } from 'node:fs';
if (process.argv[2] !== 'ci') {
  console.error('expected npm ci arguments, received: ' + process.argv.slice(2).join(' '));
  process.exit(2);
}
appendFileSync(process.env.QUALITY_STUDIO_MARKER_FILE, 'ci\\n');
`;
}

export function npmStubEnvironment(marker, stub) {
  return {
    ...process.env,
    QUALITY_STUDIO_MARKER_FILE: marker,
    QUALITY_STUDIO_NPM_COMMAND: process.execPath,
    QUALITY_STUDIO_NPM_COMMAND_ARGUMENTS: JSON.stringify([stub]),
  };
}

export async function freePorts(count) {
  if (!Number.isSafeInteger(count) || count < 1) throw new TypeError('count must be a positive integer');
  const servers = [];
  try {
    for (let index = 0; index < count; index++) {
      const server = createServer();
      await new Promise((resolvePromise, rejectPromise) =>
        server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
      servers.push(server);
    }
    return servers.map(server => {
      const address = server.address();
      if (!address || typeof address === 'string') throw new Error('fixture did not allocate an IP port');
      return address.port;
    });
  } finally {
    await Promise.all(servers.map(server => new Promise((resolvePromise, rejectPromise) =>
      server.close(error => error ? rejectPromise(error) : resolvePromise()))));
  }
}

export async function assertPortsReleased(ports) {
  for (const port of ports) {
    const server = createServer();
    await new Promise((resolvePromise, rejectPromise) =>
      server.listen(port, '127.0.0.1', resolvePromise).once('error', rejectPromise));
    await new Promise((resolvePromise, rejectPromise) =>
      server.close(error => error ? rejectPromise(error) : resolvePromise()));
  }
}
