import { expect, test } from '@playwright/test';
import type { CompanyDto } from '../src/api/organization';
import { bankSettingsBaseline, bankSettingsLoadProblem, resolveBankSettingsScope } from '../src/lib/saudiBankSettingsScope';

/**
 * Setup > Bank and WPS: which company the form is for, and the three decisions the first review found missing.
 * Pure functions, so every state the panel can be in is pinned here without a browser.
 */

const company = (id: string, countryCode: string): CompanyDto => ({ id, legalNameEn: `Co ${id}`, tradeName: '', countryCode } as CompanyDto);

test('scope: no Saudi company in the list says so, and a list that has not arrived is not "no Saudi company"', () => {
  expect(resolveBankSettingsScope({ companyId: null, companies: [company('a', 'AE')], picked: '' })).toEqual({ kind: 'noSaudi' });
  expect(resolveBankSettingsScope({ companyId: null, companies: [], picked: '' })).toEqual({ kind: 'noSaudi' });
});

test('scope: a company Setup is scoped to is used even when the reader cannot list companies (payroll-only roles)', () => {
  const r = resolveBankSettingsScope({ companyId: 'z', companies: [], picked: '' });
  expect(r).toMatchObject({ kind: 'selected', showPicker: false });
  expect(r.kind === 'selected' && r.company.id).toBe('z');
  expect(resolveBankSettingsScope({ companyId: 'z', companies: undefined, picked: '' }).kind).toBe('selected');
});

test('scope: SA, SAU and KSA are all Saudi, in any case', () => {
  for (const code of ['SA', 'sa', 'SAU', 'sau', 'KSA']) {
    expect(resolveBankSettingsScope({ companyId: 'a', companies: [company('a', code)], picked: '' }).kind, code).toBe('selected');
  }
});

test('scope: the company chosen in Setup wins and hides the picker', () => {
  const r = resolveBankSettingsScope({ companyId: 'b', companies: [company('a', 'SA'), company('b', 'SA')], picked: 'a' });
  expect(r).toMatchObject({ kind: 'selected', showPicker: false });
  expect(r.kind === 'selected' && r.company.id).toBe('b');
});

test('scope: a single Saudi company is selected without asking', () => {
  const r = resolveBankSettingsScope({ companyId: null, companies: [company('a', 'SA'), company('x', 'AE')], picked: '' });
  expect(r).toMatchObject({ kind: 'selected', showPicker: false });
});

test('scope: several Saudi companies and none chosen asks, then follows the pick', () => {
  const companies = [company('a', 'SA'), company('b', 'SA')];
  expect(resolveBankSettingsScope({ companyId: null, companies, picked: '' })).toMatchObject({ kind: 'choose' });
  const r = resolveBankSettingsScope({ companyId: null, companies, picked: 'b' });
  expect(r).toMatchObject({ kind: 'selected', showPicker: true });
  expect(r.kind === 'selected' && r.company.id).toBe('b');
});

test('scope: Setup scoped to a non-Saudi company explains it instead of asking to choose from nothing', () => {
  const companies = [company('a', 'SA'), company('x', 'AE')];
  const r = resolveBankSettingsScope({ companyId: 'x', companies, picked: '' });
  expect(r.kind).toBe('scopeNotSaudi');
  expect(r.kind === 'scopeNotSaudi' && r.scoped.id).toBe('x');
});

test('baseline: a pre-selected only format is not an unsaved edit', () => {
  const empty = { formatId: '', molEstablishmentId: '', mainAccountNumber: '', organizationName: '', organizationAddress1: '', organizationAddress2: '', organizationAddress3: '', companyName: '', narrative: '', batchType: '', autoWpsUpload: false, nationalUnifiedNo: '', debitAccountIban: '' };
  const one = [{ id: 'anb-connect-csv-v1' }] as { id: string }[];
  expect(bankSettingsBaseline(empty, one).formatId).toBe('anb-connect-csv-v1');
  // Nothing is invented when there is a choice, when one is already saved, or when there is no format.
  expect(bankSettingsBaseline(empty, [...one, { id: 'other' }]).formatId).toBe('');
  expect(bankSettingsBaseline({ ...empty, formatId: 'saved' }, one).formatId).toBe('saved');
  expect(bankSettingsBaseline(empty, []).formatId).toBe('');
});

test('load problem: a refusal is explained, never offered a retry that cannot work', () => {
  expect(bankSettingsLoadProblem(403)).toEqual({ kind: 'forbidden', retry: false });
  expect(bankSettingsLoadProblem(404)).toEqual({ kind: 'notFound', retry: false });
  expect(bankSettingsLoadProblem(500)).toEqual({ kind: 'failed', retry: true });
  expect(bankSettingsLoadProblem(undefined)).toEqual({ kind: 'failed', retry: true });
});
