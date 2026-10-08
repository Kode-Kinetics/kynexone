import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  ACCESS_STATE_COPY, BULK_PRINTABLE_STATES, CANONICAL_CONFLICT_CODES, CANONICAL_SKIP_CODES, CONFLICT_REASON_KEYS, DEFAULT_CONFLICT_KEY, GENERIC_SKIP_KEY, SKIP_REASON_KEYS,
  WORK_EMAIL_ERROR_KEYS, appAddress, dateLine, formatWelcomeCode, pairUp, parseWorkEmailRows, skipReasonKey, sortSlips,
  welcomeQrUrl, workEmailErrorCode, workEmailLocalProblem, workEmailProblemKey, workEmailDomainProblem, STOPPED_REASON_KEYS, BLOCKED_REASON_KEYS,
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
  expect(workEmailLocalProblem('noah')).toBeNull();
  expect(workEmailLocalProblem('noah+hr@evostel.com')).toBe('work_email_plus_address');
  expect(workEmailLocalProblem('nöah@evostel.com')).toBe('work_email_invalid_characters');
  expect(workEmailLocalProblem('نوح@evostel.com')).toBe('work_email_invalid_characters');
  expect(workEmailProblemKey('work_email_invalid_characters')).toBe('Work email can only use English letters, numbers, dots, dashes and underscores before the @.');
  expect(workEmailProblemKey('work_email_wrong_domain')).toBeNull();
  expect(workEmailErrorCode({ response: { status: 422, data: { error: 'work_email_invalid_characters' } } })).toBe('work_email_invalid_characters');
  expect(skipReasonKey('privileged_login')).toBe('This person has admin permissions. A security admin must reset their sign-in.');
});

test('a different domain is reported, the company one is fine, and reason codes have sentences', () => {
  expect(workEmailDomainProblem('noah@gmail.com', 'evostel.com')).toBe('evostel.com');
  expect(workEmailDomainProblem('noah@Evostel.com', 'evostel.com')).toBeNull();
  expect(workEmailDomainProblem('noah', 'evostel.com')).toBeNull();
  expect(workEmailDomainProblem('noah@gmail.com', '')).toBeNull();
  expect(skipReasonKey('awaiting_approval')).toBe('Waiting for approval. You can give access once {name} is approved.');
  expect(skipReasonKey('reset_requires_permission')).toBe("Ask an HR Manager to reset this person's sign-in.");
  for (const key of [...Object.values(STOPPED_REASON_KEYS), ...Object.values(BLOCKED_REASON_KEYS)]) {
    expect(LOCALE_DICTS.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});
