import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  ACCESS_STATE_COPY, BULK_PRINTABLE_STATES, CANONICAL_CONFLICT_CODES, CANONICAL_SKIP_CODES, CONFLICT_REASON_KEYS, DEFAULT_CONFLICT_KEY, GENERIC_SKIP_KEY, SKIP_REASON_KEYS,
  WORK_EMAIL_ERROR_KEYS, appAddress, dateLine, formatWelcomeCode, pairUp, parseWorkEmailRows, skipReasonKey, sortSlips,
  welcomeQrUrl, deriveThreeStates, workEmailErrorCode, workEmailLocalProblem, workEmailProblemKey, STOPPED_REASON_KEYS, BLOCKED_REASON_KEYS,
} from '../src/lib/employeeAccess';

// HR side of employee sign-in access (lib/employeeAccess.ts): the slip's formatting, the paste parser,
// and that every sentence the screens look up by code is in both dictionaries.

test('the code prints as two groups of four', () => {
  expect(formatWelcomeCode('48217730')).toBe('4821 7730');
  expect(formatWelcomeCode('4821 7730')).toBe('4821 7730');
  expect(formatWelcomeCode('123')).toBe('123');
});

test('the QR carries email, code and company in the hash, never a query string', () => {
  const url = welcomeQrUrl('https://app.example.test/', 'noah+x@evostel.com', '48217730', 'evostel');
  expect(url).toBe('https://app.example.test/welcome#e=noah%2Bx%40evostel.com&c=48217730&w=evostel');
  expect(url).not.toContain('?');
  expect(welcomeQrUrl('https://a.test', 'a@b.co', '11112222')).toBe('https://a.test/welcome#e=a%40b.co&c=11112222');
  expect(appAddress('https://kynexone.vercel.app/')).toBe('kynexone.vercel.app');
});

test('slips sort by site, then department, then name, blanks last, two per sheet', () => {
  const s = (employeeName: string, site: string, department: string) => ({ employeeName, site, department });
  const sorted = sortSlips([s('Zed', 'Riyadh HQ', 'Sales'), s('Amal', 'Jeddah', 'Sales'), s('Bader', 'Riyadh HQ', 'Finance'), s('Ali', '', 'Sales'), s('Adam', 'Riyadh HQ', 'Sales')]);
  expect(sorted.map((x) => x.employeeName)).toEqual(['Amal', 'Bader', 'Adam', 'Zed', 'Ali']);
  expect(pairUp([1, 2, 3])).toEqual([[1, 2], [3]]);
});

test('the date line matches the copy deck in both languages, in Riyadh time', () => {
  // 20:59:59Z is 23:59:59 in Riyadh: still the 14th there.
  expect(dateLine('2026-10-14T20:59:59Z', 'en')).toBe('Wednesday 14 October 2026 (3 Jumada I 1448 AH)');
  expect(dateLine('2026-10-14T20:59:59Z', 'ar')).toBe('الأربعاء، 14 أكتوبر 2026م (3 جمادى الأولى 1448هـ)');
  // An unknown zone falls back to the company default rather than throwing.
  expect(dateLine('2026-10-14T20:59:59Z', 'en', 'Not/AZone')).toContain('14 October 2026');
  expect(dateLine('nonsense', 'en')).toBe('');
});

test('IT\'s list parses from Excel, CSV, either column order, and skips a header', () => {
  const parsed = parseWorkEmailRows([
    'Employee no.\tWork email',
    'EMP-0042\tNoah.Williams@evostel.com',
    'EMP-0043, layla.haddad@evostel.com',
    '"omar.saleh@evostel.com";"EMP-0044"',
    'nothing useful here',
    '',
  ].join('\n'));
  expect(parsed.rows).toEqual([
    { employeeCode: 'EMP-0042', workEmail: 'noah.williams@evostel.com' },
    { employeeCode: 'EMP-0043', workEmail: 'layla.haddad@evostel.com' },
    { employeeCode: 'EMP-0044', workEmail: 'omar.saleh@evostel.com' },
  ]);
  expect(parsed.unreadable).toBe(1);
});

