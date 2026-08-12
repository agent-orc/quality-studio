import eslint from '@eslint/js';
import angular from 'angular-eslint';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const warningsOnly = (configs) =>
  configs.map((config) => ({
    ...config,
    rules: Object.fromEntries(
      Object.entries(config.rules ?? {}).map(([rule, setting]) => {
        const severity = Array.isArray(setting) ? setting[0] : setting;
        return [
          rule,
          severity === 'off' || severity === 0
            ? setting
            : Array.isArray(setting)
              ? ['warn', ...setting.slice(1)]
              : 'warn',
        ];
      }),
    ),
  }));

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
    extends: warningsOnly([
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
    ]),
    languageOptions: {
      globals: globals.browser,
    },
    processor: angular.processInlineTemplates,
  },
  {
    files: ['frontend/src/**/*.html', 'src/**/*.html'],
    extends: warningsOnly([
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ]),
  },
  {
    files: [
      'frontend/**/*.{js,mjs,cjs}',
      'tests/**/*.{js,mjs,cjs}',
      'scripts/**/*.mjs',
      '../scripts/**/*.mjs',
      '../tests/**/*.mjs',
    ],
    extends: warningsOnly([eslint.configs.recommended]),
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
