import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { AxiosAdapter, InternalAxiosRequestConfig } from 'axios';
import client from '../src/api/client';
import {
  complianceContractsApi, compliancePassportsApi, complianceRenewalsApi, complianceVisaApi, complianceWorkPermitsApi,
} from '../src/api/compliance';

/**
 * List screens that showed the server's first page (its default size, 20–50 rows) as the whole
 * list, with no pager and nothing to say rows were missing. Found by the paging sweep in #152.
 * A screen whose purpose needs every row (an expiry register, a picker) now reads every page; a
 * history list shows "Showing X of Y" with Load more. These tests fail on the code they replace.
 */

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

// ── A fake API behind the real axios client ─────────────────────────────────────────────────
// The API modules are exercised as shipped; only the transport is replaced. Like the controllers
// behind them, the fake pages by `page`/`pageSize`, applies its own default page size when none is
// sent, and echoes the page it returned.

(globalThis as { localStorage?: unknown }).localStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };

interface Call { url: string; params: Record<string, unknown> }

function serve(rowsByUrl: Record<string, readonly unknown[]>, defaultPageSize = 20): Call[] {
  const calls: Call[] = [];
  const adapter: AxiosAdapter = async (config: InternalAxiosRequestConfig) => {
    const url = config.url ?? '';
    const params = { ...(config.params ?? {}) } as Record<string, unknown>;
    calls.push({ url, params });
    const rows = rowsByUrl[url];
    if (!rows) throw new Error(`unexpected request ${url}`);
    const page = Number(params.page ?? 1);
    const size = Number(params.pageSize ?? defaultPageSize);
    const data = { total: rows.length, page, pageSize: size, items: rows.slice((page - 1) * size, page * size) };
    return { data, status: 200, statusText: 'OK', headers: {}, config };
  };
  client.defaults.adapter = adapter;
  return calls;
}

const rows = (n: number, prefix: string) => Array.from({ length: n }, (_, i) => ({ id: `${prefix}-${i + 1}` }));

test.describe('compliance expiry registers read every row', () => {
  test('the bug this replaces: one request without a page size returns the server default of 20', async () => {
    serve({ '/api/compliance/visa-tracking': rows(250, 'visa') });
    const firstPage = await complianceVisaApi.list({ expiringInDays: 30 });
    expect(firstPage.items).toHaveLength(20);
    expect(firstPage.total).toBe(250);
  });

  for (const [name, url, listAll] of [
    ['visas / iqamas', '/api/compliance/visa-tracking', () => complianceVisaApi.listAll({ expiringInDays: 30 })],
    ['passports', '/api/compliance/passports', () => compliancePassportsApi.listAll({ expiringInDays: 30 })],
    ['work permits', '/api/compliance/work-permits', () => complianceWorkPermitsApi.listAll({ expiringInDays: 30 })],
  ] as const) {
    test(`${name}: all 250 rows arrive, in 100-row pages, with the expiry filter on every page`, async () => {
      const calls = serve({ [url]: rows(250, name) });
      const all = await listAll();
      expect(all).toHaveLength(250);
      expect(calls.map((c) => [c.params.page, c.params.pageSize, c.params.expiringInDays])).toEqual([
        [1, 100, 30], [2, 100, 30], [3, 100, 30],
      ]);
    });
  }

  test('contracts and renewals are read to the end with their status filter', async () => {
    const calls = serve({ '/api/compliance/contracts': rows(130, 'c'), '/api/compliance/renewals': rows(21, 'r') });
    expect(await complianceContractsApi.listAll({ status: 'Active' })).toHaveLength(130);
    expect(await complianceRenewalsApi.listAll({ status: 'Pending' })).toHaveLength(21);
    expect(calls.map((c) => `${c.url} p${c.params.page} ${c.params.status}`)).toEqual([
      '/api/compliance/contracts p1 Active', '/api/compliance/contracts p2 Active', '/api/compliance/renewals p1 Pending',
    ]);
  });

  test('the compliance screen uses the full readers and says when a register failed to load', () => {
    const page = read('src/views/CompliancePage.tsx');
    for (const call of [
      'complianceContractsApi.listAll(', 'complianceVisaApi.listAll(', 'compliancePassportsApi.listAll(',
      'complianceWorkPermitsApi.listAll(', 'complianceRenewalsApi.listAll(',
    ]) expect(page).toContain(call);
    expect(page).not.toMatch(/compliance(Contracts|Visa|Passports|WorkPermits|Renewals)Api\.list\(/);
    // A failed load is an error, never the "No records" / green "No expirations" all-clear.
    for (const what of ['"Contracts"', '"Renewals"', '"Expiry alerts"']) expect(page).toContain(`<ListLoadError what=${what}`);
    expect(page).toContain("'Visa and iqama records'");
    // The dashboard preview says it is a preview of a longer list.
    expect(page).toContain("pageWindowText(alerts.length, alertsTotal, 'expirations')");
    // The passport KPIs drill down to a tab that renders the passport register, not a blank page.
    expect(page).toContain("(tab === 'visa' || tab === 'passports') && <VisaPassportTab");
  });
});
