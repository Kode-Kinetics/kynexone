import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { PERFORMANCE_MODULE_PERMISSIONS } from '../src/lib/performanceAccess';
import { navigationGroups, navigationHints } from '../src/routes/navigation';

/**
 * The sidebar regrouping: Talent and Finance are their own groups, Offboarding sits with HR & Time, the payroll
 * and compliance screens left Administration. Only the grouping and two labels changed; every item keeps its path
 * and its access rules (the backend NavigationPageGateParityTests read the same file).
 */
const items = navigationGroups.flatMap(g => g.items.map(item => ({ group: g.label, item })));
const find = (path: string) => items.find(x => x.item.path === path);

/** Every path in the menu BEFORE the regrouping (37 items). Adding or dropping a screen is a deliberate edit here. */
const ALL_PATHS = [
  '/dashboard', '/group', '/ess',
  '/people', '/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks', '/org-chart', '/hr-letters',
  '/attendance', '/leave', '/shifts', '/overtime', '/timesheets',
  '/payroll', '/loans', '/benefits', '/benefits/by-grade', '/benefits/usage', '/contract-renewals', '/compensation',
  '/recruitment', '/offboarding', '/performance', '/engagement', '/performance/succession', '/learning', '/compliance',
  '/ai-assistant', '/reports',
  '/hr-requests', '/approvals', '/pay-approvals', '/saudi-compliance', '/gosi-filing', '/setup', '/payroll/variance',
];

test('the regrouping lost no screen and added none', () => {
  expect(items.map(x => x.item.path).sort()).toEqual([...ALL_PATHS].sort());
  expect(new Set(items.map(x => x.item.path)).size).toBe(items.length);
});

test('the groups, in order', () => {
  expect(navigationGroups.map(g => g.label)).toEqual(['Overview', 'HR & Time', 'Talent', 'Finance', 'Compliance', 'Insights & Reports', 'Administration']);
  expect(navigationGroups.some(g => g.label === 'Finance & Talent')).toBe(false);
});

const EXPECTED: Array<[path: string, label: string, group: string]> = [
  ['/offboarding', 'Offboarding', 'HR & Time'],
  ['/recruitment', 'Recruitment', 'Talent'],
  ['/performance', 'Performance', 'Talent'],
  ['/performance/succession', 'Succession', 'Talent'],
  ['/learning', 'Training & Learning', 'Talent'],
  ['/engagement', 'Surveys & Engagement', 'Talent'],
  ['/payroll', 'Payroll', 'Finance'],
  ['/pay-approvals', 'Pay approvals', 'Finance'],
  ['/gosi-filing', 'GOSI Reconciliation', 'Finance'],
  ['/payroll/variance', 'Parallel Run Variance', 'Finance'],
  ['/loans', 'Loans & Advances', 'Finance'],
  ['/benefits', 'Benefits', 'Finance'],
  ['/benefits/by-grade', 'Benefits by grade', 'Finance'],
  ['/contract-renewals', 'Contract renewals', 'Finance'],
  ['/compensation', 'Merit planning', 'Finance'],
  ['/compliance', 'Compliance', 'Compliance'],
  ['/saudi-compliance', 'Saudi Compliance', 'Compliance'],
  ['/hr-requests', 'Request Center', 'Administration'],
  ['/approvals', 'Approvals', 'Administration'],
  ['/setup', 'Setup', 'Administration'],
];
for (const [path, label, group] of EXPECTED) {
  test(`${path} is "${label}" in ${group}`, () => {
    const found = find(path);
    expect(found, path).toBeTruthy();
    expect(found!.item.label).toBe(label);
    expect(found!.group).toBe(group);
  });
}

test('the unmoved groups keep their entries', () => {
  const inGroup = (g: string) => items.filter(x => x.group === g).map(x => x.item.path);
  expect(inGroup('Overview')).toEqual(['/dashboard', '/group', '/ess']);
  expect(inGroup('Insights & Reports')).toEqual(['/ai-assistant', '/reports']);
  expect(inGroup('HR & Time')).toEqual([
    '/people', '/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks', '/org-chart', '/hr-letters',
    '/attendance', '/leave', '/shifts', '/overtime', '/timesheets', '/offboarding',
  ]);
  expect(inGroup('Talent')).toEqual(['/recruitment', '/performance', '/performance/succession', '/learning', '/engagement']);
  expect(inGroup('Compliance')).toEqual(['/compliance', '/saudi-compliance']);
});

