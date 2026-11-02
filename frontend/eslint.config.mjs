// Flat ESLint config. Strategy: the rule set is strict, the LEGACY violations are frozen in
// eslint-baseline.json (scripts/lint-ratchet.mjs), and any NEW violation of any rule fails CI.
// Counts per (file, rule) may only go down. Re-pin after fixing: npm run lint:repin
import { FlatCompat } from '@eslint/eslintrc';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const compat = new FlatCompat({ baseDirectory: dirname(fileURLToPath(import.meta.url)) });

const config = [
  { linterOptions: { reportUnusedDisableDirectives: 'off' } },
  { ignores: ['.next/**', 'dist/**', 'node_modules/**', 'e2e/**', 'unit/**', 'scripts/**', 'next-env.d.ts', '*.config.*'] },
  ...compat.extends('next/core-web-vitals', 'next/typescript'),
  {
    rules: {
      // Native blocking dialogs: use the app's dialog components (confirm/feedback), never the browser's.
      'no-restricted-globals': [
        'error',
        { name: 'confirm', message: 'Use the in-app confirm dialog, not window.confirm.' },
        { name: 'alert', message: 'Use the in-app toast/dialog, not window.alert.' },
        { name: 'prompt', message: 'Use an in-app form dialog, not window.prompt.' },
      ],
      'no-restricted-properties': [
        'error',
        { object: 'window', property: 'confirm', message: 'Use the in-app confirm dialog.' },
        { object: 'window', property: 'alert', message: 'Use the in-app toast/dialog.' },
        { object: 'window', property: 'prompt', message: 'Use an in-app form dialog.' },
      ],
      // All HTTP goes through the API client (src/api/client.ts): auth, tenant headers, error envelope.
      'no-restricted-imports': [
        'error',
        { paths: [{ name: 'axios', message: 'Import the shared API client from src/api, not axios directly.' }] },
      ],
      // Accessibility basics, as errors so new code cannot add violations.
      'jsx-a11y/alt-text': 'error',
      'jsx-a11y/aria-props': 'error',
      'jsx-a11y/aria-role': 'error',
      'jsx-a11y/role-has-required-aria-props': 'error',
      'jsx-a11y/anchor-has-content': 'error',
      'jsx-a11y/click-events-have-key-events': 'error',
      'jsx-a11y/label-has-associated-control': 'error',
      'jsx-a11y/no-static-element-interactions': 'error',
    },
  },
  // The API client module is the one place allowed to talk to axios.
  { files: ['src/api/**'], rules: { 'no-restricted-imports': 'off' } },
];

export default config;
