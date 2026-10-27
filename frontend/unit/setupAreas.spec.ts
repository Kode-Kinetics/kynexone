import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS } from '../src/lib/setupModuleSections';
import { COMPANY_OWNED_SETTING_IDS, SETUP_AREAS, SETUP_SECTION_GROUPS, SETUP_SETTING_SECTIONS, destinationAvailability, inSetupOrder, resolveSetupArea, type SetupAccessContext, type SetupDestination } from '../src/lib/setupAreas';

const context: SetupAccessContext = {
  hasPermission: () => true,
  hasRole: () => true,
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
  isCompanyScopedGroup: false,
};
const destinations = () => SETUP_AREAS.flatMap(area => area.destinations);
const destination = (id: string): SetupDestination => {
  const found = destinations().find(item => item.id === id);
  expect(found, `Missing destination ${id}`).toBeDefined();
  return found!;
};

const NAV_GROUP_IDS = SETUP_SECTION_GROUPS.filter(group => group.id !== 'getStarted').map(group => group.id);

test('areas_are_the_navigation_groups_and_hold_exactly_the_sections_the_navigation_shows_there', () => {
  // One taxonomy: All setup areas is derived from the navigation groups, so a section has one home.
  expect(SETUP_AREAS.map(area => area.id)).toEqual(NAV_GROUP_IDS);
  const sections = [...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS];
  for (const area of SETUP_AREAS) {
    const embedded = area.destinations.filter(item => item.path.startsWith('/setup?tab=')).map(item => item.id);
    const expected = inSetupOrder(sections.filter(section => section.group === area.id)).map(section => section.id);
    expect(embedded, area.id).toEqual(expected);
  }
  // Every navigation section (except the Get started tools) appears exactly once, in the area of its group.
  const placed = destinations().filter(item => item.path.startsWith('/setup?tab='));
  expect(new Set(placed.map(item => item.id)).size).toBe(placed.length);
  expect(placed.map(item => item.id).sort()).toEqual(sections.filter(section => section.group !== 'getStarted').map(section => section.id).sort());
  // The placements the old 12-area list got wrong now follow the navigation.
  expect(destination('locations').areaId).toBe('companyStructure');
  expect(destination('departments').areaId).toBe('companyStructure');
  expect(destination('notificationTemplates').areaId).toBe('talentServices');
  expect(destination('adminAuditLogs').areaId).toBe('workspace');
  expect(destination('accessLog').areaId).toBe('accessSecurity');
  expect(destination('gccSettings').areaId).toBe('payrollCompliance');
  expect(destinations().some(item => item.id === 'countryPayrollKeys')).toBe(false);
});

test('every_destination_has_stable_group_and_order', () => {
  for (const area of SETUP_AREAS) {
    const groups = new Map<string, { label: string; order: number; destinations: number[] }>();
    for (const item of area.destinations) {
      expect(item.businessGroup.id, item.id).toMatch(/^[a-z][a-z0-9-]*$/);
      expect(item.businessGroup.label.trim(), item.id).not.toBe('');
      expect(Number.isInteger(item.groupOrder), item.id).toBe(true);
      expect(Number.isInteger(item.destinationOrder), item.id).toBe(true);
      const existing = groups.get(item.businessGroup.id);
      if (existing) {
        expect(item.businessGroup.label, item.id).toBe(existing.label);
        expect(item.groupOrder, item.id).toBe(existing.order);
        existing.destinations.push(item.destinationOrder);
      } else {
        groups.set(item.businessGroup.id, {
          label: item.businessGroup.label,
          order: item.groupOrder,
          destinations: [item.destinationOrder],
        });
      }
    }
    expect([...groups.values()].map(group => group.order).sort((a, b) => a - b)).toEqual(
      [...groups.keys()].map((_, index) => index),
    );
    for (const group of groups.values()) {
      expect(group.destinations.sort((a, b) => a - b)).toEqual(
        group.destinations.map((_, index) => index),
      );
    }
  }
});

