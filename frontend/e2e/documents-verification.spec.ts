import { expect, test, type Page, type Request } from '@playwright/test';

/**
 * G-004: HR verifies or rejects the documents employees upload, and the employee reads the outcome.
 *   Compliance → Employee Documents → To verify: the queue, Verify (confirm), Reject (a reason is required).
 *   Self-Service → My letters: "Documents I gave HR" with the status and HR's reason.
 * Every API call is intercepted; nothing is written. Run: npx playwright test -c e2e/playwright.hotfix-dead-ends.config.ts
 */

const ROWS = [
  { documentId: 'd-1', employeeId: 17, employeeCode: 'EMP-0017', employeeName: 'Amira Mansour', companyId: 'c1', documentType: 'Medical certificate', documentCategory: 'General', fileName: 'medical.pdf', uploadedAtUtc: '2026-11-01T08:00:00Z', expiryDate: null, scanStatus: 'clean', isProtected: false },
  { documentId: 'd-2', employeeId: 18, employeeCode: 'EMP-0018', employeeName: 'Omar Haddad', companyId: 'c1', documentType: 'Degree', documentCategory: 'Education', fileName: 'degree.pdf', uploadedAtUtc: '2026-11-03T08:00:00Z', expiryDate: null, scanStatus: 'clean', isProtected: false },
];

interface Boot { permissions: string[]; locale?: 'en' | 'ar'; pending?: typeof ROWS; essDocuments?: unknown[] }

async function boot(page: Page, url: string, opts: Boot) {
  const errors: string[] = [];
  const posts: Array<{ path: string; body: unknown }> = [];
  let pending = [...(opts.pending ?? ROWS)];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript((locale) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, opts.locale ?? 'en');
  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === '/api/auth/me') return json({
      id: 'hr-1', employeeId: 17, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Huda HR', roles: ['HR Manager'], permissions: opts.permissions,
      companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path.startsWith('/api/employees/reports/')) return json([]);
    if (path === '/api/employees/documents/pending-verification') return json({ items: pending, total: pending.length, page: 1, pageSize: 25 });
    const decide = /^\/api\/employees\/(\d+)\/documents\/([\w-]+)\/(verify|reject)$/.exec(path);
    if (decide && request.method() === 'POST') {
      posts.push({ path, body: request.postDataJSON() });
      pending = pending.filter(r => r.documentId !== decide[2]);
      return json({ id: decide[2], approvalStatus: decide[3] === 'verify' ? 'Verified' : 'Rejected' });
    }
    if (path === '/api/ess/documents') return json(opts.essDocuments ?? []);
    if (path === '/api/ess/document-requests/types') return json([]);
    if (path === '/api/ess/document-requests') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0 });
  });
  await page.goto(url);
  return { errors, posts };
}

