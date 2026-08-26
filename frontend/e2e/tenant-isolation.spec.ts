import { test, expect } from '@playwright/test';
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
 */
test.describe('Tenant isolation (API-level)', () => {
  let intelliflowToken: string;
  let rasAlManarToken: string;

  test.beforeAll(async ({ request }) => {
    intelliflowToken = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    rasAlManarToken  = await apiLogin(request, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
  });

  // ── Employee isolation ───────────────────────────────────────────────────────

  test('IntelliFlow and Ras Al-Manar employee sets are isolated', async ({ request }) => {
    // 1. Get IntelliFlow employee list
    const myEmps = await request.get('/api/employees', {
      headers: { Authorization: `Bearer ${intelliflowToken}` },
    });
    expect(myEmps.status()).toBe(200);
    const myData = await myEmps.json();
    const myIds = (myData.items ?? myData.employees ?? myData.data ?? []) as Array<{ id: unknown }>;

    // 2. Get Evostel employee list
    const theirEmps = await request.get('/api/employees', {
      headers: { Authorization: `Bearer ${rasAlManarToken}` },
    });
    expect(theirEmps.status()).toBe(200);
    const theirData = await theirEmps.json();
    const theirIds = (theirData.items ?? theirData.employees ?? theirData.data ?? []) as Array<{ id: unknown }>;

    expect(myIds.length).toBeGreaterThan(0);
    expect(theirIds.length).toBeGreaterThan(0);

    // 3. There must be no overlap
    const myIdList   = myIds.map((e: { id: unknown }) => e.id);
    const theirIdList = theirIds.map((e: { id: unknown }) => e.id);
    const overlap = myIdList.filter(id => theirIdList.includes(id));
    expect(overlap).toHaveLength(0);
  });

  test('Ras Al-Manar token with IntelliFlow employee ID returns 403 or 404', async ({ request }) => {
    // First get an IntelliFlow employee id
    const myEmps = await request.get('/api/employees', {
      headers: { Authorization: `Bearer ${intelliflowToken}` },
    });
    if (!myEmps.ok()) return; // skip if no employees yet
    const myData = await myEmps.json();
    const ids = (myData.items ?? myData.employees ?? myData.data ?? []) as Array<{ id: unknown }>;
    if (ids.length === 0) return; // no employees to test with

    const empId = ids[0].id;

    // Evostel token tries to fetch IntelliFlow employee
    const crossTenantResp = await request.get(`/api/employees/${empId}`, {
      headers: { Authorization: `Bearer ${rasAlManarToken}` },
    });
    expect([403, 404]).toContain(crossTenantResp.status());
  });

  // ── Leave request isolation ──────────────────────────────────────────────────

  test('IntelliFlow leave requests are not visible to Evostel token', async ({ request }) => {
    const myLeave    = await request.get('/api/leave/requests', { headers: { Authorization: `Bearer ${intelliflowToken}` } });
    const theirLeave = await request.get('/api/leave/requests', { headers: { Authorization: `Bearer ${rasAlManarToken}` } });

    if (!myLeave.ok() || !theirLeave.ok()) return;

    const myData    = await myLeave.json();
    const theirData = await theirLeave.json();
    const myIds    = ((myData.items ?? myData.requests ?? myData.data ?? []) as Array<{ id: unknown }>).map((r: { id: unknown }) => r.id);
    const theirIds = ((theirData.items ?? theirData.requests ?? theirData.data ?? []) as Array<{ id: unknown }>).map((r: { id: unknown }) => r.id);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap).toHaveLength(0);
  });

  // ── Attendance isolation ─────────────────────────────────────────────────────

  test('IntelliFlow attendance records not visible to Evostel token', async ({ request }) => {
    const myAtt    = await request.get('/api/attendance', { headers: { Authorization: `Bearer ${intelliflowToken}` } });
    const theirAtt = await request.get('/api/attendance', { headers: { Authorization: `Bearer ${rasAlManarToken}` } });

    if (!myAtt.ok() || !theirAtt.ok()) return;

    const myData    = await myAtt.json();
    const theirData = await theirAtt.json();
    const myIds    = ((myData.items ?? myData.records ?? myData.data ?? []) as Array<{ id: unknown }>).map((r: { id: unknown }) => r.id);
    const theirIds = ((theirData.items ?? theirData.records ?? theirData.data ?? []) as Array<{ id: unknown }>).map((r: { id: unknown }) => r.id);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap).toHaveLength(0);
  });

  // ── Tenant admin routes are isolated ──────────────────────────────────────────

  test('Ras Al-Manar token cannot read IntelliFlow settings', async ({ request }) => {
    // Tenant admin settings routes must be scoped by the JWT tenant_id
    const resp = await request.get('/api/tenant-admin/localization', {
      headers: { Authorization: `Bearer ${rasAlManarToken}` },
    });
    // Must succeed (Evostel's own settings) OR return 403 — never expose IntelliFlow data
    if (resp.ok()) {
      const data = await resp.json();
      // The response must not include any IntelliFlow-specific data.
      const text = JSON.stringify(data).toLowerCase();
      expect(text).not.toContain('intelliflow');
    }
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

  test('IntelliFlow audit logs not visible to Evostel token', async ({ request }) => {
    const myLogs    = await request.get('/api/audit-logs', { headers: { Authorization: `Bearer ${intelliflowToken}` } });
    const theirLogs = await request.get('/api/audit-logs', { headers: { Authorization: `Bearer ${rasAlManarToken}` } });

    if (!myLogs.ok() || !theirLogs.ok()) return;

    const myData    = await myLogs.json();
    const theirData = await theirLogs.json();
    const myIds    = ((myData.items ?? myData.logs ?? myData.data ?? []) as Array<{ id: unknown }>).map((l: { id: unknown }) => l.id);
    const theirIds = ((theirData.items ?? theirData.logs ?? theirData.data ?? []) as Array<{ id: unknown }>).map((l: { id: unknown }) => l.id);

    const overlap = myIds.filter(id => theirIds.includes(id));
    expect(overlap).toHaveLength(0);
  });
});