test('setup_owned_destinations_carry_the_section_label_and_route_labels_are_unchanged', () => {
  // Wave 1 (2026-10-22): a Setup-owned destination is named exactly as its section is in the navigation and its
  // section header, so the same screen never has two names. Route destinations keep their own labels.
  const expectedLabels: Record<string, string> = {
    people: 'Employee records and contracts', contractRenewals: 'Contract renewals', recruitment: 'Recruitment and workforce planning',
    attendance: 'Attendance operations', shifts: 'Shift rosters', overtime: 'Overtime requests', timesheets: 'Project timesheets',
    leave: 'Leave requests and balances', gradeBenefits: 'Benefits by grade', merit: 'Merit planning and benchmarks',
    payroll: 'Payroll and salary structures', benefits: 'Benefit enrolments', loans: 'Loans and advances',
    learning: 'Learning catalogue and rules', engagement: 'Engagement survey templates', performance: 'Performance cycles',
    succession: 'Succession and development', hrRequests: 'Request center', hrLetters: 'HR letters', offboarding: 'Offboarding and settlement',
    compliance: 'Documents and renewals', saudiComplianceDashboard: 'Saudization and Qiwa status', gosiFiling: 'GOSI filing',
  };
  const sectionIds = new Set([...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS].map(section => section.id as string));
  expect(Object.fromEntries(destinations().filter(item => !sectionIds.has(item.id)).map(item => [item.id, item.label]))).toEqual(expectedLabels);
  expect(destination('gccSettings').label).toBe('Country rules');
  expect(destination('notificationTemplates').label).toBe('Notifications');
  expect(destination('adminAuditLogs').label).toBe('Audit log');
});

test('every_navigation_label_matches_its_studio_destination_label', () => {
  // The navigation and the section header both render SETUP_SETTING_SECTIONS[i].label; the Studio must agree.
  const embedded = destinations().filter(item => item.path.startsWith('/setup?tab='));
  expect(embedded.length).toBeGreaterThan(0);
  for (const item of embedded) {
    const tab = new URLSearchParams(item.path.split('?')[1]).get('tab');
    const section = [...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS].find(entry => entry.id === tab);
    expect(section, `${item.id} has a Setup section`).toBeDefined();
    expect(item.label, item.id).toBe(section!.label);
  }
  // Every section sits in one of the navigation's groups, sentence case, with a description.
  const groups = new Set(SETUP_SECTION_GROUPS.map(group => group.id));
  for (const section of SETUP_SETTING_SECTIONS) {
    expect(groups.has(section.group), section.id).toBe(true);
    expect(section.description.trim(), section.id).not.toBe('');
    expect(section.label.slice(1), `${section.id} is sentence case`).toBe(section.label.slice(1).replace(/\b(?!GL|WPS|SIF)[A-Z]/g, c => c.toLowerCase()));
  }
  expect(SETUP_SETTING_SECTIONS.map(section => section.id)).toEqual([
    'companies', 'branches', 'locations', 'costCenters', 'departments',
    'grades', 'designations', 'establishment',
    'gccSettings', 'statutoryRules', 'fiscalYears', 'glMapping',
    'masterData', 'numberingRules', 'notificationTemplates', 'emailConfig', 'systemSettings', 'adminAuditLogs',
  ]);
  expect(COMPANY_OWNED_SETTING_IDS.has('costCenters')).toBe(true);
});

test('tenant_shared_destinations_have_tenant_effective_scope', () => {
  for (const item of destinations()) {
    expect(item.effectiveTargetScope, item.id).toBe(
      item.id === 'companies' ? 'group' : item.scope === 'mixed' ? 'recordDependent' : item.scope,
    );
  }
});

test('companies_is_an_explicit_group_scope_register', () => {
  expect(destination('companies').effectiveTargetScope).toBe('group');
  expect(destination('companies').scope).toBe('company');
});

test('operations_remain_external_routes', () => {
  const externalOperationIds = [
    'people', 'contractRenewals', 'recruitment', 'attendance', 'shifts', 'overtime', 'timesheets', 'leave',
    'benefits', 'loans', 'performance', 'succession', 'hrRequests', 'hrLetters', 'offboarding', 'compliance',
    'saudiComplianceDashboard', 'gosiFiling',
  ];
  expect(destinations().filter(item => item.kind === 'operations' && item.presentation === 'route').map(item => item.id)).toEqual(externalOperationIds);
  for (const id of externalOperationIds) expect(destination(id).presentation).toBe('route');
  // The audit log is a Setup section (Workspace), not a route.
  expect(destination('adminAuditLogs').presentation).toBe('embedded');
});

