import { test, expect, APIRequestContext } from '@playwright/test';
import {
  apiLogin,
  INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN,
  RASALMANAR_SLUG, RASALMANAR_ADMIN,
} from './helpers';

/**
 * Tenant isolation tests — all via direct API calls.
 * Verifies that a JWT issued for Tenant A cannot access Tenant B's data.
 *
 * These are the P0 security tests for the demo.
 *
 * ── Why the shape handling below is strict ───────────────────────────────────
 * Every check here is "the two tenants' id sets must not intersect". That is
 * only evidence when BOTH sets are non-empty and both were really parsed out of
 * the response. The previous version used
 *     (data.items ?? data.logs ?? data.data ?? []) as Array<{id}>
 * which silently produced [] for /api/audit-logs — a BARE ARRAY endpoint — so
 * `[] ∩ [] = []` and a total cross-tenant audit-log leak stayed green. It also
 * returned early on a non-2xx response, which made a 500 a passing test.
 *
 * So: `listRows` accepts only the two shapes this API actually serves and
 * throws on anything else, `idsOf` refuses rows without a usable id, every
 * response is asserted 200, and every comparison carries a positive control
 * proving there was data on both sides to compare.
 */

/** Bare array (e.g. /api/audit-logs) or PagedResult ({ items, total, ... }). Anything else throws. */
function listRows(body: unknown, endpoint: string): Array<{ id?: unknown }> {
  if (Array.isArray(body)) return body as Array<{ id?: unknown }>;
  if (body !== null && typeof body === 'object' && Array.isArray((body as { items?: unknown }).items))
    return (body as { items: Array<{ id?: unknown }> }).items;
  throw new Error(
    `${endpoint} returned an unrecognised list shape — the isolation check cannot read it: ` +
    `${JSON.stringify(body).slice(0, 300)}`,
  );
}

/** Row ids, refusing undefined/null: an overlap of `undefined` values proves nothing. */
function idsOf(rows: Array<{ id?: unknown }>, endpoint: string): unknown[] {
  return rows.map((row, i) => {
    if (row?.id === undefined || row?.id === null)
      throw new Error(`${endpoint} row ${i} has no id — cannot compare tenants by id: ${JSON.stringify(row).slice(0, 200)}`);
    return row.id;
  });
}

/** GET + assert 200 + parse into rows. */
async function fetchRows(
  request: APIRequestContext,
  endpoint: string,
  token: string,
  who: string,
): Promise<Array<{ id?: unknown }>> {
  const resp = await request.get(endpoint, { headers: { Authorization: `Bearer ${token}` } });
  expect(resp.status(), `GET ${endpoint} as ${who} must return 200, got ${resp.status()}: ${(await resp.text()).slice(0, 200)}`)
    .toBe(200);
  return listRows(await resp.json(), `${endpoint} (${who})`);
}

