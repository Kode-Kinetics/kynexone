import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS } from '../src/lib/setupModuleSections';
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
    { id: 'onboarding', destinations: [['onboardingChecklists', '/setup?tab=onboardingChecklists'], ['onboardingChecklistTemplates', '/setup?tab=onboardingChecklistTemplates'], ['hireTemplates', '/setup?tab=hireTemplates']] },
    { id: 'time', destinations: [
      ['locations', '/setup?tab=locations'], ['attendance', '/attendance'],
      ['shiftDefinitions', '/setup?tab=shiftDefinitions'], ['shiftPolicy', '/setup?tab=shiftPolicy'],
      ['attendanceRules', '/setup?tab=attendanceRules'], ['overtimePolicies', '/setup?tab=overtimePolicies'], ['attendanceDevices', '/setup?tab=attendanceDevices'],
      ['shifts', '/shifts'], ['overtime', '/overtime'], ['timesheets', '/timesheets'],
    ] },
    { id: 'leave', destinations: [
      ['leaveTypes', '/setup?tab=leaveTypes'], ['leavePolicies', '/setup?tab=leavePolicies'],
      ['holidayCalendars', '/setup?tab=holidayCalendars'], ['leave', '/leave'],
    ] },
    { id: 'compensation', destinations: [
      ['benefitPlans', '/setup?tab=benefitPlans'], ['gradeBenefits', '/benefits/by-grade'],
      ['loanTypes', '/setup?tab=loanTypes'], ['loanPolicies', '/setup?tab=loanPolicies'],
      ['advancePolicy', '/setup?tab=advancePolicy'], ['bonusTypes', '/setup?tab=bonusTypes'],
      ['merit', '/compensation'], ['benefits', '/benefits'], ['loans', '/loans'],
    ] },
    { id: 'payroll', destinations: [
      ['glMapping', '/setup?tab=glMapping'], ['fiscalYears', '/setup?tab=fiscalYears'],
      ['payroll', '/payroll'], ['payslipTemplates', '/setup?tab=payslipTemplates'],
    ] },
    { id: 'performance', destinations: [
      ['performanceScorecards', '/setup?tab=performanceScorecards'],
      ['performance', '/performance'], ['succession', '/performance/succession'],
    ] },
    { id: 'learning', destinations: [['learning', '/learning']] },
    { id: 'services', destinations: [
      ['hrRequests', '/hr-requests'], ['hrLetters', '/hr-letters'],
      ['letterTemplates', '/setup?tab=letterTemplates'], ['engagement', '/engagement'],
      ['engagementPrivacy', '/setup?tab=engagementPrivacy'], ['notificationTemplates', '/setup?tab=notificationTemplates'],
    ] },
    { id: 'offboarding', destinations: [['offboarding', '/offboarding']] },
    { id: 'compliance', destinations: [
      ['gccSettings', '/setup?tab=gccSettings'], ['statutoryRules', '/setup?tab=statutoryRules'],
      ['complianceProfiles', '/setup?tab=complianceProfiles'], ['taxPolicies', '/setup?tab=taxPolicies'],
      ['saudiCompliance', '/setup?tab=saudiCompliance'],
      ['compliance', '/compliance'], ['saudiComplianceDashboard', '/saudi-compliance'],
      ['gosiFiling', '/gosi-filing'], ['adminAuditLogs', '/setup?tab=adminAuditLogs'],
    ] },
  ]);
  expect(destinations()).toHaveLength(61);
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
      { id: 'templates-tasks', label: 'Templates and tasks', order: 0, destinations: ['onboardingChecklists', 'onboardingChecklistTemplates', 'hireTemplates'] },
    ] },
    { areaId: 'time', groups: [
      { id: 'work-configuration', label: 'Work configuration', order: 0, destinations: ['locations', 'shiftDefinitions', 'shiftPolicy', 'attendanceRules', 'overtimePolicies', 'attendanceDevices'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['attendance', 'overtime', 'timesheets', 'shifts'] },
    ] },
    { areaId: 'leave', groups: [
      { id: 'policy-definitions', label: 'Policy definitions', order: 0, destinations: ['leaveTypes', 'leavePolicies', 'holidayCalendars'] },
      { id: 'related-workspace', label: 'Related workspace', order: 1, destinations: ['leave'] },
    ] },
    { areaId: 'compensation', groups: [
      { id: 'plans-entitlements', label: 'Plans and entitlements', order: 0, destinations: ['benefitPlans', 'gradeBenefits', 'loanTypes', 'loanPolicies', 'advancePolicy', 'bonusTypes', 'merit'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['benefits', 'loans'] },
    ] },
    { areaId: 'payroll', groups: [
      { id: 'posting-calendars', label: 'Posting and calendars', order: 0, destinations: ['glMapping', 'fiscalYears'] },
      { id: 'payroll-configuration', label: 'Payroll configuration', order: 1, destinations: ['payroll', 'payslipTemplates'] },
    ] },
    { areaId: 'performance', groups: [
      { id: 'cycles-templates', label: 'Cycles and templates', order: 0, destinations: ['performanceScorecards'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['succession', 'performance'] },
    ] },
    { areaId: 'learning', groups: [
      { id: 'catalogue-rules', label: 'Catalogue and rules', order: 0, destinations: ['learning'] },
    ] },
    { areaId: 'services', groups: [
      { id: 'templates-communication', label: 'Templates and communication', order: 0, destinations: ['letterTemplates', 'engagement', 'engagementPrivacy', 'notificationTemplates'] },
      { id: 'related-operations', label: 'Related operations', order: 1, destinations: ['hrRequests', 'hrLetters'] },
    ] },
    { areaId: 'offboarding', groups: [
      { id: 'exit-operations', label: 'Exit operations', order: 0, destinations: ['offboarding'] },
    ] },
    { areaId: 'compliance', groups: [
      { id: 'rules-profiles', label: 'Rules and profiles', order: 0, destinations: ['gccSettings', 'statutoryRules', 'complianceProfiles', 'taxPolicies', 'saudiCompliance'] },
      { id: 'filings-evidence', label: 'Filings and evidence', order: 1, destinations: ['compliance', 'saudiComplianceDashboard', 'gosiFiling', 'adminAuditLogs'] },
    ] },
  ]);
});