test('catalogue_has_unique_destinations_and_known_paths', () => {
  expect(new Set(destinations().map(item => item.id)).size).toBe(destinations().length);
  const allowedPaths = new Set([
    '/setup', '/people', '/contract-renewals', '/recruitment', '/recruitment/onboarding',
    '/attendance', '/shifts', '/overtime', '/timesheets', '/leave', '/benefits',
    '/benefits/by-grade', '/loans', '/compensation', '/payroll', '/payroll/templates',
    '/performance', '/performance/succession', '/learning', '/hr-requests', '/hr-letters',
    '/engagement', '/offboarding', '/compliance', '/compliance-profiles', '/tax-policies',
    '/saudi-compliance', '/gosi-filing',
  ]);
  for (const area of SETUP_AREAS) {
    expect(area.destinations.length).toBeGreaterThan(0);
    for (const item of area.destinations) {
      expect(item.areaId).toBe(area.id);
      const url = new URL(item.path, 'https://catalogue.invalid');
      expect(url.origin).toBe('https://catalogue.invalid');
      expect(allowedPaths.has(url.pathname), item.path).toBe(true);
      expect(item.requiredPermissions.length).toBeGreaterThan(0);
    }
  }
});

test('unknown_area_resolves_to_overview', () => {
  for (const input of [null, '', 'unknown', '/leave', '//example.com', 'https://example.com', 'Organization']) {
    expect(resolveSetupArea(input)).toBe('overview');
  }
  // A saved link naming a former area lands on the navigation group that now holds it.
  expect(resolveSetupArea('leave')).toBe('timeLeave');
  expect(resolveSetupArea('timeLeave')).toBe('timeLeave');
  expect(resolveSetupArea('overview')).toBe('overview');
});

test('canonical_editors_require_route_editor_feature_and_scope_authority', () => {
  // A module's configuration opens in All setup areas for exactly whom its Setup section opens for (whoever may change
  // leave policies: Admin or HR Manager with a leave permission), and follows the module's switch.
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasRole: role => role === 'HR Manager' })).toBe('available');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasRole: role => role === 'HR Officer' })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasRole: role => role === 'Manager' })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasPermission: key => key === 'organization.write' })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, verdictForPath: path => ({ allowed: path !== '/leave' }) })).toBe('unavailable');
  expect(destinationAvailability(destination('onboardingChecklists'), { ...context, hasPermission: key => key === 'recruitment.read' })).toBe('restricted');
  expect(destinationAvailability(destination('onboardingChecklists'), { ...context, hasRole: role => role === 'Recruiter' })).toBe('restricted');
  expect(destinationAvailability(destination('onboardingChecklists'), { ...context, isFeatureEnabled: key => key !== 'recruitment' })).toBe('unavailable');
});

test('restricted_destination_is_not_available', () => {
  expect(destinationAvailability(destination('recruitment'), { ...context, hasPermission: () => false })).toBe('restricted');
  // Route permissions are alternatives, matching PermissionGate; they are not editor authority.
  expect(destinationAvailability(destination('recruitment'), { ...context, hasPermission: key => key === 'recruitment.read' })).toBe('available');
  expect(destinationAvailability(destination('recruitment'), { ...context, flagStatus: 'error', hasPermission: () => false })).toBe('restricted');
});

test('organization_reader_can_open_only_companies_among_setup_tabs', () => {
  const reader = { ...context, hasPermission: (key: string) => key === 'organization.read' };
  expect(destinationAvailability(destination('companies'), reader)).toBe('available');
  expect(destinationAvailability(destination('companies'), { ...reader, isCompanyScopedGroup: true })).toBe('available');
  for (const item of destinations().filter(item => item.presentation === 'embedded' && item.id !== 'companies')) {
    expect(destinationAvailability(item, reader), item.id).toBe('restricted');
  }
});

test('establishment_writer_can_open_existing_setup_tabs', () => {
  const writer = { ...context, hasPermission: (key: string) => key === 'organization.establishment.write' };
  for (const item of destinations().filter(item => item.presentation === 'embedded' && item.path.startsWith('/setup?') && !item.moduleSection)) {
    expect(destinationAvailability(item, writer), item.id).toBe('available');
  }
  // A module's configuration needs that module's own permission, not an organisation one.
  for (const item of destinations().filter(item => item.moduleSection)) {
    expect(destinationAvailability(item, writer), item.id).toBe('restricted');
  }
  expect(destinationAvailability(destination('recruitment'), writer)).toBe('restricted');
});

