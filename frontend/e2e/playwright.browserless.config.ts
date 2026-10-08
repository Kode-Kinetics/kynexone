import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  // Source lints that need neither a browser nor the docker stack. rtl-logical-properties
  // is the RTL ratchet: it reads app/ and src/ and fails on a physical direction utility
  // that is not on its documented allow-list. preflight-rules and identity-contract prove the
  // e2e preflight's refusals and that the role matrix is generated from (and agrees with)
  // backend AuthSeeder.cs — register item F07. evidence-rules is the same idea for the evidence
  // harness (F12): it proves the redaction still masks personal data, still leaves record ids and
  // money alone, and that a step with no image is printed as NO CAPTURE rather than left blank.
  // i18n-coverage is the Arabic ratchet: every t() key exists in en and ar, no new sentence
  // fragments, and per-file counts of hard-coded strings may only go down (e2e/i18n-baseline.json).
  testMatch: /(employee-edit-draft|ui-truthfulness-state|rtl-logical-properties|i18n-coverage|employee-create-country-gate|recruitment-journey-actions|offer-placement|toast-context-stability|calendar-date|new-hire-review|performance-access|paging-truthfulness|list-screens-paging|preflight-rules|identity-contract|evidence-rules)\.spec\.ts/,
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
});
