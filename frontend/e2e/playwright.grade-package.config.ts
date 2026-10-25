import { defineConfig, devices } from '@playwright/test';

// Grade pay packages (Setup → Grades editor, Payroll → Grade packages): every API call is intercepted by the spec.
// Start the frontend separately (GRADE_PACKAGE_BASE_URL, e.g. `next dev -p 5174`) — this suite writes nothing.
export default defineConfig({
  testDir: '.', testMatch: /grade-package\.spec\.ts/, workers: 1, retries: 0, timeout: 90_000,
  forbidOnly: !!process.env.CI,
  reporter: 'list', outputDir: '/tmp/kynex-grade-package-results',
  use: { baseURL: process.env.GRADE_PACKAGE_BASE_URL || 'http://127.0.0.1:5174', actionTimeout: 15_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
