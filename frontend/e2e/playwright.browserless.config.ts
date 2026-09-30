import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  // Source lints that need neither a browser nor the docker stack. rtl-logical-properties
  // is the RTL ratchet: it reads app/ and src/ and fails on a physical direction utility
  // that is not on its documented allow-list. preflight-rules and identity-contract prove the
  // e2e preflight's refusals and that the role matrix is generated from (and agrees with)
  // backend AuthSeeder.cs — register item F07.
  testMatch: /(ui-truthfulness-state|rtl-logical-properties|employee-create-country-gate|recruitment-journey-actions|offer-placement|toast-context-stability|calendar-date|new-hire-review|performance-access|paging-truthfulness|preflight-rules|identity-contract)\.spec\.ts/,
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
});
