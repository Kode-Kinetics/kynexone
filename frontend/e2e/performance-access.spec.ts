import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  PERFORMANCE_MODULE_PERMISSIONS,
  PERFORMANCE_TAB_PERMISSIONS,
  canOpenPerformanceTab,
  landingPerformanceTab,
  performanceCapabilities,
} from '../src/lib/performanceAccess';
import type { PerformanceTab } from '../src/lib/performanceAccess';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

/** The performance keys each seeded role holds (backend AuthSeeder.EnsureTenantRolesAsync). */
const SEEDED: Record<string, string[]> = {
  'HR Manager': ['performance.read', 'performance.write', 'performance.approve', 'performance.cycle_manage'],
  Manager: ['performance.read', 'performance.write'],
  Employee: ['performance.read'],
  'Payroll Officer': [],
};
const as = (role: string) => (permission: string) => SEEDED[role].includes(permission);
const tabsFor = (role: string) =>
  (Object.keys(PERFORMANCE_TAB_PERMISSIONS) as PerformanceTab[]).filter(t => canOpenPerformanceTab(t, as(role)));

/**
 * F08 — the Performance screen for each audience. On main the menu and the page needed performance.read or
 * performance.write, which no seeded role below HR Director held, and inside the page every tab and button
 * was shown to everyone who got in: a line manager was offered Publish, Advance and the calibration board,
 * all of which the API refuses to them.
 */
test.describe('Performance — who sees what', () => {
  test('an employee sees their own review, their goals and feedback, and lands on their review', () => {
    expect(tabsFor('Employee')).toEqual(['my-reviews', 'goals', 'feedback']);
    expect(landingPerformanceTab(as('Employee'))).toBe('my-reviews');
    expect(performanceCapabilities(as('Employee'))).toEqual({ setUpCycles: false, writeReviews: false, decideRatings: false });
  });

  test('a line manager reviews their team and sets goals, but is never offered calibration, publishing or set-up', () => {
    const tabs = tabsFor('Manager');
    expect(tabs).toEqual(['cycles', 'my-reviews', 'team-reviews', 'goals', 'pip', 'feedback']);
    for (const hrOnly of ['overview', 'calibration', 'recommendations', 'analytics', 'templates'] as const) {
      expect(tabs).not.toContain(hrOnly);
    }
    expect(landingPerformanceTab(as('Manager'))).toBe('team-reviews');
    expect(performanceCapabilities(as('Manager'))).toEqual({ setUpCycles: false, writeReviews: true, decideRatings: false });
    // A link straight to HR's calibration board still lands a manager on their own team.
    expect(landingPerformanceTab(as('Manager'), 'calibration')).toBe('team-reviews');
  });

  test('HR Manager runs the whole cycle and lands on the overview', () => {
    expect(tabsFor('HR Manager')).toEqual(Object.keys(PERFORMANCE_TAB_PERMISSIONS));
    expect(landingPerformanceTab(as('HR Manager'))).toBe('overview');
    expect(landingPerformanceTab(as('HR Manager'), 'calibration')).toBe('calibration');
    expect(performanceCapabilities(as('HR Manager'))).toEqual({ setUpCycles: true, writeReviews: true, decideRatings: true });
  });

  test('a role with no performance key does not get the module at all', () => {
    expect(PERFORMANCE_MODULE_PERMISSIONS.some(as('Payroll Officer'))).toBe(false);
    expect(tabsFor('Payroll Officer')).toEqual([]);
    expect(landingPerformanceTab(as('Payroll Officer'))).toBeNull();
  });

  test('the tenant-wide tabs need the HR tier, because their API is performance.approve', () => {
    for (const tab of ['overview', 'analytics', 'calibration'] as const) {
      expect(PERFORMANCE_TAB_PERMISSIONS[tab]).toEqual(['performance.approve']);
    }
  });

  test('the menu and the page gates use the same rule', () => {
    expect(read('src/routes/navigation.ts')).toContain(
      "path: '/performance', requiredPermissions: PERFORMANCE_MODULE_PERMISSIONS");
    expect(read('app/(dashboard)/performance/page.tsx')).toContain('permissions={PERFORMANCE_MODULE_PERMISSIONS}');
    expect(read('app/(dashboard)/performance/calibration/page.tsx')).toContain('PERFORMANCE_TAB_PERMISSIONS.calibration');
    expect(read('app/(dashboard)/performance/pip/page.tsx')).toContain('PERFORMANCE_TAB_PERMISSIONS.pip');
  });

  test('every step on the page is offered only to the tier the API accepts it from', () => {
    const page = read('src/views/PerformancePage.tsx');
    // No role-name shortcuts: a custom role with the key gets the button, a Deny override removes it.
    expect(page).not.toMatch(/hasRole|GOAL_APPROVER_ROLES/);
    expect(page).toContain('const tabs = TABS.filter(t => canOpenPerformanceTab(t.id, hasPermission));');
    expect(page).toContain('{tabs.map(t => (');
    // HR set-up.
    expect(page).toContain("{can.setUpCycles && (\n          <button type=\"button\" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className=\"h-4 w-4\" /> New Cycle</button>");
    expect(page).toContain("{c.status === 'Draft' && can.setUpCycles && (");
    expect(page).toContain("['Active', 'InReview', 'Calibration', 'FinalApproval'].includes(c.status) && can.setUpCycles && (");
    // HR decisions.
    expect(page).toContain("{r.status === 'FinalApproval' && can.decideRatings && (");
    expect(page).toContain('{can.decideRatings && <OpenAppealsPanel onResolved={load} />}');
    expect(page).toContain("{p.status === 'Active' && can.decideRatings && (");
    expect(page).toContain("{p.status === 'ManagerReviewed' && can.decideRatings && (");
    // The line manager's steps.
    expect(page).toContain("['SelfAssessmentSubmitted', 'ManagerReview'].includes(r.status) && can.writeReviews && (");
    expect(page).toContain('const canApproveGoals = can.writeReviews;');
  });
});
