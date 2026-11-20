import { expect, test } from '@playwright/test';
import { ADMIN, boot } from '../setup-consolidation/fixture';

/**
 * Starting two-step sign-in asks the server for a setup secret once. Two quick clicks on "Turn on two-step
 * sign-in" (and a development re-mount of the panel) must still send exactly one POST /api/auth/mfa/setup.
 * Every API call is intercepted: nothing real is read or changed.
 *
 *   UX_FIXES_BASE_URL=http://127.0.0.1:<port> npx playwright test -c e2e/playwright.ux-fixes.config.ts two-step-setup-single-request
 */
const URI = 'otpauth://totp/KynexOne:reviewer?secret=JBSWY3DPEHPK3PXP&issuer=KynexOne';
const STATUS = {
  enabled: false, required: false, requiredBecause: null, enforceFromUtc: null, enforced: false, promptToEnroll: false,
  canDisable: false, rememberDeviceDays: 30, trustedDeviceCount: 0,
};

const open = async (page: Parameters<typeof boot>[0], setups: string[]) => {
  await boot(page, {
    persona: ADMIN,
    url: '/account/security',
    mocks: {
      '/api/auth/mfa/status': STATUS,
      '/api/auth/mfa/trusted-devices': [],
      '/api/auth/mfa/setup': (_u: URL, method: string) => { if (method === 'POST') setups.push('setup'); return { provisioningUri: URI }; },
    },
  });
};

test('two rapid clicks on "Turn on two-step sign-in" send exactly one setup request', async ({ page }) => {
  const setups: string[] = [];
  await open(page, setups);
  const turnOn = page.getByTestId('two-step-turn-on');
  await expect(turnOn).toBeVisible({ timeout: 30_000 });
  await turnOn.dblclick();
  await expect(page.getByTestId('two-step-key')).toContainText('JBSW', { timeout: 15_000 });
  // Let any second request land before counting.
  await page.waitForTimeout(500);
  expect(setups).toHaveLength(1);
});

test('the setup request is not repeated when the panel stays open (single click)', async ({ page }) => {
  const setups: string[] = [];
  await open(page, setups);
  await page.getByTestId('two-step-turn-on').click();
  await expect(page.getByTestId('two-step-key')).toContainText('JBSW', { timeout: 15_000 });
  await page.waitForTimeout(500);
  expect(setups).toHaveLength(1);
});