test('skip reasons and work-email refusals map to sentences', () => {
  expect(skipReasonKey('work_email_set_by_caller')).toBe("You set this person's work email, so another HR colleague must give access.");
  expect(skipReasonKey('seat_limit')).toBe("Your company's KynexOne plan is full.");
  expect(skipReasonKey('active')).toBe("Use Reset sign-in on the person's profile.");
  expect(skipReasonKey('something_new')).toBeNull();
  expect(workEmailErrorCode({ response: { status: 422, data: { error: 'work_email_plus_address' } } })).toBe('work_email_plus_address');
  expect(workEmailErrorCode({ response: { status: 400, data: { error: 'work_email_plus_address' } } })).toBeNull();
  expect(BULK_PRINTABLE_STATES.has('active')).toBe(false);
});

test('save-time duplicate conflicts have a plain message for single entry and edit', () => {
  const code = workEmailErrorCode({ response: { status: 409, data: { error: 'work_email_conflict', attempted: 'taken@external.test', suggestion: 'taken2@external.test' } } });
  expect(code).toBe('work_email_conflict');
  expect(workEmailProblemKey(code)).toBe('This email is already used by another employee.');
});

test('every sentence looked up by code exists in English and Arabic', () => {
  const keys = [
    ...Object.values(ACCESS_STATE_COPY).flatMap((c) => [c.label, c.action].filter((x): x is string => !!x)),
    ...Object.values(SKIP_REASON_KEYS), ...Object.values(WORK_EMAIL_ERROR_KEYS), ...Object.values(CONFLICT_REASON_KEYS),
    DEFAULT_CONFLICT_KEY, GENERIC_SKIP_KEY,
  ];
  for (const key of keys) {
    expect(LOCALE_DICTS.en[key], key).toBe(key);
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});

test('HR-facing words never include the jargon the HR panel ruled out', () => {
  const hrKeys = [
    ...Object.values(ACCESS_STATE_COPY).flatMap((c) => [c.label, c.action].filter((x): x is string => !!x)),
    ...Object.values(SKIP_REASON_KEYS), ...Object.values(CONFLICT_REASON_KEYS),
  ];
  for (const key of hrKeys) expect(key, key).not.toMatch(/\buser\b|\blink|invitation|access mode|staged/i);
});

test('every canonical skip and conflict code has its own sentence', () => {
  for (const code of CANONICAL_SKIP_CODES) {
    const key = skipReasonKey(code);
    expect(key, code).not.toBeNull();
    expect(key, code).not.toBe(GENERIC_SKIP_KEY);
  }
  for (const code of CANONICAL_CONFLICT_CODES) expect(CONFLICT_REASON_KEYS[code], code).toBeTruthy();
});

test('the work-email local part is checked with the server rule as HR types', () => {
  expect(workEmailLocalProblem('')).toBeNull();
  expect(workEmailLocalProblem('noah.williams-2_x@evostel.com')).toBeNull();
  expect(workEmailLocalProblem('noah')).toBe('work_email_invalid_format');
  expect(workEmailLocalProblem('noah@')).toBe('work_email_invalid_format');
  expect(workEmailLocalProblem('noah@@external.test')).toBe('work_email_invalid_format');
  expect(workEmailLocalProblem('noah@external.test')).toBeNull();
  expect(workEmailLocalProblem('noah+hr@evostel.com')).toBe('work_email_plus_address');
  expect(workEmailLocalProblem('nöah@evostel.com')).toBe('work_email_invalid_characters');
  expect(workEmailLocalProblem('نوح@evostel.com')).toBe('work_email_invalid_characters');
  expect(workEmailProblemKey('work_email_invalid_characters')).toBe('Work email can only use English letters, numbers, dots, dashes and underscores before the @.');
  expect(workEmailProblemKey('work_email_wrong_domain')).toBeNull();
  expect(workEmailErrorCode({ response: { status: 422, data: { error: 'work_email_invalid_characters' } } })).toBe('work_email_invalid_characters');
  expect(skipReasonKey('privileged_login')).toBe('This person has HR or admin permissions. A security admin must reset their sign-in.');
});

test('complete addresses from any domain are valid and reason codes have sentences', () => {
  expect(workEmailLocalProblem('noah@gmail.com')).toBeNull();
  expect(workEmailLocalProblem('noah@Evostel.com')).toBeNull();
  expect(workEmailProblemKey('work_email_invalid_format')).toBe('Enter the complete work email address issued by IT, for example name@company.com.');
  expect(skipReasonKey('awaiting_approval')).toBe('Waiting for approval. You can give access once {name} is approved.');
  expect(skipReasonKey('reset_requires_permission')).toBe("Ask an HR Manager to reset this person's sign-in.");
  for (const key of [...Object.values(STOPPED_REASON_KEYS), ...Object.values(BLOCKED_REASON_KEYS)]) {
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});

test('complete-address validation rejects malformed dots and domain labels without restricting the domain', () => {
  for (const address of ['.noah@external.test', 'noah.@external.test', 'noah..smith@external.test', 'noah@-external.test', 'noah@external-.test', 'noah@external..test', 'noah@external_test.com', 'noah@external/test.com', `${'n'.repeat(65)}@external.test`, `noah@${'x'.repeat(64)}.test`, `${'n'.repeat(64)}@${'x'.repeat(63)}.${'y'.repeat(63)}.${'z'.repeat(62)}`]) {
    expect(workEmailLocalProblem(address), address).toBe('work_email_invalid_format');
  }
  expect(workEmailLocalProblem('noah.smith@external-mail.test')).toBeNull();
  expect(workEmailLocalProblem(`${'n'.repeat(64)}@external.test`)).toBeNull();
});

// Three separate answers: employment, login, password. Signing in never activates employment.
const PAY = { blocking: [{ key: 'pay:compensationUnapproved' }] };
const person = (status = 'Draft', workEmail: string | null = 'a@co.test') => ({ status, workEmail });

test('login and password are set while the employee is not active: employment stays its own answer', () => {
  const states = deriveThreeStates(person('Draft'), { state: 'active' }, { blocking: [{ key: 'org:department' }] });
  expect(states).toEqual({ employee: 'not_active', login: 'login_enabled', password: 'password_set', needsWorkEmail: false, newCodeLive: false });
});

test('the pay blocker reads as awaiting pay approval; an active status wins', () => {
  expect(deriveThreeStates(person('Draft'), { state: 'not_started' }, PAY).employee).toBe('awaiting_pay_approval');
  expect(deriveThreeStates(person('Active'), { state: 'not_started' }, PAY).employee).toBe('active');
  expect(deriveThreeStates(person('Draft'), { state: 'not_started' }, null).employee).toBe('not_active');
});

test('a live code is an enabled login whose password is not set yet', () => {
  expect(deriveThreeStates(person(), { state: 'code_given' }, null)).toMatchObject({ login: 'login_enabled', password: 'code_given' });
});

test('no login, waiting, blocked: no login and no password', () => {
  for (const state of ['not_started', 'blocked'] as const) {
    expect(deriveThreeStates(person(), { state }, null)).toMatchObject({ login: 'no_login', password: 'not_set_up' });
  }
});

test('an older API without the facts does not guess the password after a stop', () => {
  expect(deriveThreeStates(person('Terminated'), { state: 'stopped' }, null)).toMatchObject({ login: 'access_stopped', password: 'unknown' });
});

const facts = (loginEnabled: boolean, passwordConfigured: boolean) => ({ loginEnabled, passwordConfigured });

test('with the independent facts, a reset keeps the password set while a new code is live', () => {
  expect(deriveThreeStates(person(), { state: 'code_given', ...facts(true, true) }, null))
    .toMatchObject({ login: 'login_enabled', password: 'password_set', newCodeLive: true });
});

test('with the independent facts, a first code is a staged login with no password', () => {
  expect(deriveThreeStates(person(), { state: 'code_given', ...facts(false, false) }, null))
    .toMatchObject({ login: 'no_login', password: 'code_given', newCodeLive: true });
});

test('with the independent facts, an expired code falls back to the true password state', () => {
  expect(deriveThreeStates(person(), { state: 'not_started', ...facts(true, true) }, null))
    .toMatchObject({ login: 'login_enabled', password: 'password_set', newCodeLive: false });
  expect(deriveThreeStates(person(), { state: 'not_started', ...facts(false, false) }, null))
    .toMatchObject({ login: 'no_login', password: 'not_set_up' });
});

test('with the independent facts, stopped access never says the password is unknown', () => {
  expect(deriveThreeStates(person('Terminated'), { state: 'stopped', ...facts(false, true) }, null))
    .toMatchObject({ login: 'access_stopped', password: 'password_set' });
  expect(deriveThreeStates(person('Terminated'), { state: 'stopped', ...facts(false, false) }, null))
    .toMatchObject({ login: 'access_stopped', password: 'not_set_up' });
});

test('with the independent facts, blocked is no login even if a password flag were set elsewhere', () => {
  expect(deriveThreeStates(person(), { state: 'blocked', ...facts(false, false) }, null))
    .toMatchObject({ login: 'no_login', password: 'not_set_up' });
});

test('a response with only one of the two facts is treated as an older API', () => {
  expect(deriveThreeStates(person('Terminated'), { state: 'stopped', loginEnabled: true }, null))
    .toMatchObject({ login: 'access_stopped', password: 'unknown' });
});

test('no work email: rows B and C need it first', () => {
  expect(deriveThreeStates(person('Draft', ''), { state: 'waiting_for_work_email', workEmail: '' }, null).needsWorkEmail).toBe(true);
  expect(deriveThreeStates(person('Draft', null), null, null).needsWorkEmail).toBe(true);
  expect(deriveThreeStates(person('Draft', 'a@co.test'), { state: 'not_started' }, null).needsWorkEmail).toBe(false);
});

test('an unavailable access status leaves login and password unanswered, never invented', () => {
  expect(deriveThreeStates(person(), null, PAY)).toMatchObject({ employee: 'awaiting_pay_approval', login: null, password: null });
});

test('the three-state copy exists in English and Arabic', async () => {
  const { accessStates } = await import('../src/i18n/accessStates');
  for (const key of Object.keys(accessStates.ar)) {
    expect(LOCALE_DICTS.en[key], key).toBe(key);
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
  for (const key of ['Active', 'Access stopped', 'Give access', 'Give new code', 'Reset sign-in', 'Activate', 'Retry', 'Loading readiness…']) {
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});

test('a suspended, offboarded, terminated or inactive person is never "not active yet"', () => {
  const expected: Record<string, string> = {
    Suspended: 'suspended', Offboarded: 'offboarded', Terminated: 'ended', Exited: 'ended', Archived: 'ended', Inactive: 'inactive',
  };
  for (const [status, row] of Object.entries(expected)) {
    expect(deriveThreeStates(person(status), { state: 'not_started' }, null).employee, status).toBe(row);
    // A leftover pay blocker does not turn a person who left into a hire awaiting pay approval.
    expect(deriveThreeStates(person(status), { state: 'not_started' }, PAY).employee, status).toBe(row);
  }
  expect(deriveThreeStates(person('suspended'), null, null).employee).toBe('suspended');
  expect(deriveThreeStates(person('Draft'), null, null).employee).toBe('not_active');
  expect(deriveThreeStates(person('Invited'), null, PAY).employee).toBe('awaiting_pay_approval');
});

test('the labels for people on hold or gone read in Arabic', () => {
  for (const key of ['Employment suspended', 'Offboarding in progress', 'Employment ended', 'Employee inactive', 'Change the employment status from the employee record.']) {
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});
