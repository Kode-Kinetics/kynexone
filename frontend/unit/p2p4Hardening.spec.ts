import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { formatMessage } from '../src/i18n/message';
import { onboardingP2 } from '../src/i18n/onboardingP2';
import { onboardingP3 } from '../src/i18n/onboardingP3';
import { onboardingP4 } from '../src/i18n/onboardingP4';
import { isolateLtr, isolateName } from '../src/lib/bidi';
import { consentOrder } from '../src/lib/consentOrder';
import { hostingRegion, noticeVersionNumber } from '../src/lib/essPrivacy';
import { deviceStepUpBody } from '../src/api/esign';

const ar = LOCALE_DICTS.ar;
const fmt = (key: string, params: Record<string, number | string>) => formatMessage(ar[key] ?? key, params, 'ar', n => String(n));

test('CD64: no Arabic acknowledgement text calls the signature «مؤهلًا»; it says «معتمدًا»', () => {
  for (const dict of [onboardingP2.ar, onboardingP3.ar, onboardingP4.ar]) for (const [k, v] of Object.entries(dict)) expect(v, k).not.toMatch(/مؤهل/);
  expect(ar['Electronic acknowledgement (not a qualified signature)']).toBe('إقرار إلكتروني (وليس توقيعًا إلكترونيًا معتمدًا)');
  expect(ar['Electronic acknowledgement of contracts and policies. It is not a qualified electronic signature.']).toContain('توقيعًا إلكترونيًا معتمدًا');
});

test('72 §4: the sign screens use the vocabulary of acknowledgement', () => {
  expect(ar['Sign']).toBe('أُقرّ وأوقّع');
  expect(ar['Already signed']).toBe('تم الإقرار بها');
  expect(ar['You have signed everything HR asked for.']).toBe('أقررتَ بكل ما طلبته الموارد البشرية.');
});

test('72 §4: the template-applied chip no longer says the opposite of what happened', () => {
  const key = '{filled, plural, one {# field filled} other {# fields filled}}, {kept, plural, one {# left as you entered it.} other {# left as you entered them.}}';
  expect(fmt(key, { filled: 2, kept: 0 })).toContain('ولم يُغيَّر أي حقل أدخلته.');
  expect(fmt(key, { filled: 2, kept: 0 })).not.toContain('ولم يبق شيء');
  expect(fmt(key, { filled: 2, kept: 11 })).toContain('وبقي 11 حقلًا كما أدخلته.');
  expect(fmt(key, { filled: 1, kept: 1 })).toContain('وبقي حقل واحد كما أدخلته.');
});

test('CD59: the bank-change copy says who approves (Finance), and not HR', () => {
  const key = 'Accepted. A Finance approver must now approve the new bank details. It cannot be you or the employee. They apply from the day after Finance approves.';
  expect(onboardingP2.en[key]).toBeTruthy();
  expect(ar[key]).toMatch(/المالية/);
  expect(onboardingP2.en['Accepted. Finance now has to approve the new bank details; they apply from the next day.']).toBeUndefined();
});

test('privacy notice: the version and the hosting region are read as words, never as keys', () => {
  expect(noticeVersionNumber('ess-1')).toBe('1');
  expect(noticeVersionNumber('ess-12')).toBe('12');
  expect(noticeVersionNumber('whatever')).toBeNull();
  expect(hostingRegion('us-east')).toEqual({ labelKey: 'United States (east)', outsideKingdom: true });
  expect(hostingRegion('US-East ')).toEqual({ labelKey: 'United States (east)', outsideKingdom: true });
  expect(hostingRegion('me-sa')).toEqual({ labelKey: 'Saudi Arabia', outsideKingdom: false });
  expect(hostingRegion('mars-1')).toEqual({ labelKey: 'a hosting region chosen by your company', outsideKingdom: false });
  for (const k of ['United States (east)', 'United States (west)', 'Europe (west)', 'Europe (central)', 'Saudi Arabia', 'a hosting region chosen by your company']) {
    expect(ar[k], k).toBeTruthy();
  }
  expect(ar['United States (east)']).toBe('الولايات المتحدة (الشرق)');
  expect(ar['Your details are stored on servers in {region}, outside the Kingdom, and are visible only to you and authorised HR staff. HR reviews every change before it is applied to your record.']).toContain('{region}');
});

test('bidi: names and times are isolated so a neighbouring Arabic sentence cannot reorder them', () => {
  expect(isolateName('Sam Rider')).toBe('⁨Sam Rider⁩');
  expect(isolateLtr('27 Oct 2026, 09:30')).toBe('⁦' + '27 Oct 2026, 09:30' + '⁩');
  expect(isolateName('')).toBe('');
});

test('CD65: the Arabic consent text comes first in an Arabic session', () => {
  expect(consentOrder('ar')).toEqual(['ar', 'en']);
  expect(consentOrder('en')).toEqual(['en', 'ar']);
});

test('CD60: the device step-up asks for a token bound to this employee, this document and its hash', () => {
  const sha = 'a'.repeat(64);
  expect(deviceStepUpBody(901, 'doc-1', sha, 'pw', '')).toEqual({ employeeId: 901, documentId: 'doc-1', documentSha256: sha, password: 'pw', mfaCode: undefined });
  expect(deviceStepUpBody(901, 'doc-1', sha, 'pw', '123456').mfaCode).toBe('123456');
});
