import { test as setup } from '@playwright/test';
import { seedMasarDemo } from './demo-seed';

/**
 * Entry point for the Masar Holding demo seed (Release A, R7 phase 1). A Playwright "test" only so it
 * runs on the toolchain the repo already has, exactly like e2e/bootstrap/bootstrap.setup.ts:
 *
 *   cd frontend && DEMO_API_BASE_URL=http://localhost:5117 \
 *     PLATFORM_ADMIN_EMAIL=... PLATFORM_ADMIN_PASSWORD=... \
 *     npx playwright test -c e2e/release-a/playwright.demo-seed.config.ts
 */
setup('seed the Masar Holding demo company through the public API', async () => {
  setup.setTimeout(900_000);
  await seedMasarDemo();
});
