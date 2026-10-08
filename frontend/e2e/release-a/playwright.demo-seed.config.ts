import { defineConfig } from '@playwright/test';

/**
 * The Masar Holding demo seed. No browser, no global setup (the e2e preflight would refuse a stack
 * the seed has not provisioned yet), no retries: re-running is what the seed's idempotency is for.
 */
export default defineConfig({
  testDir: '.',
  testMatch: /demo-seed\.setup\.ts/,
  retries: 0,
  workers: 1,
  reporter: 'list',
  timeout: 900_000,
});
