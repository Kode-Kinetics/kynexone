import { expect, test } from '@playwright/test';
import {
  cityFromCr, cleanEmailDomain, completeEmail, currencyLabel, digitsOnly, emailHost, hasArabicLetters, isPastMonth, isPublicEmailDomain,
  isSaudiVatNumber, maskMol, maskRegistration, monthLabel, suggestBranchCode, timeZoneOffset, toAsciiDigits,
} from '../src/lib/companySetupFields';

test.describe('digits and masks', () => {
  test('Arabic-Indic and Persian digits become ASCII', () => {
    expect(toAsciiDigits('١٠١٠١٢٣٤٥٦')).toBe('1010123456');
    expect(toAsciiDigits('۷۰۰')).toBe('700');
    expect(toAsciiDigits('CN-١٢')).toBe('CN-12');
  });
  test('a Saudi CR keeps 10 digits whatever was typed or pasted', () => {
    expect(maskRegistration('1010 123 456', true)).toBe('1010123456');
    expect(maskRegistration('١٠١٠١٢٣٤٥٦٧٨', true)).toBe('1010123456');
    expect(maskRegistration('CN-١٢٣', false)).toBe('CN-123');
    expect(digitsOnly('7 001-234 567', 10)).toBe('7001234567');
  });
  test('MOL: any separator becomes one hyphen; a trailing hyphen survives while typing', () => {
    expect(maskMol('7-1234567')).toBe('7-1234567');
    expect(maskMol('7.1234567')).toBe('7.1234567'.replace('.', '-'));
    expect(maskMol('7,')).toBe('7-');
    expect(maskMol('٧ ١٢٣٤٥٦٧')).toBe('7-1234567');
    expect(maskMol('7-12-34')).toBe('7-1234');
    expect(maskMol('-7')).toBe('7');
    expect(maskMol('1234567890123456789')).toHaveLength(15);
  });
  test('Saudi VAT is 15 digits starting and ending with 3', () => {
    expect(isSaudiVatNumber('300000000000003')).toBe(true);
    expect(isSaudiVatNumber('310000000000002')).toBe(false);
    expect(isSaudiVatNumber('30000000000003')).toBe(false);
  });
});

test.describe('names and email', () => {
  test('Arabic letters are detected', () => {
    expect(hasArabicLetters('شركة الرياض')).toBe(true);
    expect(hasArabicLetters('Riyadh Trading')).toBe(false);
    expect(hasArabicLetters('')).toBe(false);
  });
  test('pasted domains are cleaned to the bare domain', () => {
    expect(cleanEmailDomain('@acme.sa')).toBe('acme.sa');
    expect(cleanEmailDomain('https://www.Acme.SA/about?x=1')).toBe('acme.sa');
    expect(cleanEmailDomain('HR@Acme.sa ')).toBe('acme.sa');
    expect(cleanEmailDomain('mailto:hr@acme.sa')).toBe('acme.sa');
    expect(cleanEmailDomain('acme.sa.')).toBe('acme.sa');
    expect(cleanEmailDomain('')).toBe('');
  });
  test('public mail services are recognised', () => {
    expect(isPublicEmailDomain('Gmail.com')).toBe(true);
    expect(isPublicEmailDomain('acme.sa')).toBe(false);
    expect(emailHost('Sara@Gmail.com')).toBe('gmail.com');
    expect(emailHost('sara')).toBe('');
  });
  test('a bare name is completed with the company domain', () => {
    expect(completeEmail('sara', 'acme.sa')).toBe('sara@acme.sa');
    expect(completeEmail('sara@', 'acme.sa')).toBe('sara@acme.sa');
    expect(completeEmail('sara@other.sa', 'acme.sa')).toBe('sara@other.sa');
    expect(completeEmail('sara', '')).toBe('sara');
    expect(completeEmail('two words', 'acme.sa')).toBe('two words');
  });
});

test.describe('months, currencies, time zones', () => {
  test('month labels and the past', () => {
    expect(monthLabel('2026-11', 'en')).toBe('November 2026');
    expect(monthLabel('2026-13', 'en')).toBe('');
    expect(monthLabel('2026-11', 'ar')).toContain('2026');
    const today = new Date(2026, 9, 23);
    expect(isPastMonth('2026-09', today)).toBe(true);
    expect(isPastMonth('2026-10', today)).toBe(false);
    expect(isPastMonth('bad', today)).toBe(false);
  });
  test('currency names and zone offsets', () => {
    expect(currencyLabel('SAR', 'en')).toBe('SAR — Saudi Riyal');
    expect(currencyLabel('XYZ', 'en')).toBe('XYZ');
    expect(timeZoneOffset('Asia/Riyadh')).toBe('GMT+3');
    expect(timeZoneOffset('UTC')).toBe('');
  });
});

test.describe('branches and locations', () => {
  test('the CR issuing office suggests a city; unknown offices suggest nothing', () => {
    expect(cityFromCr('1010123456')).toBe('Riyadh');
    expect(cityFromCr('٤٠٣٠١٢٣٤٥٦')).toBe('Jeddah');
    expect(cityFromCr('9999123456')).toBeUndefined();
    expect(cityFromCr('1010')).toBeUndefined();
  });
  test('branch codes: HQ first, then a free BRn', () => {
    expect(suggestBranchCode([])).toBe('HQ');
    expect(suggestBranchCode(['HQ'])).toBe('BR2');
    expect(suggestBranchCode(['HQ', 'BR2'])).toBe('BR3');
    expect(suggestBranchCode(['HQ', 'BR3'])).toBe('BR4');
  });
});
