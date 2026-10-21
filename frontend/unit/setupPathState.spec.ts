import { expect, test } from '@playwright/test';
import { resolveSetupPath, serializeSetupPath } from '../src/lib/setupPathState';

const context = {
  authorizedCompanyIds: new Set(['fixture-company', 'second-company']),
  selectedCompanyId: 'fixture-company',
};

test('ai_setup_defaults_to_guided', () => {
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&companyId=fixture-company'), context)).toEqual({
    path: 'guided', area: 'overview', setting: null, companyId: 'fixture-company', needsReplace: false,
  });
});

test('legacy_mode_starter_opens_the_guided_chooser_and_is_rewritten', () => {
  // Addendum A7: the old studio's address now lands on Guided Setup (the company setup chooser), canonicalised.
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&mode=starter&area=leave&setting=leavePolicies&companyId=fixture-company'), context)).toEqual({
    path: 'guided', area: 'leave', setting: 'leavePolicies', companyId: 'fixture-company', needsReplace: true,
  });
  const next = serializeSetupPath(resolveSetupPath(new URLSearchParams('tab=aiSetup&mode=starter&companyId=fixture-company'), context), new URLSearchParams());
  expect(next.get('path')).toBe('guided');
  expect(next.get('mode')).toBeNull();
  expect(next.get('companyId')).toBe('fixture-company');
});

test('path_policy_starter_opens_the_old_studio_under_configuration_center', () => {
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&path=policyStarter&companyId=fixture-company'), context)).toEqual({
    path: 'policyStarter', area: 'overview', setting: null, companyId: 'fixture-company', needsReplace: false,
  });
  expect(serializeSetupPath(resolveSetupPath(new URLSearchParams('tab=aiSetup&path=policyStarter'), context), new URLSearchParams()).get('path')).toBe('policyStarter');
});

test('path_configuration_opens_configuration', () => {
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&path=configuration&area=organization&setting=companies&companyId=fixture-company'), context)).toEqual({
    path: 'configuration', area: 'organization', setting: 'companies', companyId: 'fixture-company', needsReplace: false,
  });
});

test('area_or_setting_without_path_is_configuration_compatibility', () => {
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&area=leave&companyId=fixture-company'), context)).toEqual({
    path: 'configuration', area: 'leave', setting: null, companyId: 'fixture-company', needsReplace: true,
  });
  expect(resolveSetupPath(new URLSearchParams('tab=aiSetup&setting=fiscalYears&companyId=fixture-company'), context)).toEqual({
    path: 'configuration', area: 'payroll', setting: 'fiscalYears', companyId: 'fixture-company', needsReplace: true,
  });
});

test('setting_must_belong_to_area', () => {
  expect(resolveSetupPath(new URLSearchParams('path=configuration&area=leave&setting=companies&companyId=fixture-company'), context)).toEqual({
    path: 'configuration', area: 'leave', setting: null, companyId: 'fixture-company', needsReplace: true,
    notice: 'setting-mismatch',
  });
});

test('unknown_area_does_not_open_a_setting_from_another_area', () => {
  expect(resolveSetupPath(new URLSearchParams('path=configuration&area=forged-area&setting=leavePolicies&companyId=fixture-company'), context)).toEqual({
    path: 'configuration', area: 'overview', setting: null, companyId: 'fixture-company', needsReplace: true,
    notice: 'setting-mismatch',
  });
});

test('unauthorized_company_fails_closed', () => {
  expect(resolveSetupPath(new URLSearchParams('path=configuration&area=organization&companyId=unknown-company'), context)).toEqual({
    path: 'configuration', area: 'organization', setting: null, companyId: null, needsReplace: true,
    notice: 'unauthorized-company',
  });
});

test('serializer_preserves_department_level_and_setup_area', () => {
  const serialized = serializeSetupPath({
    path: 'configuration', area: 'payroll', setting: 'fiscalYears', companyId: 'fixture-company', needsReplace: false,
  }, new URLSearchParams('department=department-7&level=grade-2&setupArea=budget&source=retained&industry=private-draft-value'));

  expect(serialized.toString()).toBe('department=department-7&level=grade-2&setupArea=budget&source=retained&tab=aiSetup&path=configuration&area=payroll&setting=fiscalYears&companyId=fixture-company');
  expect(serialized.has('industry')).toBe(false);
});
