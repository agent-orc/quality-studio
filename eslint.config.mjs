// @ts-check
// Lints repository-owned Node scripts only. The Angular/TypeScript app has its
// own eslint.config.mjs in frontend/ scoped to that project's dependencies.
import eslint from '@eslint/js';
import globals from 'globals';

export default [
  {
    ignores: ['**/node_modules/**', 'frontend/**', 'bin/**', 'obj/**'],
  },
  {
    files: ['scripts/**/*.mjs', 'scripts/**/*.cjs', 'tests/*.mjs'],
    ...eslint.configs.recommended,
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.node },
    },
  },
];
