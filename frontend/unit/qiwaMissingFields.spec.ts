import { test, expect } from '@playwright/test';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { QIWA_WORK_LOCATION_NOTE, qiwaFieldLabel, qiwaMissingText, qiwaNeedsWorkLocationNote } from '../src/lib/qiwaMissingFields';

// Launch wave L4.2: the Qiwa data check lists what is missing in words, and says plainly why a work location ID is asked for
// and where it is entered (the product's branches do not hold Qiwa's identifier for a location).

const tEn = (key: string) => translate('en', key);
const tAr = (key: string) => translate('ar', key);

test('a missing field is named in words, never as its snake_case code', () => {
  expect(qiwaFieldLabel('work_location_id', tEn)).toBe('Work location ID');
  expect(qiwaFieldLabel('id_number', tEn)).toBe('ID number');
  expect(qiwaFieldLabel('contract_reference', tEn)).toBe('Contract reference');
  expect(qiwaMissingText(['work_location_id', 'occupation_code'], tEn)).toBe('Work location ID, Occupation code');
  // An unknown code is shown as it came rather than hidden.
  expect(qiwaFieldLabel('something_new', tEn)).toBe('something_new');
});

test('every Qiwa field has an Arabic label', () => {
  for (const code of ['saudi_or_non_saudi', 'id_type', 'id_number', 'nationality', 'occupation_code', 'establishment_id', 'work_location_id', 'contract_reference']) {
    const label = qiwaFieldLabel(code, tEn);
    expect(LOCALE_DICTS.ar[label], `Arabic for "${label}"`).toBeTruthy();
    expect(qiwaFieldLabel(code, tAr)).not.toMatch(/_/);
  }
  expect(qiwaMissingText(['work_location_id'], tAr)).toContain('موقع العمل');
});

test('the work-location note appears only when someone is missing it, and explains the gap', () => {
  expect(qiwaNeedsWorkLocationNote([{ missingFields: ['id_type'] }, { missingFields: [] }])).toBe(false);
  expect(qiwaNeedsWorkLocationNote([{ missingFields: ['id_type'] }, { missingFields: ['work_location_id'] }])).toBe(true);
  expect(QIWA_WORK_LOCATION_NOTE).toMatch(/branch/i);
  expect(QIWA_WORK_LOCATION_NOTE).toMatch(/Qiwa/);
  expect(QIWA_WORK_LOCATION_NOTE).toMatch(/employee/i);
  expect(LOCALE_DICTS.ar[QIWA_WORK_LOCATION_NOTE]).toBeTruthy();
});