test('establishment_writer_still_obeys_company_scoped_setup_allowlist', () => {
  const writer = { ...context, isCompanyScopedGroup: true, hasPermission: (key: string) => key === 'organization.establishment.write' };
  for (const id of ['companies', 'branches', 'departments', 'designations', 'grades', 'costCenters', 'glMapping', 'adminAuditLogs']) {
    expect(destinationAvailability(destination(id), writer), id).toBe('available');
  }
  for (const id of ['establishment', 'locations', 'statutoryRules']) {
    expect(destinationAvailability(destination(id), writer), id).toBe('restricted');
  }
});

test('rates_reader_does_not_gain_unrelated_setup_tabs', () => {
  const reader = { ...context, hasPermission: (key: string) => key === 'payroll.rates.read' };
  expect(destinationAvailability(destination('statutoryRules'), reader)).toBe('available');
  for (const item of destinations().filter(item => item.presentation === 'embedded' && item.id !== 'statutoryRules')) {
    expect(destinationAvailability(item, reader), item.id).toBe('restricted');
  }
  expect(destinationAvailability(destination('statutoryRules'), { ...context, hasPermission: key => key === 'payroll.rates.statutory_override' })).toBe('restricted');
});

test('loading_or_disabled_feature_is_not_available', () => {
  expect(destinationAvailability(destination('recruitment'), { ...context, flagStatus: 'loading' })).toBe('checking');
  expect(destinationAvailability(destination('recruitment'), { ...context, isFeatureEnabled: () => false })).toBe('unavailable');
  expect(destinationAvailability(destination('leave'), { ...context, verdictForPath: () => ({ allowed: false }) })).toBe('unavailable');
  expect(destinationAvailability(destination('leave'), { ...context, flagStatus: 'loading' })).toBe('checking');
});

test('flag_error_is_unknown_not_disabled', () => {
  expect(destinationAvailability(destination('recruitment'), { ...context, flagStatus: 'error', isFeatureEnabled: () => false })).toBe('unknown');
  expect(destinationAvailability(destination('leave'), { ...context, flagStatus: 'error' })).toBe('unknown');
});

test('operation_is_not_a_settings_form', () => {
  for (const id of ['people', 'attendance', 'overtime', 'timesheets', 'hrRequests', 'hrLetters', 'offboarding', 'compliance', 'gosiFiling']) {
    expect(destination(id).kind).toBe('operations');
    expect(destination(id).presentation).toBe('route');
    expect(destination(id).assistance).toBe('not-connected');
  }
  // Onboarding checklists are configuration: they open as their Setup section; the onboarding task list stays in Recruitment.
  expect(destination('onboardingChecklists').path).toBe('/setup?tab=onboardingChecklists');
  expect(destination('onboardingChecklists').presentation).toBe('embedded');
  expect(destination('onboardingChecklists').scope).toBe('tenant');
  expect(destination('onboardingChecklists').assistance).toBe('existing-onboarding');
});

test('shift_definitions_and_policy_are_setup_sections_and_rosters_stay_in_shifts', () => {
  expect(destination('shiftDefinitions').kind).toBe('settings');
  expect(destination('shiftPolicy').kind).toBe('settings');
  expect(destination('shifts').kind).toBe('operations');
});

test('company_scoped_group_preserves_existing_setup_restrictions', () => {
  const companyContext = { ...context, isCompanyScopedGroup: true };
  // Departments, designations and grades are per company (2026-10-23), so a company super admin manages their own.
  for (const id of ['companies', 'branches', 'departments', 'designations', 'grades', 'costCenters', 'glMapping', 'adminAuditLogs']) {
    expect(destinationAvailability(destination(id), companyContext), id).toBe('available');
  }
  for (const id of ['locations', 'establishment', 'statutoryRules']) {
    expect(destinationAvailability(destination(id), companyContext), id).toBe('restricted');
  }
  // Modules' configuration keeps its module page's rule, which never narrowed by company scope.
  expect(destinationAvailability(destination('leaveTypes'), companyContext)).toBe('available');
});

test('module_lookup_receives_pathname_without_setup_query', () => {
  expect(destinationAvailability(destination('companies'), {
    ...context,
    verdictForPath: path => ({ allowed: path !== '/setup' }),
  })).toBe('unavailable');
});
