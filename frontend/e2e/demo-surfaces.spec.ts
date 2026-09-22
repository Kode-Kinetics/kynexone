import { test, expect } from '@playwright/test';
import {
  apiLogin, tenantLogin, mainText,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

/**
 * WAVE 5 — the surfaces that shipped this week and had never been opened in a browser:
 * HR letters, timesheets, the Nitaqat panel, report export, and the ESS document request.
 *
 * WHAT THIS FILE IS FOR. `tsc` and `next build` were both clean for all of it, so the only
 * thing left to find was what happens when someone actually clicks. Every assertion here is on
 * SPECIFIC content or a SPECIFIC artefact, never on "the page rendered something": the
 * failure this suite exists to catch is a screen that paints its shell and its empty state
 * while the feature behind it does nothing, and a length check cannot tell those apart. See the
 * "Honest render assertions" block in ./helpers.ts.
 *
 * THESE TESTS FAIL WHEN THE STACK IS OLDER THAN THE CODE, AND THAT IS DELIBERATE. `/hr-letters`,
 * `/timesheets` and `/opening-balances` do not exist in a frontend image built before they
 * merged, and their API routes 404 against an older backend. A red run here means the stack
 * under test is stale — which is exactly the thing that would otherwise be discovered by a
 * client clicking a nav item and getting a 404 page.
 */

// ── HR letters ────────────────────────────────────────────────────────────────────────────────

test.describe('HR letters', () => {
  test('the letter catalogue is configured, not just present', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/hr-letters/types', { headers: { Authorization: `Bearer ${token}` } });
    expect(resp.status()).toBe(200);

    const types = await resp.json() as Array<{ letterType: string; isConfigured: boolean; nameAr: string }>;
    expect(types.length).toBeGreaterThan(0);

    const salary = types.find((t) => t.letterType === 'SalaryCertificate');
    expect(salary, 'the salary certificate is the letter every demo asks for').toBeDefined();
    // The Arabic name is what proves this is the bilingual catalogue and not an English stub.
    expect(salary!.nameAr).toMatch(/[؀-ۿ]/);

    // isConfigured:false for everything means the tenant has NO templates — the Issue tab then
    // renders a dead end telling the user to go and restore the defaults. That is the state a
    // freshly seeded demo tenant is in, and it is a demo blocker, not an empty list.
    expect(
      types.some((t) => t.isConfigured),
      'no letter type has a template on this tenant: POST /api/hr-letters/templates/seed-defaults ' +
      'must run as part of demo preparation, or the Issue a Letter tab cannot be used at all',
    ).toBe(true);
  });

  test('issuing a salary certificate returns a real PDF with a quotable reference', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const auth = { Authorization: `Bearer ${token}` };

    const employees = await request.get('/api/employees?pageSize=1&status=Active', { headers: auth });
    expect(employees.status()).toBe(200);
    const employeeId = (await employees.json()).items?.[0]?.id;
    expect(employeeId, 'an active employee is required to issue a certificate').toBeTruthy();

    const issued = await request.post('/api/hr-letters/issue', {
      headers: auth,
      data: {
        employeeId,
        letterType: 'SalaryCertificate',
        language: 'bilingual',
        purpose: 'an end-to-end verification run',
        addresseeName: 'Riyad Bank',
      },
    });
    expect(issued.status()).toBe(200);
    expect(issued.headers()['content-type']).toContain('application/pdf');

    // A bank quotes this number back. If it is absent the register cannot be searched by it.
    const reference = issued.headers()['x-letter-reference'];
    expect(reference, 'every issuance must carry a reference').toMatch(/^SAL-CERT-\d{4}-\d{5}$/);

    const pdf = await issued.body();
    // Magic bytes, not just a non-zero length: an HTML error page is also non-zero.
    expect(pdf.subarray(0, 5).toString('latin1')).toBe('%PDF-');
    // A bilingual certificate embeds an Arabic face. QuestPDF host-font fallback is off
    // (DocumentFonts), so if the embedded Noto subset were missing the Arabic would be tofu.
    expect(
      pdf.toString('latin1').includes('NotoSansArabic'),
      'the bilingual PDF must embed Noto Sans Arabic — without it every Arabic glyph is a box',
    ).toBe(true);

    // The issuance must be findable afterwards; an unrecorded certificate is a compliance hole.
    const register = await request.get(
      `/api/hr-letters/register?reference=${encodeURIComponent(reference)}`, { headers: auth });
    expect(register.status()).toBe(200);
    const page = await register.json();
    expect(page.total, `the register must contain ${reference} immediately after issuing it`).toBeGreaterThan(0);
    expect(page.items[0].referenceNumber).toBe(reference);
    expect(page.items[0].fileHash, 'the register stores a content hash for each PDF').toMatch(/^[0-9a-f]{64}$/);
  });

  test('the HR letters screen shows its four working areas', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/hr-letters');
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    const text = await mainText(page);
    for (const tab of ['Employee Requests', 'Issue a Letter', 'Issued Register', 'Templates']) {
      expect(text, `the ${tab} area must be reachable from /hr-letters`).toContain(tab);
    }
  });
});