/** Access of every moved item as it was before: permissions, feature flag and the Release A hide flag. */
const ACCESS: Record<string, { perms: readonly string[]; feature?: string; hiddenWhen?: string }> = {
  '/offboarding': { perms: ['employees.write', 'payroll.approve'] },
  '/recruitment': { perms: ['recruitment.read', 'recruitment.write', 'recruitment.requisition.raise', 'recruitment.interview.feedback'], feature: 'recruitment' },
  '/performance': { perms: PERFORMANCE_MODULE_PERMISSIONS, feature: 'performance' },
  '/performance/succession': {
    perms: ['succession.read', 'succession.positions.manage', 'succession.pools.manage', 'succession.nominate', 'succession.assess', 'succession.calibrate',
      'succession.slates.read', 'succession.slates.manage', 'succession.devplans.read', 'succession.devplans.manage', 'succession.reports.read'],
    feature: 'succession',
  },
  '/learning': { perms: ['learning.read', 'learning.manage', 'learning.assign', 'learning.report', 'learning.records.correct'], feature: 'learning' },
  '/engagement': { perms: ['engagement.read', 'engagement.manage', 'engagement.actions.manage'], feature: 'engagement' },
  '/payroll': { perms: ['payroll.read'], feature: 'payroll' },
  '/pay-approvals': { perms: ['payroll.approve'] },
  '/gosi-filing': { perms: ['payroll.read'] },
  '/payroll/variance': { perms: ['payroll.read'] },
  '/loans': { perms: ['loans.self', 'loans.read', 'loans.write'] },
  '/benefits': { perms: ['employees.write'], hiddenWhen: 'release_a' },
  '/benefits/by-grade': { perms: ['entitlements.read'], feature: 'release_a' },
  '/benefits/usage': { perms: ['entitlements.read', 'benefits.usage.approve'], feature: 'release_a' },
  '/contract-renewals': { perms: ['contracts.renewal.read'], feature: 'release_a' },
  '/compensation': {
    perms: ['compensation.read', 'compensation.plan', 'compensation.manage', 'compensation.approve', 'compensation.benchmarks.manage', 'compensation.equity.read'],
    feature: 'compensation',
  },
  '/compliance': { perms: ['compliance.read', 'compliance.write'], feature: 'compliance' },
  '/saudi-compliance': { perms: ['compliance.read', 'qiwa.read'] },
};
for (const [path, access] of Object.entries(ACCESS)) {
  test(`${path} keeps its permissions and feature gates`, () => {
    const item = find(path)!.item;
    expect([...(item.requiredPermissions ?? [])]).toEqual([...access.perms]);
    expect(item.requiredFeatureKey).toBe(access.feature);
    expect(item.hiddenWhenFeatureKey).toBe(access.hiddenWhen);
    expect(item.groupAccountOnly).toBeUndefined();
    expect(navigationHints[path], `hint for ${path}`).toBeTruthy();
  });
}

test('the new groups and the two renamed labels have Arabic', () => {
  for (const key of ['Talent', 'Finance', 'Compliance', 'Training & Learning', 'Surveys & Engagement']) {
    expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
    expect(LOCALE_DICTS.ar[key], key).not.toBe(key);
  }
  expect(LOCALE_DICTS.ar['Talent']).toBe('المواهب');
  expect(LOCALE_DICTS.ar['Finance']).toBe('المالية');
  expect(LOCALE_DICTS.ar['Compliance']).toBe('الامتثال');
  expect(LOCALE_DICTS.ar['Training & Learning']).toBe('التدريب والتعلّم');
  expect(LOCALE_DICTS.ar['Surveys & Engagement']).toBe('الاستطلاعات والمشاركة');
});
