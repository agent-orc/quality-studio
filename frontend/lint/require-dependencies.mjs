import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const eslint = fileURLToPath(new URL('../node_modules/.bin/eslint', import.meta.url));
if (!existsSync(eslint)) {
  console.error('BuildProfile preparation missing: frontend/node_modules/.bin/eslint was not found. Run .agent-studio/prepare from the checkout root.');
  process.exit(1);
}
