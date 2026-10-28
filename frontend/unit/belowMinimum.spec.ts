import { test, expect } from '@playwright/test';
import {
  acknowledgementsFor, checksFromWarnings, coveringAck, floorCitation, floorLabel, floorMessage, floorStatus, isReasonRequired,
  openChecks, parseFloorWarnings, parseStoredAcks, readyToSave, reasonIsValid, type FloorCheck, type StoredFloorAck,
} from '../src/lib/belowMinimum';
import { translate } from '../src/i18n/translations';

// Statutory minimums are defaults, not walls (owner decision 2026-10-25). The wire contract is the hire path's:
// 409 `warnings_require_acknowledgement` and `acknowledgedWarnings: [{ code, reason }]`.

const check = (over: Partial<FloorCheck> = {}): FloorCheck => ({
  code: 'leave_annual_below_minimum', field: 'annualEntitlementDays', label: 'Annual leave days', labelAr: 'أيام الإجازة السنوية',
  message: 'Annual leave of 15 days is below the legal minimum of 21 days.', messageAr: 'الإجازة السنوية أقل من الحد الأدنى.',
  statutoryValue: 21, configuredValue: 15, citation: 'Labor Law Art. 109', citationAr: 'نظام العمل، المادة 109', below: true, ...over,
});

test('below the minimum is "below"; equal is the Saudi default; above is more generous; no number is neutral', () => {
  expect(floorStatus(check())).toBe('below');
  expect(floorStatus(check({ below: false, configuredValue: 21 }))).toBe('default');
  expect(floorStatus(check({ below: false, configuredValue: 30 }))).toBe('generous');
  expect(floorStatus(check({ below: false, configuredValue: null, statutoryValue: null }))).toBe('neutral');
  expect(floorStatus(check({ below: false, configuredValue: 0, statutoryValue: 0 }))).toBe('neutral');
});

test('a 409 warnings_require_acknowledgement is parsed; any other error is not', () => {
  const warnings = [{ code: 'x', field: 'f', message: 'm', requiresPermission: false }];
  expect(parseFloorWarnings({ response: { status: 409, data: { code: 'warnings_require_acknowledgement', warnings } } })).toEqual(warnings);
  expect(parseFloorWarnings({ response: { status: 409, data: { code: 'possible_duplicate' } } })).toBeNull();
  expect(parseFloorWarnings({ response: { status: 400, data: { code: 'warnings_require_acknowledgement', warnings } } })).toBeNull();
  expect(parseFloorWarnings(new Error('network'))).toBeNull();
  expect(isReasonRequired({ response: { status: 422, data: { code: 'override_reason_required' } } })).toBe(true);
  expect(isReasonRequired({ response: { status: 409, data: { code: 'override_reason_required' } } })).toBe(false);
});

test('the reason must be 10 to 500 characters once trimmed', () => {
  expect(reasonIsValid('too short')).toBe(false);
  expect(reasonIsValid('   ten chars!   ')).toBe(true);
  expect(reasonIsValid('x'.repeat(500))).toBe(true);
  expect(reasonIsValid('x'.repeat(501))).toBe(false);
});

test('acknowledgements echo the exact warning codes with the trimmed reason (the hire path\'s field shape)', () => {
  expect(acknowledgementsFor([{ code: 'a' }, { code: 'b' }], '  Board approved this  ')).toEqual([
    { code: 'a', reason: 'Board approved this' }, { code: 'b', reason: 'Board approved this' },
  ]);
});

test('an acknowledgement the server already holds answers the same value only; a changed value asks again', () => {
  const held: StoredFloorAck[] = [{ code: 'leave_annual_below_minimum', configuredValue: 15, statutoryValue: 21, reason: 'agreed', acknowledgedBy: 'Hana', acknowledgedAtUtc: '2026-10-27T10:00:00Z' }];
  expect(coveringAck(held, check())?.acknowledgedBy).toBe('Hana');
  expect(openChecks([check()], held)).toHaveLength(0);
  expect(openChecks([check({ configuredValue: 14 })], held)).toHaveLength(1);
  expect(openChecks([check({ below: false, configuredValue: 21 })], [])).toHaveLength(0);
});

test('saving is allowed with nothing open, or with the box ticked and a real reason', () => {
  expect(readyToSave([], false, '')).toBe(true);
  expect(readyToSave([check()], false, 'a perfectly good reason')).toBe(false);
  expect(readyToSave([check()], true, 'short')).toBe(false);
  expect(readyToSave([check()], true, 'a perfectly good reason')).toBe(true);
});

test('stored acknowledgements parse from the server JSON and ignore garbage', () => {
  expect(parseStoredAcks(null)).toEqual([]);
  expect(parseStoredAcks('not json')).toEqual([]);
  expect(parseStoredAcks('{"a":1}')).toEqual([]);
  expect(parseStoredAcks('[{"code":"a","configuredValue":1,"reason":"r","acknowledgedBy":"Z","acknowledgedAtUtc":"2026-01-01T00:00:00Z"}]')).toHaveLength(1);
});

test('English and Arabic come from the server, Arabic falling back to English when the server sent none', () => {
  const c = check();
  expect(floorMessage(c, 'en')).toContain('legal minimum of 21');
  expect(floorMessage(c, 'ar')).toBe('الإجازة السنوية أقل من الحد الأدنى.');
  expect(floorMessage(check({ messageAr: null }), 'ar')).toContain('legal minimum');
  expect(floorLabel(c, 'ar')).toBe('أيام الإجازة السنوية');
  expect(floorCitation(c, 'en')).toBe('Labor Law Art. 109');
  expect(floorCitation(c, 'ar')).toBe('نظام العمل، المادة 109');
  expect(checksFromWarnings([{ code: 'k', field: 'f', message: 'm' }])[0]).toMatchObject({ code: 'k', below: true });
});

test('every sentence the panel shows has an Arabic translation with its placeholders intact', () => {
  for (const key of [
    'Legal minimums', 'Saudi default: {value}', 'More generous than the law', 'Below the legal minimum of {min} ({citation})',
    'I understand employees may claim the difference', 'Why is this below the legal minimum? (10 to 500 characters)',
    'Tick the box and give a reason to save below the legal minimum.', 'Acknowledged by {name} on {date}: {reason}',
    'Rules below the legal minimum', 'No rules are below the legal minimum.', 'Not acknowledged',
  ]) {
    const ar = translate('ar', key, { value: '21', min: '21', citation: 'x', name: 'n', date: 'd', reason: 'r' });
    expect(ar, key).not.toBe(translate('en', key, { value: '21', min: '21', citation: 'x', name: 'n', date: 'd', reason: 'r' }));
    expect(ar).toMatch(/[؀-ۿ]/);
  }
});
