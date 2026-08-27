// @ts-check
import eslint from '@eslint/js';
import tseslint from 'typescript-eslint';
import angular from 'angular-eslint';
import eslintConfigPrettier from 'eslint-config-prettier';
import globals from 'globals';

export default tseslint.config(
  {
    ignores: ['dist/**', '.angular/**', 'coverage/**', 'node_modules/**'],
  },
  {
    files: ['src/**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      // The repository's established component prefix is "qs" (angular.json's "app"
      // schematics default only applies to the CLI-scaffolded root component).
      '@angular-eslint/directive-selector': ['error', { type: 'attribute', prefix: ['app', 'qs'], style: 'camelCase' }],
      '@angular-eslint/component-selector': ['error', { type: 'element', prefix: ['app', 'qs'], style: 'kebab-case' }],
    },
  },
  {
    files: ['src/**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {},
  },
  {
    // Evidence/perf scripts run under Node but pass callbacks into a real browser
    // via Playwright's page.evaluate/addInitScript, so both global sets are in play.
    files: ['tests/**/*.mjs'],
    extends: [eslint.configs.recommended],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.node, ...globals.browser },
    },
  },
  eslintConfigPrettier,
);
