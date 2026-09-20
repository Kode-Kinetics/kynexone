import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  // Source lints that need neither a browser nor the docker stack. rtl-logical-properties
  // is the RTL ratchet: it reads app/ and src/ and fails on a physical direction utility
  // that is not on its documented allow-list.
  testMatch: /(ui-truthfulness-state|rtl-logical-properties)\.spec\.ts/,
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
});
