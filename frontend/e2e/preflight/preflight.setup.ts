import { test } from '@playwright/test';
import { runPreflight, type Phase } from './run';

/**
 * Stand-alone entry point for the preflight, so CI can run it as its own named step BEFORE the
 * bootstrap and before any suite:
 *
 *   npx playwright test -c e2e/preflight/playwright.preflight.config.ts                       # target
 *   E2E_PREFLIGHT_PHASE=world npx playwright test -c e2e/preflight/playwright.preflight.config.ts
 *
 * A Playwright "test" only because that is the TypeScript runner the repo already has (the bootstrap
 * does the same). It needs no browser.
 */
const PHASES: Phase[] = ['target', 'world', 'lane'];
const requested = (process.env.E2E_PREFLIGHT_PHASE ?? 'target').trim() as Phase;

test(`e2e preflight (${requested})`, async () => {
  test.setTimeout(600_000);
  if (!PHASES.includes(requested)) {
    throw new Error(`E2E_PREFLIGHT_PHASE must be one of ${PHASES.join(', ')}; got '${requested}'.`);
  }
  await runPreflight(requested);
});
