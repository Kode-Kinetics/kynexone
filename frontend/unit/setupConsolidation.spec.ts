import { expect, test } from '@playwright/test';
import {
  SETUP_MODULE_SECTIONS, SETUP_ROUTE_PERMISSIONS, moduleSectionAvailable, type SetupModuleSection, type SetupModuleAccess,
} from '../src/lib/setupModuleSections';
import { SETUP_LEGACY_PATHS, SETUP_LEGACY_ROUTES, legacySetupHref } from '../src/lib/setupLegacyRoutes';
import { NextRequest } from 'next/server';
import { config, middleware } from '../middleware';
import { SETUP_AREAS, SETUP_SECTION_GROUPS, SETUP_SETTING_SECTIONS, setupHasEntries, setupNavigationModel } from '../src/lib/setupAreas';
import { navigationItems } from '../src/routes/navigation';

/**
 * Setup is the one home for configuration (industry practice: BambooHR, HiBob, Personio, SuccessFactors Admin Center,
 * Salesforce Setup). A module keeps its daily work; what an administrator sets once and changes rarely lives here.
 * This spec pins which screens moved, who may open each one, and where each old address now leads.
 */

const everyone: SetupModuleAccess = {
  hasPermission: () => true,
  hasRole: () => true,
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
};
const section = (id: string): SetupModuleSection => {
  const found = SETUP_MODULE_SECTIONS.find(item => item.id === id);
  expect(found, `Missing Setup section ${id}`).toBeDefined();
  return found!;
};

test('the_moved_screens_and_their_groups_are_exactly_these', () => {
  const byGroup: Record<string, string[]> = {};
  for (const item of SETUP_MODULE_SECTIONS) (byGroup[item.group] ??= []).push(item.id);
  expect(byGroup).toEqual({
    getStarted: ['openingBalances'],
    timeLeave: ['leaveTypes', 'leavePolicies', 'holidayCalendars', 'shiftDefinitions', 'shiftPolicy', 'overtimePolicies', 'attendanceDevices'],
    payBenefits: ['benefitPlans', 'loanTypes', 'loanPolicies', 'advancePolicy', 'bonusTypes', 'payslipTemplates'],
    talentServices: ['onboardingChecklists', 'performanceScorecards', 'letterTemplates', 'engagementPrivacy'],
    payrollCompliance: ['taxPolicies', 'complianceProfiles', 'saudiCompliance', 'countryPayrollKeys'],
    accessSecurity: ['users', 'roles', 'permissionMatrix', 'permissions', 'permissionGrantors', 'approvalAuthorities', 'approvalDelegations', 'signInSecurity', 'accessLog'],
    workspace: ['localization', 'branding', 'helpText', 'modules', 'planBilling'],
  });
});

test('setup_navigation_groups_mirror_the_product_menu', () => {
  expect(SETUP_SECTION_GROUPS.map(group => group.id)).toEqual([
    'getStarted', 'companyStructure', 'jobsHeadcount', 'timeLeave', 'payBenefits', 'talentServices', 'payrollCompliance', 'accessSecurity', 'workspace',
  ]);
});

test('every_section_is_named_described_and_unique_across_setup', () => {
  const ids = [...SETUP_SETTING_SECTIONS.map(item => item.id), ...SETUP_MODULE_SECTIONS.map(item => item.id)];
  expect(new Set(ids).size, 'Setup section ids must be unique').toBe(ids.length);
  for (const item of SETUP_MODULE_SECTIONS) {
    expect(item.label.trim(), item.id).not.toBe('');
    expect(item.description.trim(), item.id).not.toBe('');
    expect(item.permissions.length, `${item.id} must name the permissions that open it`).toBeGreaterThan(0);
    expect(item.modulePath.startsWith('/'), `${item.id} keeps its module's on/off switch`).toBe(true);
  }
});

test('a_section_needs_one_of_its_permissions', () => {
  for (const item of SETUP_MODULE_SECTIONS) {
    expect(moduleSectionAvailable(item, { ...everyone, hasPermission: () => false }), item.id).toBe(false);
    expect(moduleSectionAvailable(item, { ...everyone, hasPermission: key => key === item.permissions[0] }), item.id).toBe(true);
  }
});

test('a_section_with_roles_needs_one_of_them', () => {
  for (const item of SETUP_MODULE_SECTIONS.filter(entry => entry.roles?.length)) {
    expect(moduleSectionAvailable(item, { ...everyone, hasRole: () => false }), item.id).toBe(false);
    expect(moduleSectionAvailable(item, { ...everyone, hasRole: role => role === item.roles![0] }), item.id).toBe(true);
  }
});

