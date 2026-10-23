import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { setupNavigationModel, type SetupNavigationAccess } from '../src/lib/setupAreas';
import { buildSetupSearchIndex, matchSetupEntries, normalizeSearchText, type SetupSearchEntry } from '../src/components/setup/setupSearch';

// "Find a setting" (Setup's section column and the ⌘K palette's Settings group): one index built from the same model
// as Setup's navigation, and a matcher that ignores case and diacritics and works in Arabic and English.

const en = (key: string) => translate('en', key);
const ar = (key: string) => translate('ar', key);

const ADMIN_PERMISSIONS = ['organization.read', 'organization.write', 'payroll.read', 'payroll.rates.read', 'finance.gl.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'learning.read'];
function access(options: { permissions?: string[]; roles?: string[]; companyScoped?: boolean; policyIntake?: boolean } = {}) {
  const permissions = new Set(options.permissions ?? ADMIN_PERMISSIONS);
  const roles = new Set(options.roles ?? ['Admin']);
  const nav: SetupNavigationAccess = {
    hasPermission: permission => permissions.has(permission),
    hasRole: role => roles.has(role),
    isCompanyScopedGroup: options.companyScoped ?? false,
    policyIntake: options.policyIntake ?? false,
  };
  return {
    nav,
    flags: { ...nav, isFeatureEnabled: () => true, verdictForPath: () => ({ allowed: true }), flagStatus: 'ready' as const },
  };
}
function index(options?: Parameters<typeof access>[0]): SetupSearchEntry[] {
  const { nav, flags } = access(options);
  return buildSetupSearchIndex({ model: setupNavigationModel(nav), access: flags });
}
const labels = (entries: SetupSearchEntry[], query: string, t = en) => matchSetupEntries(entries, query, t).map(hit => hit.label);
const ids = (entries: SetupSearchEntry[], query: string, t = en) => matchSetupEntries(entries, query, t).map(hit => hit.entry.id);

test('normalisation ignores case, diacritics, Arabic letter variants and punctuation, and reads centre as center', () => {
  expect(normalizeSearchText('Café  Économie')).toBe('cafe economie');
  expect(normalizeSearchText('Drop-down lists')).toBe('drop down lists');
  expect(normalizeSearchText('Cost centres')).toBe('cost centers');
  // Harakat and tatweel are dropped; hamza forms of alef, alef maqsura and taa marbuta fold.
  expect(normalizeSearchText('الدَّرَجـات')).toBe('الدرجات');
  expect(normalizeSearchText('إدارة')).toBe(normalizeSearchText('اداره'));
  expect(normalizeSearchText('مستوى')).toBe(normalizeSearchText('مستوي'));
});

test('labels and descriptions match, best match first', () => {
  const entries = index();
  expect(labels(entries, 'grades')[0]).toBe('Grades');
  expect(labels(entries, 'GRADES')[0]).toBe('Grades');
  // A description match: "Work week, Ramadan hours, WPS/SIF…" is Country rules'.
  expect(ids(entries, 'end-of-service')).toContain('gccSettings');
  expect(matchSetupEntries(entries, 'grades', en)[0].group).toBe('Jobs & headcount');
  expect(matchSetupEntries(entries, '   ', en)).toEqual([]);
  expect(labels(entries, 'zzzz-nothing')).toEqual([]);
});

test('synonyms find the setting people mean', () => {
  const entries = index();
  for (const scheme of ['GOSI', 'saned', 'GPSSA', 'grsia']) {
    const found = ids(entries, scheme);
    expect(found, scheme).toContain('statutoryRules');
    expect(found, scheme).toContain('glMapping');
  }
  expect(ids(entries, 'cost center')).toContain('costCenters');
  expect(ids(entries, 'centre')).toContain('costCenters');
  expect(ids(entries, 'dropdown')[0]).toBe('masterData');
  expect(ids(entries, 'picklist')).toEqual(['masterData']);
  expect(ids(entries, 'SMTP')).toEqual(['emailConfig']);
  expect(ids(entries, 'numbers')).toContain('numberingRules');
  expect(ids(entries, 'sequence')).toContain('numberingRules');
  expect(ids(entries, 'work week')).toContain('gccSettings');
  expect(ids(entries, 'ramadan')).toContain('gccSettings');
  expect(ids(entries, 'WPS')).toContain('gccSettings');
});

test('All setup areas destinations and Payroll accounting tabs are found with their caption', () => {
  const entries = index();
  const leave = matchSetupEntries(entries, 'leave types', en)[0];
  expect(leave.label).toBe('Leave types');
  expect(leave.group).toBe('Leave and absence');
  expect(leave.entry.href).toBe('/setup?tab=aiSetup&path=configuration&area=leave&setting=leaveTypes');
  const rates = matchSetupEntries(entries, 'company rates', en)[0];
  expect(rates.label).toBe('Company Rates');
  expect(rates.group).toBe('Payroll accounting');
  // Each tab carries its own id through navigation: Payroll accounting opens on that tab.
  expect(rates.entry.navId).toBeUndefined();
  expect(rates.entry.href).toBe('/setup?tab=glMapping&glTab=companyRates');
  expect(matchSetupEntries(entries, 'statutory rates', en)[0].entry.href).toBe('/setup?tab=glMapping&glTab=statutory');
  expect(matchSetupEntries(entries, 'accounting periods', en)[0].entry.href).toBe('/setup?tab=glMapping&glTab=periods');
  // A page-supplied section address keeps its own parameters.
  const { nav, flags } = access();
  const scoped = buildSetupSearchIndex({ model: setupNavigationModel(nav), access: flags, sectionHref: id => `/setup?tab=${id}&companyId=co-1` });
  expect(matchSetupEntries(scoped, 'company rates', en)[0].entry.href).toBe('/setup?tab=glMapping&companyId=co-1&glTab=companyRates');
  // A setting that is also a section is listed once, as the section.
  expect(ids(entries, 'Grades').filter(id => id.includes('grades'))).toEqual(['grades']);
});

test('Arabic finds Arabic and English names, without diacritics', () => {
  const entries = index();
  expect(labels(entries, 'الدرجات', ar)[0]).toBe('الدرجات');
  expect(labels(entries, 'الدَّرَجَات', ar)[0]).toBe('الدرجات');
  // English typed into the Arabic interface still finds the section, shown in Arabic.
  expect(labels(entries, 'grades', ar)[0]).toBe('الدرجات');
  expect(matchSetupEntries(entries, 'grades', ar)[0].group).toBe('الوظائف والقوى العاملة');
  expect(ids(entries, 'القوائم المنسدلة', ar)[0]).toBe('masterData');
  expect(ids(entries, 'التأمينات الاجتماعية', ar)).toContain('statutoryRules');
});

test('a user never finds a section the navigation hides from them', () => {
  // A read-only viewer: only Companies, exactly like the navigation.
  const reader = index({ permissions: ['organization.read'], roles: ['Auditor'] });
  expect(reader.map(entry => entry.id)).toEqual(['companies']);
  expect(ids(reader, 'grades')).toEqual([]);
  expect(ids(reader, 'gosi')).toEqual([]);
  expect(ids(reader, 'leave')).toEqual([]);
  // A company-scoped group administrator: only company-owned settings, and no tenant-wide destinations.
  const scoped = index({ companyScoped: true });
  const scopedSections = scoped.filter(entry => entry.navId && !entry.id.startsWith('gl:')).map(entry => entry.id);
  expect(scopedSections).toEqual(['guidedSetup', 'allSetupAreas', 'policyStarter', 'companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']);
  expect(ids(scoped, 'grades')).toEqual([]);
  // Leave types are tenant-wide: not theirs. (The Leave workspace itself is, so its area is still found.)
  expect(ids(scoped, 'leave types')).toEqual(['area:leave']);
  // Policy library only while policy intake is on; Payroll accounting tabs only with their permission.
  expect(ids(index(), 'policy library')).toEqual([]);
  expect(ids(index({ policyIntake: true }), 'policy library')).toContain('policyLibrary');
  // Without a rates permission the Company Rates tab is not found (Payroll accounting still is, by its description).
  expect(ids(index({ permissions: ['organization.write'] }), 'company rates')).toEqual(['glMapping']);
  expect(ids(index(), 'company rates')).toEqual(['gl:companyRates', 'glMapping']);
  expect(ids(index({ permissions: ['organization.write'], roles: ['HR Manager'] }), 'erp api')).toEqual([]);
});

test('a destination whose feature or route is off is not offered', () => {
  const { nav, flags } = access();
  const entries = buildSetupSearchIndex({ model: setupNavigationModel(nav), access: { ...flags, verdictForPath: path => ({ allowed: path !== '/learning' }) } });
  expect(ids(entries, 'learning catalogue')).toEqual([]);
  expect(ids(index(), 'learning catalogue')).toContain('destination:learning:learning');
});