test.describe('Documents to verify (HR)', () => {
  test('the queue lists what is waiting, oldest first; Verify asks to confirm and then posts', async ({ page }) => {
    const { errors, posts } = await boot(page, '/compliance?tab=employee-documents', { permissions: ['compliance.read', 'compliance.write', 'employees.read', 'employees.documents', 'dashboard.read'] });
    await page.getByRole('button', { name: 'To verify', exact: true }).click({ timeout: 60_000 });
    const queue = page.getByTestId('documents-to-verify');
    await expect(queue).toContainText('2 documents are waiting');
    await expect(queue.locator('tbody tr')).toHaveCount(2);
    await expect(queue.locator('tbody tr').first()).toContainText('Amira Mansour');
    await expect(queue.getByTestId('pending-doc-d-1')).toContainText('Medical certificate');

    await queue.getByTestId('pending-doc-d-1').getByRole('button', { name: 'Verify document' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Verify this document?');
    expect(posts).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Verify document' }).click();
    await expect(queue.locator('tbody tr')).toHaveCount(1);
    expect(posts).toEqual([{ path: '/api/employees/17/documents/d-1/verify', body: { notes: null } }]);
    expect(errors).toEqual([]);
  });

  test('Reject needs a reason the employee will read', async ({ page }) => {
    const { posts } = await boot(page, '/compliance?tab=employee-documents', { permissions: ['compliance.read', 'employees.read', 'employees.documents'] });
    await page.getByRole('button', { name: 'To verify', exact: true }).click({ timeout: 60_000 });
    await page.getByTestId('pending-doc-d-2').getByRole('button', { name: 'Reject document' }).click();
    const dialog = page.getByRole('dialog');
    const confirm = dialog.getByRole('button', { name: 'Reject document' });
    await expect(confirm).toBeDisabled();
    await dialog.getByLabel('Reason the employee will read').fill('ab');
    await expect(confirm).toBeDisabled();
    await dialog.getByLabel('Reason the employee will read').fill('The scan is cut off on the right.');
    await confirm.click();
    await expect(page.getByTestId('pending-doc-d-2')).toHaveCount(0);
    expect(posts).toEqual([{ path: '/api/employees/18/documents/d-2/reject', body: { reason: 'The scan is cut off on the right.' } }]);
  });

  test('a caller without employees.documents is not offered the queue', async ({ page }) => {
    await boot(page, '/compliance?tab=employee-documents', { permissions: ['compliance.read', 'employees.read'] });
    await expect(page.getByRole('button', { name: 'Expiring', exact: true })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('button', { name: 'To verify', exact: true })).toHaveCount(0);
  });

  test('the queue in Arabic reads right to left with Arabic actions', async ({ page }) => {
    await boot(page, '/compliance?tab=employee-documents', { permissions: ['compliance.read', 'employees.read', 'employees.documents'], locale: 'ar' });
    await page.getByRole('button', { name: 'للتحقق', exact: true }).click({ timeout: 60_000 });
    await expect(page.getByTestId('documents-to-verify')).toContainText('التحقق من المستند');
    await expect(page.getByTestId('documents-to-verify')).toContainText('رفض المستند');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  });
});

test.describe('Documents I gave HR (employee)', () => {
  const documents = [
    { id: 'e-1', documentType: 'Degree', fileName: 'degree.pdf', approvalStatus: 'Verified', rejectionReason: null },
    { id: 'e-2', documentType: 'Medical certificate', fileName: 'medical.pdf', approvalStatus: 'Rejected', rejectionReason: 'The scan is cut off on the right.' },
    { id: 'e-3', documentType: 'Passport', fileName: 'passport.pdf', approvalStatus: 'Pending', rejectionReason: null },
  ];

  test('each document shows where it stands, and a rejected one says why and what to do', async ({ page }) => {
    const { errors } = await boot(page, '/ess/documents', { permissions: ['ess.read', 'ess.write', 'dashboard.read'], essDocuments: documents });
    const card = page.getByTestId('ess-my-documents');
    await expect(card).toContainText('Documents I gave HR', { timeout: 60_000 });
    await expect(card.getByTestId('ess-doc-e-1')).toContainText('Verified by HR');
    await expect(card.getByTestId('ess-doc-e-3')).toContainText('Waiting for HR to verify');
    await expect(card.getByTestId('ess-doc-e-2')).toContainText('Rejected by HR');
    await expect(card.getByTestId('ess-doc-reason-e-2')).toContainText('Reason: The scan is cut off on the right.');
    await expect(card.getByTestId('ess-doc-reason-e-2')).toContainText('Upload a corrected copy under My employee details.');
    await expect(card.getByTestId('ess-doc-reason-e-1')).toHaveCount(0);
    expect(errors).toEqual([]);
  });

  test('in Arabic the statuses and the reason label are Arabic', async ({ page }) => {
    await boot(page, '/ess/documents', { permissions: ['ess.read', 'ess.write', 'dashboard.read'], essDocuments: documents, locale: 'ar' });
    const card = page.getByTestId('ess-my-documents');
    await expect(card.getByTestId('ess-doc-e-2')).toContainText('مرفوض من الموارد البشرية', { timeout: 60_000 });
    await expect(card.getByTestId('ess-doc-reason-e-2')).toContainText('السبب: The scan is cut off on the right.');
    await expect(card.getByTestId('ess-doc-e-1')).toContainText('موثّق من الموارد البشرية');
  });

  test('a failed read says so and offers a retry, never an empty list', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'fixture'));
    await page.route('**/api/**', async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/auth/me') return route.fulfill({ json: { id: 'u', employeeId: 17, tenantId: 't', tenantSlug: 'f', fullName: 'Amira', roles: ['Employee'], permissions: ['ess.read', 'ess.write'], companies: [{ id: 'c1', name: 'A', code: 'A', countryCode: 'SA', isActive: true }] } });
      if (path === '/api/ess/documents') return route.fulfill({ status: 503, json: { message: 'down' } });
      if (path === '/api/features/disabled-keys') return route.fulfill({ json: [] });
      if (path === '/api/ess/document-requests/types' || path === '/api/ess/document-requests' || path === '/api/features/modules' || path === '/api/notifications') return route.fulfill({ json: [] });
      return route.fulfill({ json: { items: [], total: 0 } });
    });
    await page.goto('/ess/documents');
    const card = page.getByTestId('ess-my-documents');
    await expect(card.getByRole('button', { name: /try again|retry/i })).toBeVisible({ timeout: 60_000 });
    await expect(card).not.toContainText('You have not given HR any documents yet.');
  });
});
