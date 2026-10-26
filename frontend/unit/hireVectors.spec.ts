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
interface Vectors { nationalId: { valid: string[]; invalid: string[] }; iqama: { valid: string[]; invalid: string[] }; iban: { valid: string[]; invalid: string[] }; phone: { valid: Array<{ input: string; country: string; e164: string }>; invalid: Array<{ input: string; country: string }> } }
const FALLBACK: Vectors = {
  nationalId: { valid: ['1000000008', '1000000016'], invalid: ['1000000009', '1000000000', '100000008', '2000000006', '3000000000'] },
  iqama: { valid: ['2000000006'], invalid: ['2000000007', '1000000008', '200000006', '20000000067', 'abcdefghij'] },
  iban: { valid: ['SA0380000000608010167519'], invalid: ['SA0380000000608010167518', 'SA03800000006080101675', 'GB82WEST12345698765432SA', 'SA038000000060801016751900'] },
  phone: {
    valid: [{ input: '0512345678', country: 'SA', e164: '+966512345678' }, { input: '+966 51 234 5678', country: 'SA', e164: '+966512345678' }, { input: '٠٥١٢٣٤٥٦٧٨', country: 'SA', e164: '+966512345678' }],
    invalid: [{ input: '0412345678', country: 'SA' }, { input: '051234567', country: 'SA' }, { input: 'abc', country: 'SA' }],
  },
};
function load(): Vectors {
  if (!fs.existsSync(SHARED)) return FALLBACK;
  const shared = JSON.parse(fs.readFileSync(SHARED, 'utf8')) as Partial<Vectors>;
  return { ...FALLBACK, ...shared };
}
const v = load();

test.describe('shared hire vectors', () => {
  for (const id of v.nationalId.valid) test(`national id valid ${id}`, () => expect(validateIdNumber('nationalId', id)).toBe('ok'));
  for (const id of v.nationalId.invalid) test(`national id invalid ${id}`, () => expect(validateIdNumber('nationalId', id)).not.toBe('ok'));
  for (const id of v.iqama.valid) test(`iqama valid ${id}`, () => expect(validateIdNumber('iqama', id)).toBe('ok'));
  for (const id of v.iqama.invalid) test(`iqama invalid ${id}`, () => expect(validateIdNumber('iqama', id)).not.toBe('ok'));
  for (const i of v.iban.valid) test(`iban valid ${i}`, () => expect(validateIban(i)).toBe('ok'));
  for (const i of v.iban.invalid) test(`iban invalid ${i}`, () => expect(validateIban(i)).not.toBe('ok'));
  for (const p of v.phone.valid) test(`phone valid ${p.input}`, () => expect(parseMobile(p.input, p.country)?.e164).toBe(p.e164));
  for (const p of v.phone.invalid) test(`phone invalid ${p.input}`, () => expect(parseMobile(p.input, p.country)).toBeNull());

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
