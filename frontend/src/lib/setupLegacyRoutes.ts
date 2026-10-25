/**
 * Where each screen that moved into Setup used to live, so old links, bookmarks and server-sent links (readiness
 * checks, the Saudi compliance action centre, the 402 subscription redirect) still arrive. middleware.ts redirects with
 * it. Deliberately import-free: it runs at the edge.
 *
 * `tab` is the old page's tab. An entry without one is the whole page: it matches the page with no tab, or with a tab
 * it does not otherwise know. A module that keeps its daily work (Leave, Loans, Shifts…) has tab entries only, so its
 * own address still opens it.
 */
export interface SetupLegacyRoute {
  path: string;
  tab?: string;
  /** The Setup section (`/setup?tab=…`) it now opens. */
  section: string;
}

export const SETUP_LEGACY_ROUTES: readonly SetupLegacyRoute[] = [
  // Whole pages.
  { path: '/payroll/templates', section: 'payslipTemplates' },
  { path: '/tax-policies', section: 'taxPolicies' },
  { path: '/compliance-profiles', section: 'complianceProfiles' },
  { path: '/opening-balances', section: 'openingBalances' },
  { path: '/tenant-admin', section: 'planBilling' },
  { path: '/tenant-admin', tab: 'subscription', section: 'planBilling' },
  { path: '/tenant-admin', tab: 'invoices', section: 'planBilling' },
  { path: '/tenant-admin', tab: 'modules', section: 'modules' },
  { path: '/tenant-admin', tab: 'localization', section: 'localization' },
  { path: '/tenant-admin', tab: 'branding', section: 'branding' },
  { path: '/tenant-admin', tab: 'security', section: 'signInSecurity' },
  { path: '/tenant-admin', tab: 'country-rules', section: 'countryPayrollKeys' },
  { path: '/tenant-admin', tab: 'statutory-rules', section: 'statutoryRules' },
  { path: '/tenant-admin', tab: 'help-text', section: 'helpText' },
  { path: '/user-management', section: 'users' },
  { path: '/user-management', tab: 'users', section: 'users' },
  { path: '/user-management', tab: 'roles', section: 'roles' },
  { path: '/user-management', tab: 'permissionMatrix', section: 'permissionMatrix' },
  { path: '/user-management', tab: 'permissions', section: 'permissions' },
  { path: '/user-management', tab: 'grantors', section: 'permissionGrantors' },
  { path: '/user-management', tab: 'delegations', section: 'approvalDelegations' },
  { path: '/user-management', tab: 'authorities', section: 'approvalAuthorities' },
  { path: '/user-management', tab: 'security', section: 'signInSecurity' },
  { path: '/user-management', tab: 'auditLogs', section: 'accessLog' },
  // Setup tabs of modules that keep their daily work.
  { path: '/leave', tab: 'types', section: 'leaveTypes' },
  { path: '/leave', tab: 'policies', section: 'leavePolicies' },
  { path: '/leave', tab: 'holidays', section: 'holidayCalendars' },
  { path: '/shifts', tab: 'definitions', section: 'shiftDefinitions' },
  { path: '/shifts', tab: 'policy', section: 'shiftPolicy' },
  { path: '/overtime', tab: 'policies', section: 'overtimePolicies' },
  { path: '/attendance', tab: 'devices', section: 'attendanceDevices' },
  { path: '/benefits', tab: 'plans', section: 'benefitPlans' },
  { path: '/loans', tab: 'loanTypes', section: 'loanTypes' },
  { path: '/loans', tab: 'loanPolicies', section: 'loanPolicies' },
  { path: '/loans', tab: 'advancePolicy', section: 'advancePolicy' },
  { path: '/loans', tab: 'bonusTypes', section: 'bonusTypes' },
  { path: '/performance', tab: 'templates', section: 'performanceScorecards' },
  { path: '/hr-letters', tab: 'templates', section: 'letterTemplates' },
  { path: '/engagement', tab: 'settings', section: 'engagementPrivacy' },
  { path: '/saudi-compliance', tab: 'configure', section: 'saudiCompliance' },
];

/** Every path that has a legacy entry; middleware.ts matches exactly these. */
export const SETUP_LEGACY_PATHS: readonly string[] = [...new Set(SETUP_LEGACY_ROUTES.map(route => route.path))];

/**
 * The Setup address an old one now leads to, keeping its other query parameters (company, search, deep-link focus), or
 * null when the old address is still a page of its own.
 */
export function legacySetupHref(pathname: string, params: URLSearchParams): string | null {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  const routes = SETUP_LEGACY_ROUTES.filter(route => route.path === path);
  if (routes.length === 0) return null;
  const tab = params.get('tab');
  // /leave?companyId= with nothing else is the old company-readiness link to that company's leave policies; the page
  // itself, narrowed by company, is reached with another parameter (the Setup area breadcrumb adds `setupArea`).
  if (path === '/leave' && tab === null && params.get('companyId') && [...params.keys()].every(key => key === 'companyId'))
    return `/setup?${new URLSearchParams({ tab: 'leavePolicies', companyId: params.get('companyId')! }).toString()}`;
  const match = routes.find(route => tab !== null && route.tab === tab) ?? routes.find(route => route.tab === undefined);
  if (!match) return null;
  const next = new URLSearchParams();
  next.set('tab', match.section);
  // An impersonation request is honoured only on /login (middleware.ts); it never rides along into Setup.
  params.forEach((value, key) => { if (key !== 'tab' && key !== 'impersonate') next.append(key, value); });
  return `/setup?${next.toString()}`;
}