test('a_section_follows_its_modules_switch_and_feature_flag', () => {
  for (const item of SETUP_MODULE_SECTIONS) {
    expect(moduleSectionAvailable(item, { ...everyone, verdictForPath: path => ({ allowed: path !== item.modulePath }) }), item.id).toBe(false);
    expect(moduleSectionAvailable(item, { ...everyone, flagStatus: 'loading' }), item.id).toBe(false);
    if (item.featureKey) {
      expect(moduleSectionAvailable(item, { ...everyone, isFeatureEnabled: key => key !== item.featureKey }), item.id).toBe(false);
    }
  }
});

test('the_setup_route_opens_for_every_section_permission_and_no_other', () => {
  const expected = new Set<string>(['organization.read', 'organization.write', 'organization.establishment.write',
    'finance.gl.read', 'finance.gl.manage', 'finance.gl.drivers.manage', 'payroll.rates.read', 'payroll.rates.manage', 'payroll.rates.statutory_override']);
  for (const item of SETUP_MODULE_SECTIONS) item.permissions.forEach(key => expected.add(key));
  expect(new Set(SETUP_ROUTE_PERMISSIONS)).toEqual(expected);
  const menuEntry = navigationItems.find(item => item.path === '/setup');
  expect(new Set(menuEntry?.requiredPermissions)).toEqual(expected);
});

test('a_module_only_user_sees_their_sections_and_nothing_else', () => {
  // A Finance user: loans configuration, no organisation sections, no setup tools.
  const finance = {
    hasPermission: (key: string) => key === 'loans.read',
    hasRole: (role: string) => role === 'Finance',
    isCompanyScopedGroup: false,
    policyIntake: false,
    isFeatureEnabled: () => true,
    verdictForPath: () => ({ allowed: true }),
    flagStatus: 'ready' as const,
  };
  const model = setupNavigationModel(finance);
  expect(model.tools).toEqual([]);
  expect(model.settings.map(item => item.id)).toEqual(['loanTypes', 'advancePolicy', 'bonusTypes']);
});

test('an_organisation_writer_gains_no_module_section_without_its_permission', () => {
  const writer = {
    hasPermission: (key: string) => key === 'organization.write',
    hasRole: () => true,
    isCompanyScopedGroup: false,
    policyIntake: false,
    isFeatureEnabled: () => true,
    verdictForPath: () => ({ allowed: true }),
    flagStatus: 'ready' as const,
  };
  const ids = setupNavigationModel(writer).settings.map(item => item.id);
  for (const item of SETUP_MODULE_SECTIONS) expect(ids, item.id).not.toContain(item.id);
  expect(ids).toContain('grades');
});

test('moved_pages_leave_the_main_menu', () => {
  const paths = navigationItems.map(item => item.path);
  for (const path of ['/payroll/templates', '/compliance-profiles', '/tax-policies', '/tenant-admin', '/opening-balances', '/user-management']) {
    expect(paths, path).not.toContain(path);
  }
});

test('setup_areas_open_moved_screens_inside_setup', () => {
  const destinations = SETUP_AREAS.flatMap(area => area.destinations);
  for (const item of SETUP_MODULE_SECTIONS) {
    const destination = destinations.find(entry => entry.id === item.id);
    if (!destination) continue;
    expect(destination.path, item.id).toBe(`/setup?tab=${item.id}`);
    expect(destination.presentation, item.id).toBe('embedded');
  }
  for (const id of ['payslipTemplates', 'taxPolicies', 'complianceProfiles', 'leaveTypes', 'leavePolicies', 'holidayCalendars', 'onboardingChecklists']) {
    expect(destinations.some(entry => entry.id === id), id).toBe(true);
  }
});

test('every_legacy_route_names_a_real_section', () => {
  const ids = new Set<string>([...SETUP_SETTING_SECTIONS.map(item => item.id), ...SETUP_MODULE_SECTIONS.map(item => item.id)]);
  for (const route of SETUP_LEGACY_ROUTES) expect(ids.has(route.section), `${route.path}?tab=${route.tab}`).toBe(true);
});

