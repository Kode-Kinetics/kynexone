import { test, expect } from '@playwright/test';
import {
  apiLogin, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

// Track A — Saudi Regulatory Compliance E2E coverage, against the fixture world (e2e/world.ts).
// The module guard and the statutory lock that used to be proven here against a per-run "limited"
// tenant are now proven by e2e/module-switches.spec.ts, which switches modules off and back on.

test.describe('Saudi compliance — API authorization', () => {
  test('IntelliFlow admin can access saudi-compliance dashboard', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/dashboard', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('qiwa');
    expect(body).toHaveProperty('wps');
    expect(body).toHaveProperty('gosi');
    expect(body).toHaveProperty('actionItems');
  });

  test('IntelliFlow employee cannot access QIWA configuration endpoint', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const resp = await request.put('/api/qiwa/connection', {
      headers: { Authorization: `Bearer ${token}` },
      data: { establishmentId: '7000123456', establishmentName: 'X', unifiedOrganisationNumber: '1', environment: 'sandbox' },
    });
    // Employee lacks qiwa.configure permission.
    expect(resp.status()).toBe(403);
  });

  test('GOSI readiness endpoint returns 200 with disclaimer', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/gosi-readiness', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('disclaimer');
    expect(body.disclaimer).toContain('illustrative');
    expect(body).toHaveProperty('employees');
  });

  test('QIWA readiness summary surfaces blocked employees with missing fields', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/qiwa/readiness-summary', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('totalEmployees');
    expect(body).toHaveProperty('blockedFromSync');
    expect(body).toHaveProperty('blockedEmployees');
    expect(Array.isArray(body.blockedEmployees)).toBe(true);
  });

  test('WPS pre-export validation on a non-existent run returns 404', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.post('/api/payroll/runs/00000000-0000-0000-0000-000000000000/wps-validation', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(404);
  });
});

// ── Nitaqat / Saudization ────────────────────────────────────────────────────
// Shape-and-authorization only. The demo tenant may or may not have an MHRSD
// economic activity configured, and BOTH outcomes are correct product behaviour:
// a standing, or a named refusal. What must never happen is a 200 carrying
// neither, or a band with no numbers behind it.
test.describe('Saudi compliance — Nitaqat', () => {
  test('Nitaqat standing returns either a band with its working, or a named refusal', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const body = await resp.json();
    expect(body).toHaveProperty('ok');

    if (body.ok) {
      const s = body.standing;
      expect(s).toBeTruthy();
      // A band is never shown without the numbers that produced it.
      for (const k of [
        'saudiWeighted', 'totalWeighted', 'achievedPercent', 'band',
        'currentBandFloorPercent', 'breakdown', 'scenario',
        'allInputsVerified', 'unverifiedInputs',
      ]) {
        expect(s).toHaveProperty(k);
      }
      expect(['Red', 'LowGreen', 'MediumGreen', 'HighGreen', 'Platinum']).toContain(s.band);
      expect(Array.isArray(s.breakdown)).toBe(true);
    } else {
      // A refusal is a designed state: it must name a reason and give a remedy.
      expect(body.standing).toBeNull();
      expect(body.refusal).toBeTruthy();
      expect(body.refusal.reason).toMatch(/^nitaqat_/);
      expect(body.refusal.remedy.length).toBeGreaterThan(0);
    }
  });

  test('the report catalogue offers a Saudization report', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/reports/catalog', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(200);
    const catalog = await resp.json();
    expect(catalog.some((r: { key: string }) => r.key === 'compliance.saudization')).toBe(true);
  });

  test('hire impact requires a nationality', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const resp = await request.get('/api/saudi-compliance/nitaqat/hire-impact?count=1', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(resp.status()).toBe(400);
    expect((await resp.json()).error).toBe('nationality_required');
  });
});

test.describe('Saudi compliance — UI', () => {
  test('saudi-compliance page loads for IntelliFlow admin', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/saudi-compliance');
    await expect(page.getByRole('heading', { name: 'Saudi Compliance' })).toBeVisible({ timeout: 15_000 });
    // No crash: at least one of the section cards is present.
    await expect(page.getByText('QIWA', { exact: true }).first()).toBeVisible();
  });
});
