import eslint from '@eslint/js';
import angular from 'angular-eslint';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import architecture from './lint/architecture.mjs';
import { angularArchitecture } from './lint/architecture.config.mjs';
import { typographyProcessor } from './lint/typography.mjs';
import { typographyContract } from './lint/typography.config.mjs';

// Every rule keeps the severity its own configuration defines. `npm run lint` is a gate:
// a violation fails the command rather than scrolling past as a warning.
export default tseslint.config(
  {
    ignores: [
      'frontend/dist/**',
      'frontend/out-tsc/**',
      'frontend/node_modules/**',
      'frontend/evidence/**',
      'frontend/coverage/**',
      '.quality/**',
    ],
  },
  {
    files: ['frontend/src/**/*.ts', 'src/**/*.ts', 'frontend/style-reference/**/*.ts', 'style-reference/**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    processor: angular.processInlineTemplates,
    plugins: { 'quality-architecture': architecture },
    rules: { 'quality-architecture/layer-imports': ['error', angularArchitecture] },
  },
  {
    files: ['frontend/src/**/*.css', 'src/**/*.css'],
    plugins: { 'quality-architecture': architecture },
    rules: { 'quality-architecture/minimum-font-size': 'error' },
    processor: typographyProcessor(typographyContract),
  },
  {
    files: ['frontend/src/**/*.html', 'src/**/*.html', 'frontend/style-reference/**/*.html', 'style-reference/**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
  },
  {
    files: [
      'frontend/**/*.{js,mjs,cjs}',
      'lint/**/*.mjs',
      'tests/**/*.{js,mjs,cjs}',
      'scripts/**/*.mjs',
      '../scripts/**/*.mjs',
      '../tests/**/*.mjs',
    ],
    extends: [eslint.configs.recommended],
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
  },
  {
    files: [
      'frontend/src/**/*.spec.ts',
      'src/**/*.spec.ts',
      'frontend/tests/**/*.{mjs,cjs}',
      'tests/**/*.{mjs,cjs}',
    ],
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.jasmine,
      },
    },
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
);
