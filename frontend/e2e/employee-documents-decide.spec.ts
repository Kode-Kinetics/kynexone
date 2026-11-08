import { expect, test, type Page } from '@playwright/test';
import { bootEmployeeWorkspaceFixture, employees } from './employee-workspace.fixture';

/**
 * G-004: Verify and Reject on the employee's own Compliance & Documents section, in English and Arabic.
 * Verify asks to confirm; Reject needs a reason; the result (status and reason) is shown on the document; a server refusal
 * (the subject never decides) is shown in words. Every API call is intercepted; nothing is written.
 * Run: npx playwright test -c e2e/playwright.hotfix-dead-ends.config.ts
 */

const doc = (over: Record<string, unknown>) => ({
  id: 'd-1', employeeId: 901, documentType: 'Medical certificate', documentCategory: 'General', fileName: 'medical.pdf', contentType: 'application/pdf',
  storageUrl: 'x', isRequired: false, approvalStatus: 'Pending', versionNumber: 1, uploadedAtUtc: '2026-11-01T08:00:00Z', scanStatus: 'clean', isProtected: false, ...over,
});

async function open(page: Page, locale: 'en' | 'ar', opts: { decide?: (kind: 'verify' | 'reject', body: unknown) => { status: number; json: unknown }; permissions?: string[] } = {}) {
  await bootEmployeeWorkspaceFixture(page, { locale, permissions: opts.permissions });
  const posts: Array<{ kind: string; id: string; body: unknown }> = [];
  const documents = [
    doc({ id: 'd-1' }),
    doc({ id: 'd-2', documentType: 'Degree', approvalStatus: 'Rejected', rejectionReason: 'Not legible' }),
    doc({ id: 'd-3', documentType: 'Training record', approvalStatus: 'Verified' }),
    doc({ id: 'd-4', documentType: 'Passport', documentCategory: 'Identity', isProtected: true }),
  ];
  // Later routes win: the section reads the employee detail, and the two decision endpoints.
  await page.route('**/api/employees/901', async route => {
    if (route.request().method() !== 'GET') return route.fallback();
    return route.fulfill({ json: { ...employees[901], documents } });
  });
  await page.route(/\/api\/employees\/901\/documents\/[\w-]+\/(verify|reject)$/, async route => {
    const match = /documents\/([\w-]+)\/(verify|reject)$/.exec(new URL(route.request().url()).pathname)!;
    const kind = match[2] as 'verify' | 'reject';
    const body = route.request().postDataJSON();
    posts.push({ kind, id: match[1], body });
    const custom = opts.decide?.(kind, body);
    if (custom) return route.fulfill({ status: custom.status, json: custom.json });
    const base = documents.find(d => d.id === match[1])!;
    return route.fulfill({ json: kind === 'verify'
      ? { ...base, approvalStatus: 'Verified', rejectionReason: null }
      : { ...base, approvalStatus: 'Rejected', rejectionReason: (body as { reason: string }).reason } });
  });
  await page.goto('/people/901?section=compliance-documents');
  return posts;
}

const L = {
  en: { verify: 'Verify document', reject: 'Reject document', reasonLabel: 'Reason the employee will read', verified: 'Verified by HR', rejected: 'Rejected by HR', reason: (r: string) => `Reason: ${r}`, confirmTitle: 'Verify this document?', cancel: 'Cancel' },
  ar: { verify: 'التحقق من المستند', reject: 'رفض المستند', reasonLabel: 'السبب الذي سيقرؤه الموظف', verified: 'موثّق من الموارد البشرية', rejected: 'مرفوض من الموارد البشرية', reason: (r: string) => `السبب: ${r}`, confirmTitle: 'هل تتحقق من هذا المستند؟', cancel: 'إلغاء' },
} as const;

for (const locale of ['en', 'ar'] as const) {
  const w = L[locale];
  test.describe(`employee record, documents (${locale})`, () => {
    test('only a pending, unprotected-or-permitted document offers the buttons; others show their status and reason', async ({ page }) => {
      await open(page, locale);
      await expect(page.getByTestId('doc-status-d-1')).toBeVisible({ timeout: 60_000 });
      await expect(page.getByTestId('doc-status-d-2')).toHaveText(w.rejected);
      await expect(page.getByTestId('doc-reason-d-2')).toContainText(w.reason('Not legible'));
      await expect(page.getByTestId('doc-status-d-3')).toHaveText(w.verified);
      // Pending: two buttons for d-1 and d-4 (this reviewer holds employees.sensitive); none for the rejected or verified ones.
      await expect(page.getByRole('button', { name: w.verify, exact: true })).toHaveCount(2);
      await expect(page.getByRole('button', { name: w.reject, exact: true })).toHaveCount(2);
    });

    test('Verify confirms first, posts once, and the document then reads Verified', async ({ page }) => {
      const posts = await open(page, locale);
      await expect(page.getByTestId('doc-status-d-1')).toBeVisible({ timeout: 60_000 });
      await page.getByRole('button', { name: w.verify, exact: true }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText(w.confirmTitle);
      expect(posts).toHaveLength(0);
      await dialog.getByRole('button', { name: w.verify, exact: true }).click();
      await expect(page.getByTestId('doc-status-d-1')).toHaveText(w.verified);
      expect(posts).toEqual([{ kind: 'verify', id: 'd-1', body: { notes: null } }]);
    });

    test('Reject needs a reason, then shows the reason on the document', async ({ page }) => {
      const posts = await open(page, locale);
      await expect(page.getByTestId('doc-status-d-1')).toBeVisible({ timeout: 60_000 });
      await page.getByRole('button', { name: w.reject, exact: true }).first().click();
      const dialog = page.getByRole('dialog');
      const confirm = dialog.getByRole('button', { name: w.reject, exact: true });
      await expect(confirm).toBeDisabled();
      await dialog.getByLabel(w.reasonLabel).fill('ab');
      await expect(confirm).toBeDisabled();
      await dialog.getByLabel(w.reasonLabel).fill('Cut off on the right');
      await confirm.click();
      await expect(page.getByTestId('doc-status-d-1')).toHaveText(w.rejected);
      await expect(page.getByTestId('doc-reason-d-1')).toContainText(w.reason('Cut off on the right'));
      expect(posts).toEqual([{ kind: 'reject', id: 'd-1', body: { reason: 'Cut off on the right' } }]);
    });
  });
}

test('the subject-never-decides refusal is shown and the document stays pending', async ({ page }) => {
  await open(page, 'en', { decide: () => ({ status: 403, json: { code: 'document_self_verification', message: 'You cannot verify or reject this document: it is your own, or you uploaded it. Someone else in HR must decide.' } }) });
  await expect(page.getByTestId('doc-status-d-1')).toBeVisible({ timeout: 60_000 });
  await page.getByRole('button', { name: 'Verify document', exact: true }).first().click();
  await page.getByRole('dialog').getByRole('button', { name: 'Verify document', exact: true }).click();
  await expect(page.locator('p[role="alert"]')).toContainText('Someone else in HR must decide');
  await expect(page.getByTestId('doc-status-d-1')).toHaveText('Waiting for HR to verify');
});

test('a reviewer without employees.sensitive is not offered the buttons on a protected document', async ({ page }) => {
  await open(page, 'en', { permissions: ['employees.read', 'employees.documents', 'dashboard.read'] });
  await expect(page.getByTestId('doc-status-d-1')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('button', { name: 'Verify document', exact: true })).toHaveCount(1);
});
