import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { draftIdentityError, draftIdentityFixKind, draftIdentityPayload, draftIdentityServerError } from '../src/lib/newHireReview';
import { saudiIqama, saudiNationalId } from '../e2e/bootstrap/saudiIds';
import { LOCALE_DICTS } from '../src/i18n/translations';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

/**
 * An accepted offer's draft for a Saudi company cannot activate without the government ID, and the review used to say
 * "add it to the draft" while offering no field. These pin when the field is offered, which one, how it is checked
 * before the round trip (the same rules the server applies), and what is sent.
 */
test.describe('New hires — the identity number on the draft', () => {
  test('the field offered follows the activation problem: Saudi national ID, otherwise Iqama', () => {
    expect(draftIdentityFixKind([{ key: 'IdNumber' }])).toBe('nationalId');
    expect(draftIdentityFixKind([{ key: 'department' }, { key: 'IqamaNumber' }])).toBe('iqama');
    expect(draftIdentityFixKind([{ key: 'department' }, { key: 'designation' }])).toBeNull();
    expect(draftIdentityFixKind([])).toBeNull();
  });

  test('a National ID is checked the way the server checks it', () => {
    expect(draftIdentityError('nationalId', saudiNationalId(3))).toBeNull();
    expect(draftIdentityError('nationalId', ` ${saudiNationalId(3)} `)).toBeNull();
    expect(draftIdentityError('nationalId', '123')).toBe('National ID must be 10 digits starting with 1.');
    expect(draftIdentityError('nationalId', saudiIqama(3))).toBe('National ID must be 10 digits starting with 1.');
    expect(draftIdentityError('nationalId', '1000000001')).toBe('This ID number is not valid. Check the digits.');
    expect(draftIdentityError('nationalId', '')).toBe('National ID must be 10 digits starting with 1.');
  });

  test('an Iqama is checked the way the server checks it', () => {
    expect(draftIdentityError('iqama', saudiIqama(9))).toBeNull();
    expect(draftIdentityError('iqama', saudiNationalId(9))).toBe('Iqama number must be 10 digits starting with 2.');
    expect(draftIdentityError('iqama', '2000000001')).toBe('This ID number is not valid. Check the digits.');
  });

  test('Arabic-Indic digits are accepted and saved as ASCII', () => {
    const id = saudiNationalId(5);
    const arabic = id.replace(/\d/g, (d) => '٠١٢٣٤٥٦٧٨٩'[Number(d)]);
    expect(draftIdentityError('nationalId', arabic)).toBeNull();
    expect(draftIdentityPayload('nationalId', arabic)).toEqual({ idNumber: id });
    expect(draftIdentityPayload('iqama', ` ${saudiIqama(5)}`)).toEqual({ iqamaNumber: saudiIqama(5) });
  });

  test('the server\'s verdict is shown in the same words, whatever else fails is left to the generic reason', () => {
    const refusal = (...codes: string[]) => ({ response: { status: 422, data: { error: 'invalid_identity', errors: { idNumber: codes } } } });
    expect(draftIdentityServerError('nationalId', refusal('id_format_invalid'))).toBe('National ID must be 10 digits starting with 1.');
    expect(draftIdentityServerError('iqama', refusal('id_format_invalid'))).toBe('Iqama number must be 10 digits starting with 2.');
    expect(draftIdentityServerError('nationalId', refusal('id_checksum_invalid'))).toBe('This ID number is not valid. Check the digits.');
    expect(draftIdentityServerError('nationalId', refusal('id_format_invalid', 'id_prefix_nationality_mismatch'))).toBe('This ID number does not match the nationality.');
    expect(draftIdentityServerError('nationalId', { response: { status: 409, data: { error: 'draft_closed' } } })).toBeNull();
    expect(draftIdentityServerError('nationalId', new Error('x'))).toBeNull();
  });

  test('the screen offers the field from the review, and the dialog says a different person activates', () => {
    const page = read('src/views/NewHiresPage.tsx');
    expect(page).toContain('draftIdentityFixKind(');
    expect(page).toContain('<DraftIdentityFix');
    const fix = read('src/components/DraftIdentityFix.tsx');
    expect(fix).toContain('draftIdentityError(');
    expect(fix).toContain('employeeDraftsApi.updateIdentity(');
    expect(fix).toContain('isMaker');
  });

  test('every new string has an Arabic translation that is not a copy of the English', () => {
    const keys = [
      'Government ID needed to activate',
      'Activation needs this person\'s {document}. Enter it here and the check runs again.',
      'Save and re-check',
      'Saving this makes you an editor of this hire, so a different HR approver has to activate it.',
      'You already worked on this hire, so a different HR approver has to activate it.',
      'Government ID saved. You edited this hire, so a different HR approver has to activate it.',
      'Only someone who can edit new hires can add this number.',
    ];
    for (const key of keys) {
      expect(LOCALE_DICTS.en[key], `en: ${key}`).toBe(key);
      const ar = LOCALE_DICTS.ar[key];
      expect(ar, `ar: ${key}`).toBeTruthy();
      expect(ar).not.toBe(key);
      expect(ar.match(/\{\w+\}/g) ?? []).toEqual(key.match(/\{\w+\}/g) ?? []);
    }
  });
});
