import { expect, test } from '@playwright/test';
import { SETUP_AREAS, destinationAvailability, resolveSetupArea, type SetupAccessContext, type SetupDestination } from '../src/lib/setupAreas';

const context: SetupAccessContext = {
  hasPermission: () => true,
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
  for (const item of destinations().filter(item => item.presentation === 'embedded')) {
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
