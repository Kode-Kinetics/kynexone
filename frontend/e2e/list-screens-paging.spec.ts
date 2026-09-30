import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { AxiosAdapter, InternalAxiosRequestConfig } from 'axios';
import client from '../src/api/client';
import {
  complianceContractsApi, compliancePassportsApi, complianceRenewalsApi, complianceVisaApi, complianceWorkPermitsApi,
} from '../src/api/compliance';
import {
  absenceApi, compOffApi, encashmentApi, leaveAIApi, leaveBalancesApi, leaveRequestsApi,
} from '../src/api/leave';
import { assessmentsApi, interviewsApi, offersApi, workforcePlanningApi } from '../src/api/recruitment';
import { isSaudiCompany, selectSaudiCompany } from '../src/lib/saudiCompany';

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

test.describe('Saudi settings name the Saudi company they edit', () => {
  test('Saudi companies are recognised by any of their country spellings', () => {
    expect(['SA', 'sau', 'KSA', ' SA '].every((countryCode) => isSaudiCompany({ countryCode }))).toBe(true);
    expect(['AE', 'ARE', '', null].some((countryCode) => isSaudiCompany({ countryCode }))).toBe(false);
  });

  test('the selected company is kept while it exists, else the first Saudi company, else none', () => {
    const saudi = [{ id: 'riyadh' }, { id: 'jeddah' }];
    expect(selectSaudiCompany(saudi, 'jeddah')).toBe('jeddah');
    expect(selectSaudiCompany(saudi, '')).toBe('riyadh');
    expect(selectSaudiCompany(saudi, 'dubai')).toBe('riyadh');
    expect(selectSaudiCompany([], 'dubai')).toBe('');
  });

  test('the config screen reads every company and keeps the Saudi ones, instead of "the" first company', () => {
    const config = read('src/views/SaudiComplianceConfig.tsx');
    expect(config).not.toContain('companiesApi.list(1, 1)');
    expect(config).toContain('companiesApi.listAll()');
    expect(config).toContain('.filter(isSaudiCompany)');
    expect(config).toContain('<CompanyScope ');
    expect(read('src/views/NitaqatPanel.tsx')).toContain("import { isSaudiCompany } from '../lib/saudiCompany';");
  });
});

test.describe('leave history lists show one page at a time and say how many there are', () => {
  test('the leave list endpoints hand the screen the server total, not only the rows', async () => {
    const n = 130;
    serve({
      '/api/leave/balances': rows(n, 'bal'), '/api/leave/requests': rows(n, 'req'), '/api/leave/encashment': rows(n, 'enc'),
      '/api/leave/compoff': rows(n, 'co'), '/api/leave/absences': rows(n, 'abs'), '/api/leave/ai-insights': rows(n, 'ins'),
    }, 25);
    const pagers = {
      balances: (page: number, pageSize: number) => leaveBalancesApi.list({ year: 2026, page, pageSize }),
      requests: (page: number, pageSize: number) => leaveRequestsApi.list({ page, pageSize }),
      encashment: (page: number, pageSize: number) => encashmentApi.list({ page, pageSize }),
      compOff: (page: number, pageSize: number) => compOffApi.list({ page, pageSize }),
      absences: (page: number, pageSize: number) => absenceApi.list({ page, pageSize }),
      insights: (page: number, pageSize: number) => leaveAIApi.list({ page, pageSize }),
    };
    for (const [name, fetchPage] of Object.entries(pagers)) {
      const first = await fetchPage(1, 100);
      const second = await fetchPage(2, 100);
      expect([name, first.items.length, first.total, second.items.length]).toEqual([name, 100, n, 30]);
    }
  });

  test('balances, request history, encashment, comp-off, absences and insights page with Load more', () => {
    const leave = read('src/views/LeavePage.tsx');
    for (const call of [
      'leaveBalancesApi.list({ employeeId: empId ? Number(empId) : undefined, year, ...groupFilter, page, pageSize })',
      'leaveRequestsApi.list({ status: statusFilter || undefined, page, pageSize })',
      'encashmentApi.list({ ...groupFilter, page, pageSize })',
      'compOffApi.list({ ...groupFilter, page, pageSize })',
      'absenceApi.list({ ...groupFilter, page, pageSize })',
      'leaveAIApi.list({ page, pageSize })',
    ]) expect(leave).toContain(call);
    for (const noun of ['balances', 'requests', 'encashment requests', 'credits', 'absence records', 'insights']) {
      expect(leave).toContain(`total={list.total} noun="${noun}"`);
    }
    // Counts above the lists come from the server total, not the rows on screen.
    for (const count of ['balances', 'requests', 'credits', 'absences']) expect(leave).toContain(`list.total ?? ${count}.length`);
    // The dashboard preview asks for the six rows it shows and counts from the total.
    expect(leave).toContain("leaveRequestsApi.list({ status: 'PendingManagerApproval', ...groupFilter, pageSize: 6 })");
  });
});

