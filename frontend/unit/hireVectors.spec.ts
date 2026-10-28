import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { normalizeDigits, validateIdNumber } from '../src/lib/hire/saudiId';
import { validateIban } from '../src/lib/hire/iban';
import { parseMobile } from '../src/lib/hire/phone';

/**
 * The shared ID / IBAN / phone vectors. WP-A commits backend-dotnet/Zayra.Api.Tests/Fixtures/hire-vectors.json;
 * until it lands the vectors from the KSA rulebook (10-KSA section 1) are used. Both sides must agree on them.
 */
const SHARED = path.resolve(__dirname, '../../backend-dotnet/Zayra.Api.Tests/Fixtures/hire-vectors.json');
interface Shared {
  saudiNationalId: { valid: string[]; invalidChecksum: string[]; invalidFormat: string[] };
  iqama: { valid: string[]; invalidChecksum: string[]; invalidFormat: string[] };
  leadingDigitMismatch: { notIdNorIqama: string[] };
  borderNumber: { valid: string[]; advisoryChecksumFailure: string[]; invalidFormat: string[] };
  digitNormalisation: Array<{ input: string; expected: string }>;
  saIban: { valid: string[]; invalidChecksum: string[]; invalidLength: string[]; invalidStructure: string[] };
  phoneE164: { cases: Array<{ input: string; normalised: string | null }> };
}
const FALLBACK: Shared = {
  saudiNationalId: { valid: ['1000000008', '1000000016'], invalidChecksum: ['1000000009', '1000000000'], invalidFormat: ['100000008'] },
  iqama: { valid: ['2000000006'], invalidChecksum: ['2000000007'], invalidFormat: ['200000006'] },
  leadingDigitMismatch: { notIdNorIqama: ['3000000000'] },
  borderNumber: { valid: [], advisoryChecksumFailure: [], invalidFormat: [] },
  digitNormalisation: [{ input: '١٠٠٠٠٠٠٠٠٨', expected: '1000000008' }],
  saIban: { valid: ['SA0380000000608010167519'], invalidChecksum: ['SA0380000000608010167518'], invalidLength: ['SA03800000006080101675'], invalidStructure: [] },
  phoneE164: { cases: [{ input: '0512345678', normalised: '+966512345678' }, { input: '0412345678', normalised: null }] },
};
const v: Shared = fs.existsSync(SHARED) ? { ...FALLBACK, ...JSON.parse(fs.readFileSync(SHARED, 'utf8')) } : FALLBACK;

test.describe('shared hire vectors', () => {
  for (const id of v.saudiNationalId.valid) test(`national id valid ${id}`, () => expect(validateIdNumber('nationalId', id)).toBe('ok'));
  for (const id of v.saudiNationalId.invalidChecksum) test(`national id checksum ${id}`, () => expect(validateIdNumber('nationalId', id)).toBe('checksum'));
  for (const id of v.saudiNationalId.invalidFormat) test(`national id format ${id}`, () => expect(validateIdNumber('nationalId', id)).toBe('format'));
  for (const id of v.iqama.valid) test(`iqama valid ${id}`, () => expect(validateIdNumber('iqama', id)).toBe('ok'));
  for (const id of v.iqama.invalidChecksum) test(`iqama checksum ${id}`, () => expect(validateIdNumber('iqama', id)).toBe('checksum'));
  for (const id of v.iqama.invalidFormat) test(`iqama format ${id}`, () => expect(validateIdNumber('iqama', id)).toBe('format'));
  for (const id of v.leadingDigitMismatch.notIdNorIqama) test(`neither id nor iqama ${id}`, () => { expect(validateIdNumber('nationalId', id)).toBe('format'); expect(validateIdNumber('iqama', id)).toBe('format'); });
  for (const id of v.borderNumber.valid) test(`border valid ${id}`, () => expect(validateIdNumber('border', id)).toBe('ok'));
  for (const id of v.borderNumber.advisoryChecksumFailure) test(`border checksum is advisory ${id}`, () => expect(validateIdNumber('border', id)).toBe('ok'));
  for (const id of v.borderNumber.invalidFormat) test(`border format ${id}`, () => expect(validateIdNumber('border', id)).toBe('format'));
  for (const n of v.digitNormalisation) test(`digits ${n.input}`, () => expect(normalizeDigits(n.input)).toBe(n.expected));
  for (const i of v.saIban.valid) test(`iban valid ${i}`, () => expect(validateIban(i)).toBe('ok'));
  for (const i of v.saIban.invalidChecksum) test(`iban checksum ${i}`, () => expect(validateIban(i)).toBe('checksum'));
  for (const i of v.saIban.invalidLength) test(`iban length ${i}`, () => expect(validateIban(i)).toBe('saudi_structure'));
  for (const i of v.saIban.invalidStructure) test(`iban structure ${i}`, () => expect(validateIban(i)).not.toBe('ok'));
  for (const p of v.phoneE164.cases) test(`phone ${p.input}`, () => expect(parseMobile(p.input, 'SA')?.e164 ?? null).toBe(p.normalised));

  test('failure kind distinguishes format from checksum', () => {
    expect(validateIdNumber('nationalId', '1000000009')).toBe('checksum');
    expect(validateIdNumber('nationalId', '2000000006')).toBe('format');
    expect(validateIdNumber('iqama', '2000000007')).toBe('checksum');
    expect(validateIdNumber('nationalId', '12345')).toBe('format');
  });
  test('Arabic-Indic digits normalise to ASCII before validating', () => {
    expect(normalizeDigits('١٠٠٠٠٠٠٠٠٨')).toBe('1000000008');
    expect(validateIdNumber('nationalId', '١٠٠٠٠٠٠٠٠٨')).toBe('ok');
  });
  test('Saudi IBAN structure is its own failure kind', () => {
    expect(validateIban('SA03800000006080101675')).toBe('saudi_structure');
    expect(validateIban('SA0380000000608010167518')).toBe('checksum');
  });
});
