import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type ApiMocks, type Persona } from '../setup-consolidation/fixture';

/**
 * Space pass, batch 2 (Screen Standard 1 and 2): the main content of a screen sits in the first screenful at 1366x768 and
 * 1280x720, in English and Arabic, and the page never scrolls sideways. Every API call is intercepted; nothing is read or written.
 *
 *   /reports   Analytics Dashboard: every KPI band starts inside the first screenful: the Last Payroll Run band shows its heading and values (it started near y=880 under three bands of tall KPI tiles; the trend charts follow it)
 *   /learning  Overview: the Completion table starts above y=600 (it started near 1000 under seven tall indicator cards)
 *
 * SPACE_SHOTS_DIR=<dir> also saves a screenshot per screen, viewport and language (the before/after evidence).
 */
const SHOTS = process.env.SPACE_SHOTS_DIR;
const VIEWPORTS = [{ width: 1366, height: 768 }, { width: 1280, height: 720 }] as const;
const LOCALES = ['en', 'ar'] as const;
type Locale = (typeof LOCALES)[number];

async function shot(page: Page, screen: string, locale: Locale) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  const vp = page.viewportSize();
  await page.screenshot({ path: `${SHOTS}/${process.env.SPACE_PHASE ?? 'run'}-${screen}-${locale}-${vp?.width}x${vp?.height}.png` });
}
const noSideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const topOf = async (page: Page, selector: string) => page.locator(selector).first().evaluate(el => Math.round(el.getBoundingClientRect().top));

const persona = (roles: string[], extra: string[] = []): Persona => ({ roles, permissions: [...ALL_PERMISSIONS, ...extra] });

// ── /reports ───────────────────────────────────────────────────────────────────────────────────────────────────────────
const KPIS = {
  headcount: { totalActive: 42, newThisMonth: 3, exitsThisMonth: 1 },
  leave: { pendingLeave: 4, onLeaveToday: 2 },
  attendance: { presentToday: 37, lateToday: 5 },
  overtime: { pendingOT: 6 },
  payroll: { lastRunYear: 2026, lastRunMonth: 9, lastRunStatus: 'Approved', totalNetSalary: 512000 },
  compliance: { visasExpiring: 2, passportsExpiring: 1 },
  recruitment: { openPositions: 7, pendingApplications: 11 },
  financial: { activeLoans: 9, outstandingLoanBalance: 81500 },
  withheld: [],
  generatedAt: '2026-11-18T07:00:00Z',
};
const months = ['2026-06', '2026-07', '2026-08', '2026-09', '2026-10', '2026-11'];
const REPORTS: ApiMocks = {
  '/api/analytics/kpis': KPIS,
  '/api/analytics/trends/headcount': months.map((period, i) => ({ period, headcount: 30 + i * 2 })),
  '/api/analytics/trends/payroll': months.map((period, i) => ({ period, TotalNetSalary: 400000 + i * 20000, TotalGrossSalary: 450000 + i * 20000 })),
  '/api/analytics/trends/attendance': Array.from({ length: 30 }, (_, i) => ({ date: `2026-10-${String(i + 1).padStart(2, '0')}`, present: 30 + (i % 5), absent: 2, late: i % 4 })),
  '/api/analytics/trends/leave': months.map((period, i) => ({ period, totalRequests: 4 + i, totalDays: 10 + i * 3 })),
};

// ── /learning ──────────────────────────────────────────────────────────────────────────────────────────────────────────
const LEARNING_PERMS = ['learning.read', 'learning.manage', 'learning.assign', 'learning.report', 'learning.records.correct'];
const kpi = (key: string, value: number) => ({ key, value, definition: '', drillReport: 'status', drillFilter: null });
const LEARNING: ApiMocks = {
  '/api/learning/reports/summary': {
    kpis: [kpi('assigned', 24), kpi('open', 9), kpi('completed', 15), kpi('overdue', 3), kpi('expiring', 2), kpi('locked', 1), kpi('pending', 2)],
    completionRatePercent: 62.5, scope: 'organization', asOf: '2026-11-18',
  },
  '/api/learning/reports/completion': Array.from({ length: 5 }, (_, i) => ({
    groupBy: 'path', groupId: `p-${i}`, group: `Safety path ${i + 1}`, assigned: 8, completed: 5, inProgress: 2, notStarted: 1, overdue: i % 2, completionRatePercent: 62.5,
  })),
};

for (const locale of LOCALES) {
  for (const viewport of VIEWPORTS) {
    test.describe(`space batch 2 (${locale} ${viewport.width}x${viewport.height})`, () => {
      test.use({ viewport });

      test('/reports: the last KPI band (Last Payroll Run) starts in the first screenful and nothing scrolls sideways', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager'], ['reports.read']), locale, url: '/reports', mocks: REPORTS });
        const heading = page.getByRole('heading', { level: 3, name: /Last Payroll Run|آخر مسير رواتب/ });
        await expect(heading).toBeVisible({ timeout: 60_000 });
        await shot(page, 'reports', locale);
        expect(await heading.evaluate(el => Math.round(el.getBoundingClientRect().top)), 'Last Payroll Run heading top').toBeLessThanOrEqual(viewport.height - 80);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/learning: the Completion table starts in the first screenful and nothing scrolls sideways', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager'], LEARNING_PERMS), locale, url: '/learning', mocks: LEARNING });
        await expect(page.getByTestId('completion-table')).toBeVisible({ timeout: 60_000 });
        await shot(page, 'learning', locale);
        expect(await topOf(page, '[data-testid="completion-table"]'), 'completion table top').toBeLessThanOrEqual(600);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });
    });
  }
}
