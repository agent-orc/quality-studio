import eslint from '@eslint/js';
import angular from 'angular-eslint';
import globals from 'globals';
import tseslint from 'typescript-eslint';

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
    files: ['frontend/src/**/*.ts', 'src/**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    processor: angular.processInlineTemplates,
  },
  {
    files: ['frontend/src/**/*.html', 'src/**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
  },
  {
    files: [
      'frontend/**/*.{js,mjs,cjs}',
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
