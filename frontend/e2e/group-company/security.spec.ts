/**
 * Security / fail-closed checks — pure API (Playwright request contexts):
 *   (a) auditor@almarai-test.local: read employees OK, create employee 403
 *   (b) scoped admin CSV export contains no sibling-company employee codes
 *   (c) scoped admin cannot export the payroll register of an inaccessible
 *       company's run (403/404/empty) — skips if no such run is discoverable
 *   (d) malformed / random X-Company-Id values return empty lists, never 500
 */
import { test, expect } from '@playwright/test';
import {
  stackDownReason,
  groupSeedMissingReason,
  newApi,
  apiLogin,
  tryApiLogin,
  fetchCompanies,
  companyIdByCode,
  listItems,
  groupUser,
  ALMARAI,
  ALMARAI_SIBLING_CODES,
  empCodePrefix,
} from './helpers';

const AUDITOR = groupUser('auditor'); // auditor@almarai-test.local
const SCOPED = 'scoped.admin@almarai-test.local';
const OWNER = groupUser('owner');
const BAKERY = 'ALM-BAKERY-KSA';

let skipReason: string | null = null;

test.describe('Group→Company: security & fail-closed API checks', () => {
  test.beforeAll(async () => {
    skipReason = (await stackDownReason()) ?? (await groupSeedMissingReason(OWNER));
  });

  test.beforeEach(() => {
    test.skip(skipReason !== null, skipReason ?? '');
  });

  test('(a) auditor can GET /api/employees but POST /api/employees is 403', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, AUDITOR, ALMARAI.slug);

      const read = await api.get('/api/employees?page=1&pageSize=10', {
        headers: { Authorization: `Bearer ${token}` },
      });
      expect(read.status(), 'auditor read access must work').toBe(200);

      const write = await api.post('/api/employees', {
        headers: { Authorization: `Bearer ${token}` },
        data: {
          fullName: 'E2E Should Never Exist',
          employeeCode: 'E2E-FORBIDDEN-001',
        },
      });
      // Role authorization runs before model validation → must be 403, not 400.
      expect(write.status(), 'auditor must not be able to create employees').toBe(403);
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('(b) scoped admin CSV export excludes sibling-company employee codes', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, SCOPED, ALMARAI.slug);
      const resp = await api.get('/api/employees/export', {
        headers: { Authorization: `Bearer ${token}` },
      });
      expect(resp.status(), 'scoped admin export must succeed').toBe(200);
      const csv = await resp.text();
      expect(csv).toContain('EmployeeCode');
      for (const sibling of ALMARAI_SIBLING_CODES) {
        expect(csv, `CSV export leaked ${sibling} employees`).not.toContain(empCodePrefix(sibling));
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('(c) scoped admin cannot export payroll register of an inaccessible company run', async () => {
    const api = await newApi();
    try {
      // Discover a payroll run belonging to an inaccessible company via the group owner.
      const owner = await tryApiLogin(api, OWNER, ALMARAI.slug);
      test.skip(!owner, 'group owner login unavailable — cannot discover payroll runs');

      const companies = await fetchCompanies(api, owner!.token).catch(() => []);
      const bakeryId = companyIdByCode(companies, BAKERY);
      test.skip(!bakeryId, `could not resolve ${BAKERY} company id`);

      const runsResp = await api.get('/api/payroll/runs?page=1&pageSize=50', {
        headers: { Authorization: `Bearer ${owner!.token}`, 'X-Company-Id': bakeryId! },
      });
      let runId: string | null = null;
      if (runsResp.ok()) {
        const runs = listItems(await runsResp.json().catch(() => null));
        const first = runs[0];
        runId = first ? (first.id ?? first.Id ?? first.runId ?? null) : null;
      }
      test.skip(!runId, `no payroll run discoverable for ${BAKERY} — seed has no runs for that company; skipping gracefully`);

      // Scoped admin (no access to BAKERY) hits the register export directly.
      const { token: scopedToken } = await apiLogin(api, SCOPED, ALMARAI.slug);
      const exportResp = await api.get(`/api/payroll/reports/register/export?runId=${runId}`, {
        headers: { Authorization: `Bearer ${scopedToken}` },
      });

      expect(exportResp.status(), 'must never 500').toBeLessThan(500);
      if (exportResp.ok()) {
        // If the endpoint answers 200, it must answer with EMPTY data — no sibling rows.
        const body = await exportResp.text();
        for (const sibling of ALMARAI_SIBLING_CODES) {
          expect(body, `payroll register export leaked ${sibling} rows to a scoped admin`)
            .not.toContain(empCodePrefix(sibling));
        }
      } else {
        expect([401, 403, 404]).toContain(exportResp.status());
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('(d) random-guid X-Company-Id on /api/employees returns an empty list, not 500', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, SCOPED, ALMARAI.slug);
      const randomGuid = 'deadbeef-0000-4000-8000-000000000e2e';
      const resp = await api.get('/api/employees?page=1&pageSize=50', {
        headers: { Authorization: `Bearer ${token}`, 'X-Company-Id': randomGuid },
      });
      expect(resp.status(), 'random X-Company-Id must not cause a 500').toBeLessThan(500);
      if (resp.ok()) {
        const items = listItems(await resp.json().catch(() => null));
        expect(items.length, 'random X-Company-Id must yield an empty list (fail closed)').toBe(0);
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });

  test('(d2) garbage (non-guid) X-Company-Id on /api/employees never 500s or leaks', async () => {
    const api = await newApi();
    try {
      const { token } = await apiLogin(api, SCOPED, ALMARAI.slug);
      const resp = await api.get('/api/employees?page=1&pageSize=50', {
        headers: { Authorization: `Bearer ${token}`, 'X-Company-Id': 'not-a-guid-at-all' },
      });
      expect(resp.status(), 'garbage X-Company-Id must not cause a 500').toBeLessThan(500);
      const raw = await resp.text();
      for (const sibling of ALMARAI_SIBLING_CODES) {
        expect(raw, `garbage X-Company-Id leaked ${sibling} data`).not.toContain(empCodePrefix(sibling));
      }
    } finally {
      await api.dispose().catch(() => {});
    }
  });
});