test.describe('Tenant isolation (API-level)', () => {
  let intelliflowToken: string;
  let rasAlManarToken: string;

  test.beforeAll(async ({ request }) => {
    intelliflowToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    rasAlManarToken  = await apiLogin(request, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
  });

  // ── Employee isolation ───────────────────────────────────────────────────────

  test('IntelliFlow and Ras Al-Manar employee sets are isolated', async ({ request }) => {
    const myIds    = idsOf(await fetchRows(request, '/api/employees', intelliflowToken, 'IntelliFlow'), '/api/employees');
    const theirIds = idsOf(await fetchRows(request, '/api/employees', rasAlManarToken,  'Ras Al-Manar'), '/api/employees');

    // Positive control: an empty list on either side makes the overlap check vacuous.
    expect(myIds.length, 'IntelliFlow must have seeded employees for this check to mean anything').toBeGreaterThan(0);
    expect(theirIds.length, 'Ras Al-Manar must have seeded employees for this check to mean anything').toBeGreaterThan(0);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap, 'employee ids visible to BOTH tenants').toHaveLength(0);
  });

  test('Ras Al-Manar token with IntelliFlow employee ID returns 403 or 404', async ({ request }) => {
    // First get an IntelliFlow employee id
    const ids = idsOf(await fetchRows(request, '/api/employees', intelliflowToken, 'IntelliFlow'), '/api/employees');
    expect(ids.length, 'IntelliFlow must have a seeded employee to attempt the cross-tenant read with').toBeGreaterThan(0);

    const empId = ids[0];

    // Ras Al-Manar token tries to fetch an IntelliFlow employee
    const crossTenantResp = await request.get(`/api/employees/${empId}`, {
      headers: { Authorization: `Bearer ${rasAlManarToken}` },
    });
    expect([403, 404]).toContain(crossTenantResp.status());
  });

  // ── Leave request isolation ──────────────────────────────────────────────────

  // KNOWN BROKEN — tracked in #55. This asserts real isolation, but the endpoint returns zero
  // rows to its OWN tenant: the seeded leave_requests all have company_id = NULL, LeaveRequest is
  // ICompanyScopedOperational, and its filter hides null-company rows from any non-group-scope
  // caller. The filter is correct; the seeder never stamps CompanyId. Marked failing rather than
  // skipped so this flips to an unexpected PASS the moment #55 is fixed, instead of rotting.
  test('IntelliFlow leave requests are not visible to Ras Al-Manar token', async ({ request }) => {
    test.fail(); // see the comment above — tracked in #55
    const myIds    = idsOf(await fetchRows(request, '/api/leave/requests', intelliflowToken, 'IntelliFlow'), '/api/leave/requests');
    const theirIds = idsOf(await fetchRows(request, '/api/leave/requests', rasAlManarToken,  'Ras Al-Manar'), '/api/leave/requests');

    // Positive control. Two empty lists never intersect, so without this the test
    // passes on a fixture that contains no leave requests at all — proving nothing.
    expect(myIds.length, 'IntelliFlow must have visible leave requests for this isolation check to mean anything').toBeGreaterThan(0);
    expect(theirIds.length, 'Ras Al-Manar must have visible leave requests for this isolation check to mean anything').toBeGreaterThan(0);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap, 'leave request ids visible to BOTH tenants').toHaveLength(0);
  });

  // ── Attendance isolation ─────────────────────────────────────────────────────

  // KNOWN BROKEN — tracked in #55. AttendanceController.Daily reads AttendanceDailyRecords,
  // which has 0 rows tenant-wide, while the seeder populates attendance_records (276 intelliflow,
  // 510 rasalmanar) — also all company_id = NULL. No date range recovers data. Same marker
  // rationale as above.
  test('IntelliFlow attendance records not visible to Ras Al-Manar token', async ({ request }) => {
    test.fail(); // see the comment above — tracked in #55
    const myIds    = idsOf(await fetchRows(request, '/api/attendance', intelliflowToken, 'IntelliFlow'), '/api/attendance');
    const theirIds = idsOf(await fetchRows(request, '/api/attendance', rasAlManarToken,  'Ras Al-Manar'), '/api/attendance');

    expect(myIds.length, 'IntelliFlow must have visible attendance records for this isolation check to mean anything').toBeGreaterThan(0);
    expect(theirIds.length, 'Ras Al-Manar must have visible attendance records for this isolation check to mean anything').toBeGreaterThan(0);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap, 'attendance record ids visible to BOTH tenants').toHaveLength(0);
  });

  // ── Tenant admin routes are isolated ──────────────────────────────────────────

  test('Ras Al-Manar token cannot read IntelliFlow settings', async ({ request }) => {
    // Tenant admin settings routes must be scoped by the JWT tenant_id.
    // Reading each tenant's own localization row and comparing them is the actual
    // proof; `if (resp.ok())` around the assertion meant a 500 passed silently.
    const mine = await request.get('/api/tenant-admin/localization', {
      headers: { Authorization: `Bearer ${rasAlManarToken}` },
    });
    expect(mine.status(), `Ras Al-Manar must be able to read its OWN localization: ${(await mine.text()).slice(0, 200)}`).toBe(200);
    const mineBody = await mine.json();

    const theirs = await request.get('/api/tenant-admin/localization', {
      headers: { Authorization: `Bearer ${intelliflowToken}` },
    });
    expect(theirs.status()).toBe(200);
    const theirsBody = await theirs.json();

    // Positive control: both rows must actually identify a tenant, otherwise the
    // inequality below compares two undefineds.
    expect(mineBody.tenantId, 'localization response must carry a tenantId').toBeTruthy();
    expect(theirsBody.tenantId, 'localization response must carry a tenantId').toBeTruthy();

    // The boundary: Ras Al-Manar's token must never resolve to IntelliFlow's row.
    expect(mineBody.tenantId).not.toBe(theirsBody.tenantId);
    expect(mineBody.id).not.toBe(theirsBody.id);
    expect(JSON.stringify(mineBody).toLowerCase()).not.toContain('intelliflow');
  });

  // ── Platform admin API requires platform JWT ──────────────────────────────────

  test('Tenant JWT cannot access platform admin endpoints', async ({ request }) => {
    const resp = await request.get('/api/platform/stats', {
      headers: { Authorization: `Bearer ${intelliflowToken}` },
    });
    expect([401, 403]).toContain(resp.status());
  });

  test('Tenant JWT cannot list all tenants via platform API', async ({ request }) => {
    const resp = await request.get('/api/platform/tenants', {
      headers: { Authorization: `Bearer ${intelliflowToken}` },
    });
    expect([401, 403]).toContain(resp.status());
  });

  // ── Audit log isolation ────────────────────────────────────────────────────────

  test('IntelliFlow audit logs not visible to Ras Al-Manar token', async ({ request }) => {
    // /api/audit-logs returns a BARE ARRAY. The old `data.items ?? data.logs ??
    // data.data ?? []` chain therefore produced [] on both sides, every time.
    const myRows    = await fetchRows(request, '/api/audit-logs', intelliflowToken, 'IntelliFlow');
    const theirRows = await fetchRows(request, '/api/audit-logs', rasAlManarToken,  'Ras Al-Manar');
    const myIds    = idsOf(myRows, '/api/audit-logs');
    const theirIds = idsOf(theirRows, '/api/audit-logs');

    // Positive control — both tenants have at least their own setup login logged.
    expect(myIds.length, 'IntelliFlow must have audit log entries for this isolation check to mean anything').toBeGreaterThan(0);
    expect(theirIds.length, 'Ras Al-Manar must have audit log entries for this isolation check to mean anything').toBeGreaterThan(0);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap, 'audit log ids visible to BOTH tenants').toHaveLength(0);

    // Stronger than id-disjointness: no row may carry the other tenant's TenantId.
    const myTenantIds    = new Set(myRows.map(r => (r as { tenantId?: unknown }).tenantId));
    const theirTenantIds = new Set(theirRows.map(r => (r as { tenantId?: unknown }).tenantId));
    expect(myTenantIds.size, 'IntelliFlow audit rows must all belong to one tenant').toBe(1);
    expect(theirTenantIds.size, 'Ras Al-Manar audit rows must all belong to one tenant').toBe(1);
    expect([...myTenantIds][0]).not.toBe([...theirTenantIds][0]);
  });
});
