import eslint from '@eslint/js';
import angular from 'angular-eslint';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: [
      'frontend/dist/**',
      'frontend/out-tsc/**',
      'frontend/node_modules/**',
      'frontend/evidence/**',
      'frontend/coverage/**',
    ],
  },
  {
    files: ['frontend/src/**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      complexity: ['warn', { max: 18 }],
    },
  },
  {
    files: ['frontend/src/**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
  },
  {
    files: ['frontend/**/*.{js,mjs,cjs}', 'scripts/**/*.mjs', 'tests/**/*.mjs'],
    extends: [eslint.configs.recommended],
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
    rules: {
      complexity: ['warn', { max: 18 }],
    },
  },
  {
    files: ['frontend/src/**/*.spec.ts', 'frontend/tests/**/*.{mjs,cjs}', 'tests/**/*.mjs'],
    rules: {
      complexity: 'off',
    },
  },
);
