import { fileURLToPath } from 'node:url';

// This is this repository's design, not a universal Angular folder convention.
// Update this dependency contract and quality-architecture.json together when ownership changes.
export const angularArchitecture = {
  appRoot: fileURLToPath(new URL('../src/app/', import.meta.url)),
  layers: [
    { path: 'core/models', allow: ['core/models'] },
    { path: 'core', allow: ['core', 'shared/utils'] },
    { path: 'shared/utils', allow: ['shared/utils', 'core/models'] },
    { path: 'shared', allow: ['shared', 'core/models'] },
    { path: 'features', allow: ['core', 'shared', '$feature'] },
    { path: 'shell', allow: ['core', 'shared', 'features', 'shell'] },
  ],
  aliases: { '@app/': '' },
};
