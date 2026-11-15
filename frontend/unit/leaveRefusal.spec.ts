import { test, expect } from '@playwright/test';
import { leaveRefusalText } from '../src/lib/leaveRefusal';
import { translate, type MessageParams } from '../src/i18n/translations';

// The server refuses a leave request with { code, message, args }. `message` stays the English sentence for older clients;
// the web maps the code to a sentence in the viewer's language, so an Arabic user never reads English.

const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });
const refuse = (code: string, args?: Record<string, string>, message = 'The English sentence.') => http(400, { code, message, args });
const en = (key: string, params?: MessageParams) => translate('en', key, params);
const ar = (key: string, params?: MessageParams) => translate('ar', key, params);
const hasLatinLetters = (s: string) => /[A-Za-z]{3,}/.test(s);

test('a balance that was never set up names the leave type and the year, in English and in Arabic', () => {
  const err = refuse('leave_balance_not_set_up', { year: '2027', leaveType: 'Annual Leave' });
  expect(leaveRefusalText(err, en)).toBe('Your Annual Leave balance for 2027 has not been set up yet. Ask HR to set up your 2027 leave balance.');
  const arabic = leaveRefusalText(err, ar);
  expect(arabic).toContain('2027');
  expect(arabic).toContain('Annual Leave');
  expect(arabic).toMatch(/رصيد/);
  expect(hasLatinLetters(arabic.replace('Annual Leave', ''))).toBe(false);
});

test('day and month counts follow the plural forms of each language', () => {
  expect(leaveRefusalText(refuse('leave_notice_required', { days: '1' }), en)).toContain('1 day');
  expect(leaveRefusalText(refuse('leave_notice_required', { days: '3' }), en)).toContain('3 days');
  expect(leaveRefusalText(refuse('leave_notice_required', { days: '2' }), ar)).toContain('يومين');
  expect(leaveRefusalText(refuse('leave_notice_required', { days: '3' }), ar)).toContain('3 أيام');
  expect(leaveRefusalText(refuse('leave_min_service_months', { months: '6' }), ar)).toContain('6 أشهر');
});

test('every code in the catalogue reads as Arabic with no English left in it', () => {
  const cases: Array<[string, Record<string, string>?]> = [
    ['leave_overlap'], ['leave_notice_required', { days: '3' }], ['leave_insufficient_balance', { year: '2027' }],
    ['leave_balance_not_set_up', { year: '2027', leaveType: 'إجازة سنوية' }], ['leave_probation'], ['leave_min_service_months', { months: '6' }],
    ['leave_max_consecutive_days', { days: '10' }], ['leave_min_days', { days: '2' }], ['leave_max_days', { days: '5' }],
    ['leave_reason_required'], ['leave_attachment_required'], ['leave_attachment_not_found'], ['leave_half_day_not_allowed'],
    ['leave_hourly_not_allowed'], ['leave_blackout'], ['leave_type_unavailable'], ['leave_statutory_limit', { days: '15' }],
    ['leave_statutory_limit'], ['leave_not_eligible', { leaveType: 'إجازة سنوية' }], ['leave_invalid_dates'], ['leave_cancel_after_start'], ['leave_refused'],
  ];
  for (const [code, args] of cases) {
    const text = leaveRefusalText(refuse(code, args), ar);
    expect(text, code).not.toBe('The English sentence.');
    expect(hasLatinLetters(text), `${code}: ${text}`).toBe(false);
    expect(text, code).not.toMatch(/[{}]/);
  }
});

test('cancelling approved leave that has started is refused in the viewer\'s language', () => {
  const err = refuse('leave_cancel_after_start');
  expect(leaveRefusalText(err, en)).toBe('Approved leave can only be cancelled before it starts. Ask HR if it needs to change.');
  expect(leaveRefusalText(err, ar)).toMatch(/قبل/);
});

test('leave_refused is a neutral sentence, never the English text', () => {
  expect(leaveRefusalText(refuse('leave_refused', undefined, 'Some rule nobody has a sentence for.'), ar)).not.toContain('Some rule');
  expect(leaveRefusalText(refuse('leave_refused', undefined, 'Some rule nobody has a sentence for.'), en)).toBe('The request was refused. Check the details or ask HR.');
});

test('a code needing a value it did not get falls back to the neutral refusal, not a sentence with a hole', () => {
  expect(leaveRefusalText(refuse('leave_notice_required'), en)).toBe('The request was refused. Check the details or ask HR.');
  expect(leaveRefusalText(refuse('leave_statutory_limit'), en)).toContain('legal limit');
});

test('an unknown code, or an older server with only a message, keeps the server message', () => {
  expect(leaveRefusalText(refuse('leave_brand_new_rule', undefined, 'A brand new sentence.'), en)).toBe('A brand new sentence.');
  expect(leaveRefusalText(http(400, { message: 'Insufficient leave balance for 2027.' }), en)).toBe('Insufficient leave balance for 2027.');
});

test('failures that are not leave refusals are described as before', () => {
  expect(leaveRefusalText(http(403, { message: 'Nope.' }), en)).toBe('You do not have permission for this. Nope.');
  expect(leaveRefusalText({ isAxiosError: true }, en)).toBe('The server could not be reached. Check your connection, then retry.');
});

test('in Arabic the sentence names the leave type in Arabic when the server sent the Arabic name', () => {
  const balance = refuse('leave_balance_not_set_up', { year: '2027', leaveType: 'Annual Leave', leaveTypeAr: 'إجازة سنوية' });
  const arabic = leaveRefusalText(balance, ar, 'self', 'ar');
  expect(arabic).toContain('إجازة سنوية');
  expect(arabic).not.toContain('Annual Leave');
  expect(hasLatinLetters(arabic)).toBe(false);
  expect(leaveRefusalText(balance, ar, 'hr', 'ar')).toContain('إجازة سنوية');

  const eligibility = refuse('leave_not_eligible', { leaveType: 'Maternity Leave', leaveTypeAr: 'إجازة الأمومة' });
  expect(leaveRefusalText(eligibility, ar, 'self', 'ar')).toContain('إجازة الأمومة');
  expect(leaveRefusalText(eligibility, ar, 'self', 'ar')).not.toContain('Maternity');
});

test('in English the leave type stays the English name, and Arabic falls back to it when no Arabic name was sent', () => {
  const both = refuse('leave_balance_not_set_up', { year: '2027', leaveType: 'Annual Leave', leaveTypeAr: 'إجازة سنوية' });
  expect(leaveRefusalText(both, en, 'self', 'en')).toContain('Annual Leave');
  expect(leaveRefusalText(both, en, 'self', 'en')).not.toContain('إجازة سنوية');
  const englishOnly = refuse('leave_balance_not_set_up', { year: '2027', leaveType: 'Annual Leave' });
  expect(leaveRefusalText(englishOnly, ar, 'self', 'ar')).toContain('Annual Leave');
  const blankArabic = refuse('leave_not_eligible', { leaveType: 'Maternity Leave', leaveTypeAr: '' });
  expect(leaveRefusalText(blankArabic, ar, 'self', 'ar')).toContain('Maternity Leave');
});
