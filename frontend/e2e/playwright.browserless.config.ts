import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: /ui-truthfulness-state\.spec\.ts/,
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
});
