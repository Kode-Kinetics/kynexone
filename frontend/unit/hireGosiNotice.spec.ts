import { expect, test } from '@playwright/test';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { buildCreateRequest } from '../src/lib/hire/payload';
import { formFromEmployee } from '../src/lib/hire/draftHydrate';
import { validateHire } from '../src/lib/hire/hireValidation';
import { effectiveNoticeDays, gosiCohortFor } from '../src/lib/hire/gosiNotice';

/**
 * Owner decisions 2026-11-12. The GOSI first-registration date is stated at hire for a Saudi national only (it decides the contribution schedule:
 * before 3 July 2024 the existing one, on or after the new-entrant one). The notice period is pre-filled with the company default and editable.
 */
const ctx = { companyCountry: 'SA', currency: 'SAR' };
const base = { ...emptyHireForm(), englishName: 'Test Person' };
const vctx = { today: '2026-11-12', companyCountry: 'SA' };
const issue = (form: typeof base, field: string) => validateHire(form, vctx, 'draft').find(i => i.field === field);

test.describe('GOSI first registration date', () => {
  test('a Saudi national\'s date goes out; blank, expat and GCC nationals send nothing', () => {
    expect(buildCreateRequest({ ...base, nationalityCode: 'SA', gosiFirstRegisteredOn: '2025-01-01' }, ctx).gosiFirstRegisteredOn).toBe('2025-01-01');
    expect(buildCreateRequest({ ...base, nationalityCode: 'SA' }, ctx).gosiFirstRegisteredOn).toBeUndefined();
    expect(buildCreateRequest({ ...base, nationalityCode: 'PK', gosiFirstRegisteredOn: '2025-01-01' }, ctx).gosiFirstRegisteredOn).toBeUndefined();
    expect(buildCreateRequest({ ...base, nationalityCode: 'AE', gosiFirstRegisteredOn: '2025-01-01' }, ctx).gosiFirstRegisteredOn).toBeUndefined();
  });
  test('the cohort the date puts the person in is named from 3 July 2024', () => {
    expect(gosiCohortFor('2024-07-02')).toBe('existing');
    expect(gosiCohortFor('2024-07-03')).toBe('newEntrant');
    expect(gosiCohortFor('')).toBeNull();
    expect(gosiCohortFor('2024-02-31')).toBeNull();
  });
  test('a date that is not real, or is in the future, is refused; a past date is fine', () => {
    const saudi = { ...base, nationalityCode: 'SA' };
    expect(issue({ ...saudi, gosiFirstRegisteredOn: '2025-02-31' }, 'gosiFirstRegisteredOn')?.key).toBe('Enter a valid date.');
    expect(issue({ ...saudi, gosiFirstRegisteredOn: '2027-01-01' }, 'gosiFirstRegisteredOn')?.key).toBe('The first GOSI registration date cannot be in the future.');
    expect(issue({ ...saudi, gosiFirstRegisteredOn: '2025-02-01' }, 'gosiFirstRegisteredOn')).toBeUndefined();
    expect(issue({ ...saudi, gosiFirstRegisteredOn: '' }, 'gosiFirstRegisteredOn')).toBeUndefined();
  });
  test('a stored date resumes in the form', () => {
    const f = formFromEmployee({ id: 1, status: 'Draft', nationalityCode: 'SA', gosiFirstRegisteredOn: '2024-09-15' } as never, { companyCountry: 'SA' });
    expect(f.gosiFirstRegisteredOn).toBe('2024-09-15');
    expect(formFromEmployee({ id: 1, status: 'Draft' } as never, { companyCountry: 'SA' }).gosiFirstRegisteredOn).toBe('');
  });
});

test.describe('notice period', () => {
  test('blank shows and sends the company default; a typed value, 0 included, is the person\'s own', () => {
    expect(effectiveNoticeDays('', 60)).toBe(60);
    expect(effectiveNoticeDays('45', 60)).toBe(45);
    expect(effectiveNoticeDays('0', 60)).toBe(0);
    expect(effectiveNoticeDays('', null)).toBeUndefined();
    expect(effectiveNoticeDays('  ', undefined)).toBeUndefined();
  });
  test('the request carries the notice period as a whole number', () => {
    expect(buildCreateRequest({ ...base, noticePeriodDays: '45' }, ctx).noticePeriodDays).toBe(45);
    expect(buildCreateRequest(base, { ...ctx, defaultNoticeDays: 60 }).noticePeriodDays).toBe(60);
    expect(buildCreateRequest(base, ctx).noticePeriodDays).toBeUndefined();
    expect(buildCreateRequest({ ...base, noticePeriodDays: '0' }, { ...ctx, defaultNoticeDays: 60 }).noticePeriodDays).toBe(0);
  });
  test('only a whole number of days from 0 to 365 is accepted', () => {
    for (const bad of ['abc', '-1', '366', '1.5']) expect(issue({ ...base, noticePeriodDays: bad }, 'noticePeriodDays')?.key, bad).toBe('Enter the notice period as a whole number of days, from 0 to 365.');
    for (const ok of ['', '0', '60', '365']) expect(issue({ ...base, noticePeriodDays: ok }, 'noticePeriodDays'), ok).toBeUndefined();
  });
  test('a stored notice period resumes in the form', () => {
    expect(formFromEmployee({ id: 1, status: 'Draft', noticePeriodDays: 60 } as never, { companyCountry: 'SA' }).noticePeriodDays).toBe('60');
    expect(formFromEmployee({ id: 1, status: 'Draft', noticePeriodDays: 0 } as never, { companyCountry: 'SA' }).noticePeriodDays).toBe('0');
    expect(formFromEmployee({ id: 1, status: 'Draft' } as never, { companyCountry: 'SA' }).noticePeriodDays).toBe('');
  });
});
