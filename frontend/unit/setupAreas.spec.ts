import { expect, test } from '@playwright/test';
import { COMPANY_OWNED_SETTING_IDS, SETUP_AREAS, SETUP_SECTION_GROUPS, SETUP_SETTING_SECTIONS, destinationAvailability, resolveSetupArea, type SetupAccessContext, type SetupDestination } from '../src/lib/setupAreas';

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

test('catalogue_keeps_exactly_twelve_areas_and_all_existing_destinations', () => {
  expect(SETUP_AREAS.map(area => ({
    id: area.id,
    destinations: area.destinations.map(item => [item.id, item.path]),
  }))).toEqual([
    { id: 'organization', destinations: [
      ['companies', '/setup?tab=companies'], ['branches', '/setup?tab=branches'],
      ['departments', '/setup?tab=departments'], ['designations', '/setup?tab=designations'],
      ['grades', '/setup?tab=grades'], ['costCenters', '/setup?tab=costCenters'],
      ['establishment', '/setup?tab=establishment'], ['masterData', '/setup?tab=masterData'],
      ['people', '/people'], ['contractRenewals', '/contract-renewals'],
    ] },
    { id: 'recruitment', destinations: [['recruitment', '/recruitment']] },
    { id: 'onboarding', destinations: [['onboarding', '/recruitment/onboarding']] },
    { id: 'time', destinations: [
      ['locations', '/setup?tab=locations'], ['attendance', '/attendance'], ['shifts', '/shifts'],
      ['overtime', '/overtime'], ['timesheets', '/timesheets'],
    ] },
    { id: 'leave', destinations: [
      ['leaveTypes', '/leave'], ['leavePolicies', '/leave'],
      ['holidayCalendars', '/leave'], ['leave', '/leave'],
    ] },
    { id: 'compensation', destinations: [
      ['benefits', '/benefits'], ['gradeBenefits', '/benefits/by-grade'],
      ['loans', '/loans'], ['merit', '/compensation'],
    ] },
    { id: 'payroll', destinations: [
      ['glMapping', '/setup?tab=glMapping'], ['fiscalYears', '/setup?tab=fiscalYears'],
      ['payroll', '/payroll'], ['payslipTemplates', '/payroll/templates'],
    ] },
    { id: 'performance', destinations: [
      ['performance', '/performance'], ['succession', '/performance/succession'],
    ] },
    { id: 'learning', destinations: [['learning', '/learning']] },
    { id: 'services', destinations: [
      ['hrRequests', '/hr-requests'], ['hrLetters', '/hr-letters'],
      ['engagement', '/engagement'], ['notificationTemplates', '/setup?tab=notificationTemplates'],
    ] },
    { id: 'offboarding', destinations: [['offboarding', '/offboarding']] },
    { id: 'compliance', destinations: [
      ['gccSettings', '/setup?tab=gccSettings'], ['statutoryRules', '/setup?tab=statutoryRules'],
      ['complianceProfiles', '/compliance-profiles'], ['taxPolicies', '/tax-policies'],
      ['compliance', '/compliance'], ['saudiCompliance', '/saudi-compliance'],
      ['gosiFiling', '/gosi-filing'], ['adminAuditLogs', '/setup?tab=adminAuditLogs'],
    ] },
  ]);
  expect(destinations()).toHaveLength(45);
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
  expect(SETUP_AREAS.map(area => {
    const groups = new Map<string, { label: string; order: number; destinations: { id: string; order: number }[] }>();
    for (const item of area.destinations) {
      const group = groups.get(item.businessGroup.id) ?? {
        label: item.businessGroup.label,
        order: item.groupOrder,
        destinations: [],
      };
      group.destinations.push({ id: item.id, order: item.destinationOrder });
      groups.set(item.businessGroup.id, group);
    }
    return {
      areaId: area.id,
      groups: [...groups].map(([id, group]) => ({
        id,
        label: group.label,
        order: group.order,
        destinations: group.destinations.sort((a, b) => a.order - b.order).map(item => item.id),
      })).sort((a, b) => a.order - b.order),
    };
  })).toEqual([
    { areaId: 'organization', groups: [
      { id: 'legal-entities-structure', label: 'Legal entities and structure', order: 0, destinations: ['companies', 'branches', 'departments', 'designations', 'grades'] },
      { id: 'financial-structure', label: 'Financial structure', order: 1, destinations: ['costCenters', 'establishment'] },
      { id: 'employee-data-definitions', label: 'Employee data definitions', order: 2, destinations: ['masterData'] },
      { id: 'related-operations', label: 'Related operations', order: 3, destinations: ['people', 'contractRenewals'] },
    ] },
    { areaId: 'recruitment', groups: [
      { id: 'recruitment-operations', label: 'Recruitment operations', order: 0, destinations: ['recruitment'] },
    ] },
    { areaId: 'onboarding', groups: [
      { id: 'templates-tasks', label: 'Templates and tasks', order: 0, destinations: ['onboarding'] },
    ] },
    { areaId: 'time', groups: [
      { id: 'work-configuration', label: 'Work configuration', order: 0, destinations: ['locations', 'shifts'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['attendance', 'overtime', 'timesheets'] },
    ] },
    { areaId: 'leave', groups: [
      { id: 'policy-definitions', label: 'Policy definitions', order: 0, destinations: ['leaveTypes', 'leavePolicies', 'holidayCalendars'] },
      { id: 'related-workspace', label: 'Related workspace', order: 1, destinations: ['leave'] },
    ] },
    { areaId: 'compensation', groups: [
      { id: 'plans-entitlements', label: 'Plans and entitlements', order: 0, destinations: ['benefits', 'gradeBenefits', 'loans', 'merit'] },
    ] },
    { areaId: 'payroll', groups: [
      { id: 'posting-calendars', label: 'Posting and calendars', order: 0, destinations: ['glMapping', 'fiscalYears'] },
      { id: 'payroll-configuration', label: 'Payroll configuration', order: 1, destinations: ['payroll', 'payslipTemplates'] },
    ] },
    { areaId: 'performance', groups: [
      { id: 'cycles-templates', label: 'Cycles and templates', order: 0, destinations: ['performance'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['succession'] },
    ] },
    { areaId: 'learning', groups: [
      { id: 'catalogue-rules', label: 'Catalogue and rules', order: 0, destinations: ['learning'] },
    ] },
    { areaId: 'services', groups: [
      { id: 'templates-communication', label: 'Templates and communication', order: 0, destinations: ['engagement', 'notificationTemplates'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['hrRequests', 'hrLetters'] },
    ] },
    { areaId: 'offboarding', groups: [
      { id: 'exit-operations', label: 'Exit operations', order: 0, destinations: ['offboarding'] },
    ] },
    { areaId: 'compliance', groups: [
      { id: 'rules-profiles', label: 'Rules and profiles', order: 0, destinations: ['gccSettings', 'statutoryRules', 'complianceProfiles', 'taxPolicies'] },
      { id: 'filings-evidence', label: 'Filings and evidence', order: 1, destinations: ['compliance', 'saudiCompliance', 'gosiFiling', 'adminAuditLogs'] },
    ] },
  ]);
});

test('setup_owned_destinations_carry_the_section_label_and_route_labels_are_unchanged', () => {
  // Wave 1 (2026-10-22): a Setup-owned destination is named exactly as its section is in the navigation and its
  // section header, so the same screen never has two names. Route destinations keep their own labels.
  const expectedLabels: Record<string, string> = {
    companies: 'Companies', branches: 'Branches', departments: 'Departments', designations: 'Designations', grades: 'Grades',
    costCenters: 'Cost centres', establishment: 'Headcount & budget', masterData: 'Drop-down lists', people: 'Employee records and contracts',
    contractRenewals: 'Contract renewals', recruitment: 'Recruitment and workforce planning', onboarding: 'Onboarding checklists',
    locations: 'Locations', attendance: 'Attendance operations', shifts: 'Shift definitions and policy',
    overtime: 'Overtime requests', timesheets: 'Project timesheets', leaveTypes: 'Leave types', leavePolicies: 'Leave policies',
    holidayCalendars: 'Holiday calendars', leave: 'Leave policies and holidays', benefits: 'Benefit plans and enrollments',
    gradeBenefits: 'Benefits by grade', loans: 'Loan and advance policies', merit: 'Merit planning and benchmarks',
    glMapping: 'Payroll accounting', fiscalYears: 'Fiscal years', payroll: 'Payroll and salary structures',
    payslipTemplates: 'Payslip templates', performance: 'Performance cycles and templates', succession: 'Succession and development',
    learning: 'Learning catalogue and rules', hrRequests: 'Request center', hrLetters: 'HR letters',
    engagement: 'Engagement templates and privacy', notificationTemplates: 'Notifications',
    offboarding: 'Offboarding and settlement', gccSettings: 'Country rules', statutoryRules: 'Statutory rules',
    complianceProfiles: 'Compliance profiles', taxPolicies: 'Tax policies', compliance: 'Documents and renewals',
    saudiCompliance: 'Saudi compliance', gosiFiling: 'GOSI filing', adminAuditLogs: 'Audit log',
  };
  expect(Object.fromEntries(destinations().map(item => [item.id, item.label]))).toEqual(expectedLabels);
});

test('every_navigation_label_matches_its_studio_destination_label', () => {
  // The navigation and the section header both render SETUP_SETTING_SECTIONS[i].label; the Studio must agree.
  const embedded = destinations().filter(item => item.path.startsWith('/setup?tab='));
  expect(embedded.length).toBeGreaterThan(0);
  for (const item of embedded) {
    const tab = new URLSearchParams(item.path.split('?')[1]).get('tab');
    const section = SETUP_SETTING_SECTIONS.find(entry => entry.id === tab);
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
    'people', 'contractRenewals', 'recruitment', 'attendance', 'overtime', 'timesheets',
    'succession', 'hrRequests', 'hrLetters', 'offboarding', 'compliance', 'saudiCompliance', 'gosiFiling',
  ];
  expect(destinations().filter(item => item.kind === 'operations' && item.presentation === 'route').map(item => item.id)).toEqual(externalOperationIds);
  for (const id of externalOperationIds) expect(destination(id).presentation).toBe('route');
  // Preserve the existing Setup-owned audit-log surface rather than reclassifying it as a route.
  expect(destination('adminAuditLogs').presentation).toBe('embedded');
});

test('catalogue_has_twelve_unique_areas', () => {
  expect(SETUP_AREAS.map(area => area.id)).toEqual([
    'organization', 'recruitment', 'onboarding', 'time', 'leave', 'compensation',
    'payroll', 'performance', 'learning', 'services', 'offboarding', 'compliance',
  ]);
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
  expect(resolveSetupArea('leave')).toBe('leave');
  expect(resolveSetupArea('overview')).toBe('overview');
});

test('canonical_editors_require_route_editor_feature_and_scope_authority', () => {
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasRole: role => role === 'HR Officer' })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, hasPermission: key => key === 'organization.write' })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, isCompanyScopedGroup: true })).toBe('restricted');
  expect(destinationAvailability(destination('leavePolicies'), { ...context, verdictForPath: () => ({ allowed: false }) })).toBe('unavailable');
  expect(destinationAvailability(destination('onboarding'), { ...context, hasPermission: key => key === 'recruitment.read' })).toBe('restricted');
  expect(destinationAvailability(destination('onboarding'), { ...context, hasRole: role => role === 'Recruiter' })).toBe('restricted');
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
  for (const item of destinations().filter(item => item.presentation === 'embedded' && item.path.startsWith('/setup?'))) {
    expect(destinationAvailability(item, writer), item.id).toBe('available');
  }
  expect(destinationAvailability(destination('recruitment'), writer)).toBe('restricted');
});