// ── ESS document request → HR issuance ────────────────────────────────────────────────────────

test('an employee request becomes an issued letter the employee can download', async ({ request }) => {
  const empToken = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
  const hrToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
  const asEmployee = { Authorization: `Bearer ${empToken}` };
  const asHr = { Authorization: `Bearer ${hrToken}` };

  const types = await request.get('/api/ess/document-requests/types', { headers: asEmployee });
  expect(types.status()).toBe(200);
  // An empty list here is the ESS "Request a Document" card rendering a dropdown with nothing
  // in it — the card looks live and cannot be used.
  expect((await types.json()).length, 'self-service must offer at least one requestable document').toBeGreaterThan(0);

  const created = await request.post('/api/ess/document-requests', {
    headers: asEmployee,
    data: {
      letterType: 'SalaryCertificate', language: 'bilingual',
      purpose: 'an end-to-end verification run', addresseeName: 'Al Rajhi Bank',
    },
  });
  expect(created.status()).toBe(201);
  const requestId = (await created.json()).id;

  // HR must actually see it — a request that never reaches the queue is invisible work.
  const queue = await request.get('/api/hr-letters/requests?status=Pending', { headers: asHr });
  expect(queue.status()).toBe(200);
  expect(
    (await queue.json()).items.some((r: { id: string }) => r.id === requestId),
    'the new request must appear in the HR pending queue',
  ).toBe(true);

  const issued = await request.post(`/api/hr-letters/requests/${requestId}/issue`, { headers: asHr, data: {} });
  expect(issued.status()).toBe(200);
  expect(issued.headers()['x-letter-reference']).toBeTruthy();

  const download = await request.get(`/api/ess/document-requests/${requestId}/pdf`, { headers: asEmployee });
  expect(download.status()).toBe(200);
  expect((await download.body()).subarray(0, 5).toString('latin1')).toBe('%PDF-');
});

// ── Timesheets ────────────────────────────────────────────────────────────────────────────────

test.describe('Timesheets', () => {
  test('a week can be entered AND submitted', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const auth = { Authorization: `Bearer ${token}` };

    const current = await request.get('/api/ess/timesheets/current', { headers: auth });
    expect(current.status()).toBe(200);
    const sheet = await current.json();
    expect(sheet.days, 'the week grid must offer seven days to fill in').toHaveLength(7);

    // This week may already have been submitted — by an earlier run of this spec, or by a demo.
    // A submitted week is itself proof the path works, so assert that instead of trying to edit a
    // locked sheet. The Draft branch below is the one that catches a broken submit, and a tenant
    // where submission is impossible can only ever be in that branch.
    if (!sheet.isEditable) {
      expect(
        ['Submitted', 'Approved', 'Rejected'],
        'a non-editable week must be locked because it reached a decision state, not for some other reason',
      ).toContain(sheet.status);
      expect(sheet.totalMinutes, 'a submitted week must carry the hours that were entered').toBeGreaterThan(0);
      return;
    }

    expect(sheet.status).toBe('Draft');
    const minutes = 7 * 60;
    const saved = await request.put(`/api/ess/timesheets/${sheet.id}/entries`, {
      headers: auth,
      data: {
        entries: [{ workDate: sheet.periodStart, minutes, notes: 'e2e verification' }],
        version: sheet.version,
      },
    });
    expect(saved.status()).toBe(200);
    expect((await saved.json()).totalMinutes).toBe(minutes);

    // THE POINT OF THIS TEST. Saving is not the feature; submitting is. Submit 422s with
    // `no_approval_route` on any tenant that has no ApprovalWorkflow for entity 'Timesheet'.
    // TenantProvisioningBundle seeds one for newly provisioned tenants, but the demo seeders
    // (e2e/bootstrap/provision.ts) provisions only LEAVE-APPROVAL — so on the fixture
    // tenants the grid fills in and the Submit button can never succeed.
    const submitted = await request.post(`/api/ess/timesheets/${sheet.id}/submit`, { headers: auth });
    if (submitted.status() === 422) {
      const body = await submitted.json();
      expect(
        body.code,
        'timesheet submission is blocked. If this is `no_approval_route`, the tenant is missing an ' +
        'active ApprovalWorkflow for entity "Timesheet" — seed one as part of demo preparation.',
      ).not.toBe('no_approval_route');
    }
    expect(submitted.status()).toBe(200);
    expect((await submitted.json()).status).toBe('Submitted');
  });

  test('the weekly grid renders seven named days', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/timesheets');
    await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => {});

    const text = await mainText(page);
    // The Saudi working week starts on Sunday; all seven must be offered for entry.
    for (const day of ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']) {
      expect(text, `the week grid must show ${day}`).toContain(day);
    }
    expect(text).toContain('Submit for approval');
  });
});

