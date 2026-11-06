import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

/**
 * Native window.confirm / window.prompt are gone (Screen Standard section 5): they are English-only, unstyled,
 * not RTL-aware and block the thread. Every confirmation goes through useConfirm; a prompt() is `ask` with a reason.
 * The ratchet metric is `nativeDialog` in scripts/design-token-ratchet.mjs; it is pinned at zero.
 */
const root = join(__dirname, '..');

test('no native confirm / prompt / alert dialogs remain in app/ or src/', async () => {
  const nativeImport = new Function('u', 'return import(u)') as (u: string) => Promise<{ count: () => Record<string, number> }>;
  const { count } = await nativeImport(pathToFileURL(join(root, 'scripts', 'design-token-ratchet.mjs')).href);
  expect(count().nativeDialog).toBe(0);
});

test('the ratchet baseline is pinned at zero so a native dialog can never come back', () => {
  const baseline = JSON.parse(readFileSync(join(root, 'scripts', 'design-token-baseline.json'), 'utf8')) as Record<string, number>;
  expect(baseline.nativeDialog).toBe(0);
});