test('setup_owned_destinations_carry_the_section_label_and_route_labels_are_unchanged', () => {
  // Wave 1 (2026-10-22): a Setup-owned destination is named exactly as its section is in the navigation and its
  // section header, so the same screen never has two names. Route destinations keep their own labels.
  const expectedLabels: Record<string, string> = {
    companies: 'Companies', branches: 'Branches', departments: 'Departments', designations: 'Designations', grades: 'Grades',
    costCenters: 'Cost centres', establishment: 'Headcount & budget', masterData: 'Drop-down lists', people: 'Employee records and contracts',
    contractRenewals: 'Contract renewals', recruitment: 'Recruitment and workforce planning', onboardingChecklists: 'Onboarding checklists', onboardingChecklistTemplates: 'Onboarding checklist templates', hireTemplates: 'Hire templates',
    locations: 'Locations', attendance: 'Attendance operations', shiftDefinitions: 'Shifts', shiftPolicy: 'Shift policy', attendanceRules: 'Attendance rules',
    overtimePolicies: 'Overtime policies', attendanceDevices: 'Attendance devices', shifts: 'Shift rosters',
    overtime: 'Overtime requests', timesheets: 'Project timesheets', leaveTypes: 'Leave types', leavePolicies: 'Leave policies',
    holidayCalendars: 'Holiday calendars', leave: 'Leave requests and balances', benefitPlans: 'Benefit plans',
    gradeBenefits: 'Benefits by grade', loanTypes: 'Loan types', loanPolicies: 'Loan policies', advancePolicy: 'Salary advance policy',
    bonusTypes: 'Bonus types', merit: 'Merit planning and benchmarks', benefits: 'Benefit enrolments', loans: 'Loans and advances',
    glMapping: 'Payroll accounting', fiscalYears: 'Fiscal years', payroll: 'Payroll and salary structures',
    payslipTemplates: 'Payslip templates', performanceScorecards: 'Review scorecards', performance: 'Performance cycles',
    succession: 'Succession and development',
    learning: 'Learning catalogue and rules', hrRequests: 'Request center', hrLetters: 'HR letters', letterTemplates: 'Letter templates',
    engagement: 'Engagement survey templates', engagementPrivacy: 'Survey privacy', notificationTemplates: 'Notifications',
    offboarding: 'Offboarding and settlement', gccSettings: 'Country rules', statutoryRules: 'Statutory rules',
    complianceProfiles: 'Compliance profiles', taxPolicies: 'Tax policies', saudiCompliance: 'Saudi compliance',
    compliance: 'Documents and renewals', saudiComplianceDashboard: 'Saudization and Qiwa status', gosiFiling: 'GOSI filing', adminAuditLogs: 'Audit log',
  };
  expect(Object.fromEntries(destinations().map(item => [item.id, item.label]))).toEqual(expectedLabels);
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
  for (const id of ['companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']) {
    expect(destinationAvailability(destination(id), companyContext), id).toBe('available');
  }
  for (const id of ['departments', 'grades', 'locations', 'establishment', 'statutoryRules']) {
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