test.describe('performance goals and reviews page with the server total', () => {
  test('goals, my reviews and team reviews page with Load more and count from the total', () => {
    const perf = read('src/views/PerformancePage.tsx');
    for (const call of [
      'goalsApi.list({ status: statusFilter || undefined, page, pageSize })',
      'reviewsApi.list({ page, pageSize })',
      'reviewsApi.list({ status: statusFilter || undefined, page, pageSize })',
    ]) expect(perf).toContain(call);
    expect(perf).toContain('total={list.total} noun="goals"');
    expect(perf.match(/total=\{list\.total\} noun="reviews"/g)).toHaveLength(2);
    expect(perf).toContain('list.total ?? goals.length');
    expect(perf.match(/list\.total \?\? reviews\.length/g)).toHaveLength(2);
  });
});

test.describe('recruitment lists page with the server total', () => {
  test('interviews, assessments, offers and workforce plans pass the page they want', async () => {
    const calls = serve({
      '/api/recruitment/interviews': rows(130, 'iv'), '/api/recruitment/assessments': rows(130, 'as'),
      '/api/recruitment/offers': rows(130, 'of'), '/api/recruitment/workforce-planning': rows(130, 'wp'),
    });
    const second = await Promise.all([
      interviewsApi.list(undefined, 'Scheduled', 2, 100),
      assessmentsApi.list(undefined, 'Sent', 2, 100),
      offersApi.list(undefined, 'PendingApproval', 2, 100),
      workforcePlanningApi.list(undefined, undefined, 2, 100),
    ]);
    expect(second.map((r) => [r.items.length, r.total])).toEqual([[30, 130], [30, 130], [30, 130], [30, 130]]);
    expect(calls.every((c) => c.params.page === 2 && c.params.pageSize === 100)).toBe(true);
  });

  test('requisitions, candidates, interviews, assessments, offers, onboarding tasks and plans show Load more', () => {
    const rec = read('src/views/RecruitmentPage.tsx');
    for (const call of [
      'requisitionsApi.list({ status: statusFilter || undefined, page, pageSize })',
      'candidatesApi.list({ search: search || undefined, page, pageSize })',
      'workforcePlanningApi.list(undefined, undefined, page, pageSize)',
      'interviewsApi.list(undefined, statusFilter || undefined, page, pageSize)',
      'assessmentsApi.list(undefined, statusFilter || undefined, page, pageSize)',
      'offersApi.list(undefined, statusFilter || undefined, page, pageSize)',
      'onboardingApi.listTasks({ status: statusFilter || undefined, page, pageSize })',
    ]) expect(rec).toContain(call);
    for (const noun of ['requisitions', 'candidates', 'plans', 'interviews', 'assessments', 'offers', 'tasks']) {
      expect(rec).toContain(`total={list.total} noun="${noun}"`);
    }
    // The overview asks for the five openings it previews.
    expect(rec).toContain("openingsApi.list({ status: 'Open', pageSize: 5 })");
  });
});

test.describe('AI assistant lists say when they are a window', () => {
  test('insights and the query log page with Load more; the capped risk table says it is the top slice', () => {
    const ai = read('src/views/AIAssistantPage.tsx');
    expect(ai).not.toContain('{ page: 1 }');
    expect(ai).toContain('aiAssistantApi.listInsights({ page, pageSize })');
    expect(ai).toContain('aiAssistantApi.queryHistory({ page, pageSize })');
    expect(ai).toContain('total={insightList.total} noun="insights"');
    expect(ai).toContain('total={historyList.total} noun="queries"');
    // The risk endpoint returns at most 100 rows and no total (AIAssistantController.RiskScores).
    expect(read('../backend-dotnet/Zayra.Api/Controllers/AIAssistantController.cs')).toMatch(/OrderByDescending\(r => r\.ChurnRiskScore\)\s*\.Take\(100\)/);
    expect(ai).toContain('const RISK_SCORE_LIMIT = 100;');
    expect(ai).toContain('riskScores.length >= RISK_SCORE_LIMIT');
  });
});
