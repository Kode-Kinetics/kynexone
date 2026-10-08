import { expect, test, type Page } from '@playwright/test';

/**
 * HR's selfie review on the Attendance page, in a real browser against a mocked API (no stack needed):
 * the raw punch log offers "View selfie" only on punches the server says have a stored selfie
 * (hasSelfie), the dialog shows the JPEG fetched as a blob, and the object URL is revoked on close.
 *
 * Run: npx playwright test -c e2e/playwright.ess-workspace.config.ts attendance-selfie
 */

const today = new Date().toISOString().slice(0, 10);
const rows = [
  { id: 'raw-with-selfie', employeeId: 21, employeeCode: 'EMP-021', source: 'Mobile app punch', punchTimestampUtc: `${today}T05:02:00Z`, punchDirection: 'In', locationName: 'Mobile GPS', ipAddress: '', verificationMethod: 'Selfie+Geofence', isProcessed: false, createdAtUtc: `${today}T05:02:00Z`, hasSelfie: true },
  { id: 'raw-without-selfie', employeeId: 22, employeeCode: 'EMP-022', source: 'Web punch', punchTimestampUtc: `${today}T05:10:00Z`, punchDirection: 'In', locationName: '', ipAddress: '', verificationMethod: 'None', isProcessed: false, createdAtUtc: `${today}T05:10:00Z`, hasSelfie: false },
];

/** A real JPEG, drawn by the browser itself. */
async function makeJpeg(page: Page): Promise<Buffer> {
  await page.goto('about:blank');
  const dataUrl = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 48; canvas.height = 64;
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = '#b48c78'; ctx.fillRect(0, 0, 48, 64);
    return canvas.toDataURL('image/jpeg', 0.9);
  });
  return Buffer.from(dataUrl.split(',')[1], 'base64');
}

async function boot(page: Page, opts: { permissions: string[]; selfieStatus?: number }) {
  const jpeg = await makeJpeg(page);
  const selfieRequests: string[] = [];
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    // Record every object URL made and revoked, so the test can prove the face image is released on close.
    const w = window as unknown as { __created: string[]; __revoked: string[] };
    w.__created = []; w.__revoked = [];
    const create = URL.createObjectURL.bind(URL);
    const revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = (obj: Blob | MediaSource) => { const url = create(obj); w.__created.push(url); return url; };
    URL.revokeObjectURL = (url: string) => { w.__revoked.push(url); revoke(url); };
  });
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (path === '/api/auth/me') {
      return json({
        id: 'user-1', employeeId: 5, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Hala HR', roles: ['HR Manager'],
        permissions: opts.permissions,
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
      });
    }
    if (path === '/api/features/disabled-keys') return json([]);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/attendance/dashboard') return json({ date: today, activeEmployees: 2, present: 2, absent: 0, late: 0, missingPunch: 0, overtimeEmployees: 0, deviceErrors: 0, pendingRegularizations: 0 });
    if (path === '/api/attendance/events/raw') return json({ items: rows, total: rows.length, page: 1, pageSize: 50 });
    if (path.startsWith('/api/attendance/evidence/')) {
      selfieRequests.push(path);
      if (opts.selfieStatus && opts.selfieStatus !== 200) return route.fulfill({ status: opts.selfieStatus, json: { code: 'evidence_not_found', message: 'none' } });
      return route.fulfill({ status: 200, body: jpeg, headers: { 'content-type': 'image/jpeg', 'cache-control': 'no-store' } });
    }
    if (['/api/attendance/reports/payroll-summary', '/api/attendance/reports/device-sync', '/api/attendance/ai/insights'].includes(path)) return json([]);
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0, page: 1, pageSize: 50 });
  });
  await page.goto('/attendance');
  await page.getByRole('tab', { name: /Raw Punch Logs/ }).click();
  await expect(page.getByText('EMP-021')).toBeVisible({ timeout: 60_000 });
  return { selfieRequests };
}

test('"View selfie" appears only on a punch with a stored selfie; the image renders; its object URL is revoked on close', async ({ page }) => {
  const { selfieRequests } = await boot(page, { permissions: ['attendance.read', 'attendance.evidence.view'] });

  const withSelfie = page.getByRole('row').filter({ hasText: 'EMP-021' });
  const withoutSelfie = page.getByRole('row').filter({ hasText: 'EMP-022' });
  await expect(withSelfie.getByRole('button', { name: /^View selfie/ })).toBeVisible();
  await expect(withoutSelfie.getByRole('button', { name: /^View selfie/ })).toHaveCount(0);

  await withSelfie.getByRole('button', { name: /^View selfie/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('EMP-021');
  const image = dialog.getByRole('img', { name: 'Selfie of the employee' });
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth)).toBe(48);
  // Only this punch's selfie is fetched (the dev server's StrictMode may mount the dialog twice; production fetches once).
  expect([...new Set(selfieRequests)]).toEqual(['/api/attendance/evidence/raw-with-selfie/selfie']);
  const src = await image.getAttribute('src');
  expect(src).toMatch(/^blob:/);
  expect(await page.evaluate(() => (window as unknown as { __revoked: string[] }).__revoked)).not.toContain(src);

  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const { created, revoked } = await page.evaluate(() => {
    const w = window as unknown as { __created: string[]; __revoked: string[] };
    return { created: w.__created, revoked: w.__revoked };
  });
  expect(created).toContain(src);
  expect(revoked).toContain(src);
});

test('without the selfie-review permission there is no Selfie column and no button, even on a punch that has one', async ({ page }) => {
  await boot(page, { permissions: ['attendance.read'] });

  await expect(page.getByRole('button', { name: /^View selfie/ })).toHaveCount(0);
  await expect(page.getByRole('columnheader', { name: 'Selfie' })).toHaveCount(0);
});

test('a selfie deleted under retention says so in plain words, and Escape closes the dialog', async ({ page }) => {
  await boot(page, { permissions: ['attendance.read', 'attendance.evidence.view'], selfieStatus: 404 });

  await page.getByRole('row').filter({ hasText: 'EMP-021' }).getByRole('button', { name: /^View selfie/ }).click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('There is no stored selfie for this punch');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
