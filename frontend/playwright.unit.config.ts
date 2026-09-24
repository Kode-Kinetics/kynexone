import { defineConfig } from '@playwright/test';

/**
 * Pure-function unit tests — no browser, no server, no docker-compose stack.
 *
 * The e2e config deliberately hard-fails in globalSetup unless four services and seeded data are
 * up, which is correct for e2e and useless for a formatting helper. These run anywhere, in about a
 * second, so a "does this render 100% when there is nothing to measure?" regression is caught on a
 * laptop rather than in a CI lane that needs the whole stack.
 *
 *   npx playwright test -c playwright.unit.config.ts
 */
export default defineConfig({
  testDir: './unit',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: 'list',
});