test('old_addresses_lead_to_their_setup_section', () => {
  const cases: [string, string, string | null][] = [
    // Whole pages that moved.
    ['/payroll/templates', '', '/setup?tab=payslipTemplates'],
    ['/tax-policies', '', '/setup?tab=taxPolicies'],
    ['/compliance-profiles', '', '/setup?tab=complianceProfiles'],
    ['/opening-balances', '', '/setup?tab=openingBalances'],
    ['/tenant-admin', '', '/setup?tab=planBilling'],
    ['/tenant-admin', 'alert=subscription', '/setup?tab=planBilling&alert=subscription'],
    ['/tenant-admin', 'tab=subscription', '/setup?tab=planBilling'],
    ['/tenant-admin', 'tab=invoices', '/setup?tab=planBilling'],
    ['/tenant-admin', 'tab=modules', '/setup?tab=modules'],
    ['/tenant-admin', 'tab=localization', '/setup?tab=localization'],
    ['/tenant-admin', 'tab=branding', '/setup?tab=branding'],
    ['/tenant-admin', 'tab=security', '/setup?tab=signInSecurity'],
    ['/tenant-admin', 'tab=country-rules', '/setup?tab=countryPayrollKeys'],
    ['/tenant-admin', 'tab=statutory-rules', '/setup?tab=statutoryRules'],
    ['/tenant-admin', 'tab=help-text', '/setup?tab=helpText'],
    ['/user-management', '', '/setup?tab=users'],
    ['/user-management', 'search=amal', '/setup?tab=users&search=amal'],
    ['/user-management', 'companyId=c1&openCreate=1&purpose=companyAdmin', '/setup?tab=users&companyId=c1&openCreate=1&purpose=companyAdmin'],
    ['/user-management', 'tab=roles', '/setup?tab=roles'],
    ['/user-management', 'tab=security', '/setup?tab=signInSecurity'],
    ['/user-management', 'tab=auditLogs', '/setup?tab=accessLog'],
    ['/user-management', 'tab=delegations', '/setup?tab=approvalDelegations'],
    // Setup tabs inside modules that stay.
    ['/leave', 'tab=types', '/setup?tab=leaveTypes'],
    ['/leave', 'companyId=c1&tab=policies', '/setup?tab=leavePolicies&companyId=c1'],
    ['/leave', 'tab=holidays', '/setup?tab=holidayCalendars'],
    ['/shifts', 'tab=definitions', '/setup?tab=shiftDefinitions'],
    ['/shifts', 'tab=policy', '/setup?tab=shiftPolicy'],
    ['/overtime', 'tab=policies', '/setup?tab=overtimePolicies'],
    ['/attendance', 'tab=devices', '/setup?tab=attendanceDevices'],
    ['/benefits', 'tab=plans', '/setup?tab=benefitPlans'],
    ['/loans', 'tab=loanTypes', '/setup?tab=loanTypes'],
    ['/loans', 'tab=loanPolicies', '/setup?tab=loanPolicies'],
    ['/loans', 'tab=advancePolicy', '/setup?tab=advancePolicy'],
    ['/loans', 'tab=bonusTypes', '/setup?tab=bonusTypes'],
    ['/performance', 'tab=templates', '/setup?tab=performanceScorecards'],
    ['/hr-letters', 'tab=templates', '/setup?tab=letterTemplates'],
    ['/engagement', 'tab=settings', '/setup?tab=engagementPrivacy'],
    ['/saudi-compliance', 'tab=configure&section=qiwa', '/setup?tab=saudiCompliance&section=qiwa'],
    // Daily work stays where it is.
    ['/leave', '', null],
    ['/leave', 'companyId=c1', null],
    ['/loans', 'tab=loans', null],
    ['/benefits', '', null],
    ['/shifts', '', null],
    ['/saudi-compliance', '', null],
    ['/saudi-compliance', 'companyId=c1', null],
    ['/recruitment/onboarding', '', null],
    ['/setup', 'tab=users', null],
  ];
  for (const [path, query, expected] of cases) {
    expect(legacySetupHref(path, new URLSearchParams(query)), `${path}?${query}`).toBe(expected);
  }
});

test('the_edge_redirect_runs_on_every_old_address', () => {
  for (const path of SETUP_LEGACY_PATHS) expect(config.matcher, path).toContain(path);
  const moved = middleware(new NextRequest('https://app.example/tenant-admin?tab=branding'));
  // Temporary (307): a browser never caches it, so a section can still move again while the product is in testing.
  expect(moved.status).toBe(307);
  expect(moved.headers.get('location')).toBe('https://app.example/setup?tab=branding');
  const kept = middleware(new NextRequest('https://app.example/leave?companyId=c1'));
  expect(kept.headers.get('location')).toBeNull();
});

test('setup_is_offered_only_to_someone_with_a_section_to_open', () => {
  const base = { isCompanyScopedGroup: false, policyIntake: false, isFeatureEnabled: () => true, verdictForPath: () => ({ allowed: true }), flagStatus: 'ready' as const };
  // An employee reads their own leave and attendance: those permissions alone open no Setup section.
  const employee = { ...base, hasPermission: (key: string) => ['ess.read', 'leave.read', 'attendance.read', 'loans.self'].includes(key), hasRole: (role: string) => role === 'Employee' };
  expect(setupHasEntries(employee)).toBe(false);
  const hrOfficer = { ...base, hasPermission: (key: string) => key === 'leave.read', hasRole: (role: string) => role === 'HR Officer' };
  expect(setupHasEntries(hrOfficer)).toBe(true);
  // The menu entry asks the same question, not only "holds one of the permissions".
  expect(navigationItems.find(item => item.path === '/setup')?.requiresSetupSection).toBe(true);
});
