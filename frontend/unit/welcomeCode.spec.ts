import { expect, test } from '@playwright/test';
import {
  consumeWelcomeFragment, formatWelcomeCode, hasArabicLetters, isWelcomeCode, normalizeWelcomeCode, passwordChecks,
  toAsciiDigits, welcomeFromFragment,
} from '../src/lib/welcomeCode';
import { setWelcomeHandoff, takeBootFragment, takeWelcomeHandoff, WELCOME_BOOT, WELCOME_FRAGMENT_GLOBAL } from '../src/lib/welcomeHandoff';

// The welcome code is typed on whatever keyboard the employee's phone has. An Arabic keyboard gives
// Arabic-Indic digits, a Persian/Urdu one gives Extended Arabic-Indic digits, and people copy the
// slip's "4821 7730" with its space or type a dash. All of these are the same eight digits.

test('Arabic-Indic and Persian digits become ASCII digits', () => {
  expect(toAsciiDigits('٠١٢٣٤٥٦٧٨٩')).toBe('0123456789');
  expect(toAsciiDigits('۰۱۲۳۴۵۶۷۸۹')).toBe('0123456789');
  expect(toAsciiDigits('abc ١٢')).toBe('abc 12');
});

test('a code loses spaces and every kind of dash, and nothing else', () => {
  expect(normalizeWelcomeCode('٤٨٢١ ٧٧٣٠')).toBe('48217730');
  expect(normalizeWelcomeCode('۴۸۲۱-۷۷۳۰')).toBe('48217730');
  expect(normalizeWelcomeCode(' 4821–7730 ')).toBe('48217730'); // en dash
  expect(normalizeWelcomeCode('4821 7730')).toBe('48217730'); // no-break space
  expect(normalizeWelcomeCode('4821.7730')).toBe('4821.7730');
  expect(normalizeWelcomeCode(null)).toBe('');
});

test('only exactly eight digits is a welcome code', () => {
  expect(isWelcomeCode('٤٨٢١ ٧٧٣٠')).toBe(true);
  expect(isWelcomeCode('48217730')).toBe(true);
  expect(isWelcomeCode('4821773')).toBe(false);
  expect(isWelcomeCode('482177301')).toBe(false);
  expect(isWelcomeCode('4821773a')).toBe(false);
  expect(isWelcomeCode('')).toBe(false);
  expect(formatWelcomeCode('٤٨٢١٧٧٣٠')).toBe('4821 7730');
});

test('the QR fragment is read without corrupting the email, and the whole fragment is scrubbed', () => {
  expect(welcomeFromFragment('#e=noah.williams%40evostel.com&c=4821%207730&w=Evostel')).toEqual({
    email: 'noah.williams@evostel.com', code: '48217730', workspace: 'evostel',
  });
  // A raw '+' stays a '+' (URLSearchParams would turn it into a space).
  expect(welcomeFromFragment('#e=a+b@x.com&c=12345678').email).toBe('a+b@x.com');
  expect(welcomeFromFragment('#e=%E0%A4%A&c=12345678')).toEqual({ email: '', code: '12345678', workspace: '' });
  expect(welcomeFromFragment('')).toEqual({ email: '', code: '', workspace: '' });

  let replaced = '';
  const history = { state: { k: 1 }, replaceState: (_s: unknown, _u: string, url?: string | URL | null) => { replaced = String(url); } };
  const got = consumeWelcomeFragment({ hash: '#e=a%40x.com&c=12345678', pathname: '/welcome', search: '' }, history);
  expect(got.code).toBe('12345678');
  expect(replaced).toBe('/welcome');
  // What the inline boot script stashed wins over a (by then empty) location.hash.
  expect(consumeWelcomeFragment({ hash: '', pathname: '/welcome', search: '' }, history, '#e=b%40x.com&c=87654321').email).toBe('b@x.com');
});

test('the in-memory hand-off is read once', () => {
  setWelcomeHandoff({ email: 'a@x.com', code: '12345678', source: 'login' });
  expect(takeWelcomeHandoff()).toEqual({ email: 'a@x.com', code: '12345678', source: 'login' });
  expect(takeWelcomeHandoff()).toBeNull();
});

test('the boot script moves the fragment into memory and out of the address bar', () => {
  const calls: string[] = [];
  const fakeWindow: Record<string, unknown> = {
    location: { hash: '#e=a%40x.com&c=12345678', pathname: '/welcome', search: '' },
    history: { state: null, replaceState: (_s: unknown, _u: string, url: string) => calls.push(url) },
  };
  new Function('window', WELCOME_BOOT)(fakeWindow);
  expect(fakeWindow[WELCOME_FRAGMENT_GLOBAL]).toBe('#e=a%40x.com&c=12345678');
  expect(calls).toEqual(['/welcome']);
  expect(takeBootFragment()).toBe(''); // no DOM window under the unit runner
});

test('password ticks: length, and not the name or email', () => {
  expect(passwordChecks('short', 'noah.williams@evostel.com').longEnough).toBe(false);
  expect(passwordChecks('long enough!', 'noah.williams@evostel.com')).toEqual({ longEnough: true, notPersonal: true });
  expect(passwordChecks('Williams2026!', 'noah.williams@evostel.com').notPersonal).toBe(false);
  expect(passwordChecks('my noah password', 'noah.williams@evostel.com').notPersonal).toBe(false);
  expect(passwordChecks('', 'noah.williams@evostel.com').notPersonal).toBe(false);
  // The company's own minimum replaces the default of 10.
  expect(passwordChecks('twelve chars', 'a@x.com', 14).longEnough).toBe(false);
  expect(passwordChecks('fourteen chars', 'a@x.com', 14).longEnough).toBe(true);
});

test('Arabic letters are flagged, Arabic digits are not', () => {
  expect(hasArabicLetters('كلمةسر2026')).toBe(true);
  expect(hasArabicLetters('Secret٢٠٢٦pass')).toBe(false);
  expect(hasArabicLetters('plain ascii')).toBe(false);
});