// ── Nitaqat ───────────────────────────────────────────────────────────────────────────────────

test.describe('Nitaqat', () => {
  /**
   * REGRESSION GUARD. NitaqatReferenceSeeder commits tiers, weight rules, activities and the
   * illustrative grid in ONE SaveChangesAsync. A single over-length SourceNote therefore rolls
   * back ALL of it (Postgres 22001), Program.cs logs "Seeder failed — continuing startup", and
   * the API boots healthy with an empty catalogue. The panel then shows
   * `nitaqat_activity_not_configured` and tells the user to pick an activity from a dropdown
   * that has nothing in it. This asserts the catalogue is actually populated.
   */
  test('the economic-activity catalogue is populated', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat/activities', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);

    const activities = await resp.json() as Array<{ code: string; nameEn: string; nameAr: string }>;
    expect(
      activities.length,
      'the Nitaqat activity catalogue is EMPTY. The Saudization setup dropdown has no options, so ' +
      'no establishment can ever be configured and no band can ever be computed. Check the ' +
      'startup log for "Seeder \'NitaqatReferenceSeeder\' failed".',
    ).toBeGreaterThan(0);

    // Arabic names are part of the contract for a KSA compliance surface.
    expect(activities.every((a) => /[؀-ۿ]/.test(a.nameAr))).toBe(true);
  });

  test('standing either computes a band or refuses by name — never a bare error', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();

    if (body.ok) {
      const s = body.standing;
      expect(['Platinum', 'HighGreen', 'MediumGreen', 'LowGreen', 'Red']).toContain(s.band);
      // The weighted figures are what the band is computed from; zeroes mean nothing was counted.
      expect(s.totalWeighted).toBeGreaterThan(0);
      expect(s.achievedPercent).toBeGreaterThanOrEqual(0);
      expect(s.consequenceSummary?.length ?? 0).toBeGreaterThan(0);
    } else {
      // A refusal is an acceptable, honest outcome — but it must be a NAMED one with a remedy,
      // so the screen can tell the user what to do rather than showing a raw failure.
      expect(body.refusal?.reason).toMatch(
        /^nitaqat_(activity_not_configured|thresholds_not_published|no_establishment|size_tier_not_resolved)$/);
      expect(body.refusal.message.length).toBeGreaterThan(20);
      expect(body.refusal.remedy.length).toBeGreaterThan(10);
    }
  });
});

// ── Report export ─────────────────────────────────────────────────────────────────────────────

test('the Excel export is a real workbook, not a CSV wearing an xlsx name', async ({ request }) => {
  const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);

  const resp = await request.post('/api/reports/export', {
    headers: { Authorization: `Bearer ${token}` },
    data: { reportKey: 'hr.headcount', format: 'xlsx', filters: {} },
  });
  expect(resp.status()).toBe(200);
  expect(resp.headers()['content-type'])
    .toContain('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
  expect(resp.headers()['content-disposition']).toMatch(/\.xlsx/);

  const bytes = await resp.body();
  // An .xlsx is an OPC zip: "PK". This is the assertion that would have caught the historical
  // bug where CSV bytes were served with an Excel content type.
  expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK');
  // And it must be a SpreadsheetML package specifically, not any old zip.
  expect(bytes.toString('latin1')).toContain('xl/workbook.xml');

  // The export must carry rows, otherwise the button produces an empty file in the demo.
  expect(Number(resp.headers()['x-report-row-count'] ?? '0')).toBeGreaterThan(0);
});
