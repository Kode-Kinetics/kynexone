import test from 'node:test';
import assert from 'node:assert/strict';
import {
  formatWelcomeCode,
  hasArabicLetters,
  isAmbiguousFailure,
  isWelcomeCode,
  normalizeWelcomeCode,
  passwordChecks,
  toAsciiDigits,
  welcomeErrorKey,
} from '../src/auth/welcomeCode.ts';
import { welcomeAr, welcomeEn } from '../src/config/welcomeStrings.ts';

test('Arabic-Indic and Persian digits are read as ASCII', () => {
  assert.equal(toAsciiDigits('٠١٢٣٤٥٦٧٨٩'), '0123456789');
  assert.equal(toAsciiDigits('۰۱۲۳۴۵۶۷۸۹'), '0123456789');
});

test('a welcome code ignores spaces and dashes, and must be exactly eight digits', () => {
  assert.equal(normalizeWelcomeCode('٤٨٢١ ٧٧٣٠'), '48217730');
  assert.equal(normalizeWelcomeCode('۴۸۲۱-۷۷۳۰'), '48217730');
  assert.equal(normalizeWelcomeCode(' 4821–7730 '), '48217730');
  assert.equal(isWelcomeCode('4821 7730'), true);
  assert.equal(isWelcomeCode('4821773'), false);
  assert.equal(isWelcomeCode('482177300'), false);
  assert.equal(isWelcomeCode('abcd1234'), false);
  assert.equal(formatWelcomeCode('٤٨٢١٧٧٣٠'), '4821 7730');
});

test('password ticks follow the company minimum and reject the name or email', () => {
  assert.deepEqual(passwordChecks('Desert-Falcon-2026', 'noah.williams@evostel.com'), { longEnough: true, notPersonal: true });
  assert.equal(passwordChecks('Williams-2026!', 'noah.williams@evostel.com').notPersonal, false);
  assert.equal(passwordChecks('short', 'a@x.com').longEnough, false);
  assert.equal(passwordChecks('twelve chars', 'a@x.com', 14).longEnough, false);
  assert.equal(hasArabicLetters('كلمةسر2026'), true);
  assert.equal(hasArabicLetters('Secret٢٠٢٦'), false);
});

test('every refusal maps to a sentence, never the server text', () => {
  const cases: [number | undefined, unknown, string][] = [
    [400, 'code_invalid', 'codeInvalid'], [400, 'code_expired', 'codeGone'], [400, 'code_used', 'codeGone'],
    [400, 'password_policy', 'passwordRules'], [400, 'workspace_required', 'companyIdNeeded'],
    [400, 'try_later', 'tooManyTries'], [429, undefined, 'tooManyTries'], [400, 'sign_out_first', 'signOutFirst'],
    [400, 'seat_limit', 'planFull'], [400, 'brand_new_code', 'generic'], [undefined, undefined, 'network'],
  ];
  for (const [status, code, key] of cases) {
    assert.equal(welcomeErrorKey(status, code), key, `${status} ${String(code)}`);
    assert.ok(key in welcomeEn.errors, key);
  }
  assert.equal(isAmbiguousFailure(undefined), true);
  assert.equal(isAmbiguousFailure(502), true);
  assert.equal(isAmbiguousFailure(400), false);
});

test('sign-in strings: Arabic has every English key, in Arabic, with the same placeholders', () => {
  const walk = (en: Record<string, unknown>, ar: Record<string, unknown>, at: string) => {
    assert.deepEqual(Object.keys(ar).sort(), Object.keys(en).sort(), at);
    for (const [key, value] of Object.entries(en)) {
      const other = ar[key];
      if (typeof value === 'object') { walk(value as Record<string, unknown>, other as Record<string, unknown>, `${at}.${key}`); continue; }
      const holes = (s: string) => (s.match(/\{\{\w+\}\}/g) ?? []).sort();
      assert.deepEqual(holes(String(other)), holes(String(value)), `${at}.${key}`);
      if (key !== 'emailPlaceholder') assert.match(String(other), /[؀-ۿ]/, `${at}.${key}`);
    }
  };
  walk(welcomeEn, welcomeAr, 'signin');
  // Copy-deck wording.
  assert.equal(welcomeAr.code, 'رمز التفعيل');
  assert.equal(welcomeAr.companyId, 'معرّف الشركة');
});
