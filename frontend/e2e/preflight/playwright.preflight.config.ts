import { defineConfig } from '@playwright/test';

/**
 * The preflight lane: proves the target is the disposable, expected-build, non-production world this
 * run prepared, with the platform owner and (phase `world`) every persona verified. See ./run.ts.
 *
 * retries:0 — a preflight that passes on the second attempt has not proved anything.
 */
export default defineConfig({
  testDir: '.',
  testMatch: /preflight\.setup\.ts/,
  retries: 0,
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  timeout: 600_000,
});