test('establishment_writer_still_obeys_company_scoped_setup_allowlist', () => {
  const writer = { ...context, isCompanyScopedGroup: true, hasPermission: (key: string) => key === 'organization.establishment.write' };
  for (const id of ['companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']) {
    expect(destinationAvailability(destination(id), writer), id).toBe('available');
  }
  for (const id of ['establishment', 'departments', 'grades', 'statutoryRules']) {
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
  expect(destination('onboarding').path).toBe('/recruitment/onboarding');
  expect(destination('onboarding').kind).toBe('templates');
  expect(destination('onboarding').scope).toBe('tenant');
  expect(destination('onboarding').assistance).toBe('existing-onboarding');
});

test('shift_definitions_and_policy_are_existing_settings', () => {
  expect(destination('shifts').kind).toBe('settings');
});

test('company_scoped_group_preserves_existing_setup_restrictions', () => {
  const companyContext = { ...context, isCompanyScopedGroup: true };
  for (const id of ['companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']) {
    expect(destinationAvailability(destination(id), companyContext), id).toBe('available');
  }
  for (const id of ['departments', 'grades', 'locations', 'establishment', 'statutoryRules', 'onboarding']) {
    expect(destinationAvailability(destination(id), companyContext), id).toBe('restricted');
  }
});

test('module_lookup_receives_pathname_without_setup_query', () => {
  expect(destinationAvailability(destination('companies'), {
    ...context,
    verdictForPath: path => ({ allowed: path !== '/setup' }),
  })).toBe('unavailable');
});
