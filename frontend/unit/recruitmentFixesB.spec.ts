import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { recruitmentFixesB } from '../src/i18n/recruitmentFixesB';
import {
  BLACKLIST_REASON_MAX, BLACKLIST_REASON_MIN, canBlacklistCandidate, isBlacklisted, refusalReason, stageMoveBlock,
} from '../src/lib/recruitmentBlacklist';
import { timelineNote } from '../src/lib/applicationNote';

const has = (...roles: string[]) => (role: string) => roles.includes(role);
const identity = (key: string) => key;

test.describe('who may blacklist a candidate (the API gate: Admin and HR Manager, never HR Officer)', () => {
  test('Admin and HR Manager may', () => {
    expect(canBlacklistCandidate(has('Admin'))).toBe(true);
    expect(canBlacklistCandidate(has('HR Manager'))).toBe(true);
  });
  test('HR Officer, a manager, an employee and nobody may not', () => {
    expect(canBlacklistCandidate(has('HR Officer'))).toBe(false);
    expect(canBlacklistCandidate(has('Manager'))).toBe(false);
    expect(canBlacklistCandidate(has('Employee'))).toBe(false);
    expect(canBlacklistCandidate(has())).toBe(false);
  });
  test('the reason limits are the API limits', () => {
    expect(BLACKLIST_REASON_MIN).toBe(10);
    expect(BLACKLIST_REASON_MAX).toBe(1000);
  });
});

test.describe('blacklisted standing', () => {
  test('only the exact status Blacklisted counts', () => {
    expect(isBlacklisted({ status: 'Blacklisted' })).toBe(true);
    expect(isBlacklisted({ status: 'Active' })).toBe(false);
    expect(isBlacklisted({ status: 'blacklisted' })).toBe(false);
    expect(isBlacklisted(null)).toBe(false);
    expect(isBlacklisted(undefined)).toBe(false);
  });
  test('a blacklisted candidate has a stage move blocked, with the reason; an active one has none', () => {
    expect(stageMoveBlock({ status: 'Blacklisted' })).toBe('Blacklisted candidates cannot be moved to another stage.');
    expect(stageMoveBlock({ status: 'Active' })).toBeNull();
    expect(stageMoveBlock(null)).toBeNull();
  });
});

test.describe('the reason shown for a refused add', () => {
  const refusal = (data: unknown) => ({ isAxiosError: true, response: { status: 409, data } });
  test('shows the API sentence through the translator', () => {
    const t = (key: string) => (key === 'This candidate has already applied for this opening.' ? 'AR-DUP' : key);
    expect(refusalReason(refusal({ message: 'This candidate has already applied for this opening.' }), 'fallback', t, 'ar')).toBe('AR-DUP');
  });
  test('prefers the API Arabic sentence in Arabic and the English one otherwise', () => {
    const body = { message: 'English sentence.', messageAr: 'جملة عربية.' };
    expect(refusalReason(refusal(body), 'fallback', identity, 'ar')).toBe('جملة عربية.');
    expect(refusalReason(refusal(body), 'fallback', identity, 'en')).toBe('English sentence.');
  });
  test('falls back to the caller sentence when the body has none', () => {
    expect(refusalReason(refusal({}), 'Could not add.', identity, 'en')).toBe('Could not add.');
  });
});

test.describe('recruitmentFixesB strings', () => {
  const placeholders = (s: string) => (s.match(/\{[A-Za-z]+\}/g) ?? []).sort().join(',');
  test('every key has an Arabic text with the same placeholders, and no Arabic key is without English', () => {
    const { en, ar } = recruitmentFixesB;
    expect(Object.keys(en).length).toBeGreaterThan(10);
    expect(Object.keys(ar).sort()).toEqual(Object.keys(en).sort());
    for (const key of Object.keys(en)) {
      expect(ar[key].trim(), key).not.toBe('');
      expect(ar[key], key).not.toBe(en[key]);
      expect(placeholders(ar[key]), key).toBe(placeholders(en[key]));
    }
  });
  test('the stage names are translated (data keys stay English, the label is Arabic)', () => {
    for (const stage of ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired']) {
      expect(LOCALE_DICTS.ar[`enum.RecruitmentStage.${stage}`], stage).toBeTruthy();
    }
  });
  test('the offer workspace reason "The application is no longer active." has an Arabic text', () => {
    expect(LOCALE_DICTS.ar['The application is no longer active.']).toBeTruthy();
    expect(LOCALE_DICTS.ar['The application is no longer active.']).not.toBe('The application is no longer active.');
  });
  test('the duplicate messages the API sends in English have Arabic texts', () => {
    for (const key of ['This candidate has already applied for this opening.', 'A candidate with this email already exists.']) {
      expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
      expect(LOCALE_DICTS.ar[key], key).not.toBe(key);
    }
  });
});

test.describe('timeline notes written by the server', () => {
  const ar = (key: string) => LOCALE_DICTS.ar[key] ?? key;
  test('the known default note is translated at display time', () => {
    expect(timelineNote('Application created.', ar)).toBe(LOCALE_DICTS.ar['Application created.']);
    expect(LOCALE_DICTS.ar['Application created.']).toBeTruthy();
    expect(LOCALE_DICTS.ar['Application created.']).not.toBe('Application created.');
  });
  test('a note a person wrote (or an unknown server sentence) passes through untouched', () => {
    expect(timelineNote('Called twice, no answer.', ar)).toBe('Called twice, no answer.');
    expect(timelineNote('', ar)).toBe('');
  });
  test('the scroll-control labels have Arabic', () => {
    for (const key of ['Show earlier stages', 'Show later stages']) expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
  });
});
