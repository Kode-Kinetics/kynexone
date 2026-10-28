import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { buildCreateRequest, newIdempotencyKey } from '../src/lib/hire/payload';

const ctx = { companyCountry: 'SA', currency: 'SAR', emailDomain: 'example.test' };
const base = { ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1' };

test.describe('CD26 work email is sent', () => {
  test('local part plus the company domain', () => {
    expect(buildCreateRequest({ ...base, workEmail: ' alex.morgan ' }, ctx).workEmail).toBe('alex.morgan@example.test');
  });
  test('a typed full address at the company domain is kept; blank sends nothing', () => {
    expect(buildCreateRequest({ ...base, workEmail: 'alex@example.test' }, ctx).workEmail).toBe('alex@example.test');
    expect(buildCreateRequest(base, ctx).workEmail).toBeUndefined();
  });
  test('without a company domain a bare local part cannot be composed, so nothing is sent', () => {
    expect(buildCreateRequest({ ...base, workEmail: 'alex' }, { companyCountry: 'SA', currency: 'SAR' }).workEmail).toBeUndefined();
  });
});

test.describe('CD26/CD31 contract end and probation', () => {
  test('contract end date goes out only with a fixed-term contract type', () => {
    expect(buildCreateRequest({ ...base, contractType: 'Fixed term', contractEndDate: '2027-11-01' }, ctx).contractEndDate).toBe('2027-11-01');
    expect(buildCreateRequest({ ...base, contractType: 'FixedTerm', contractEndDate: '2027-11-01' }, ctx).contractEndDate).toBe('2027-11-01');
    expect(buildCreateRequest({ ...base, contractType: 'Permanent', contractEndDate: '2027-11-01' }, ctx).contractEndDate).toBeUndefined();
    expect(buildCreateRequest({ ...base, contractEndDate: '2027-11-01' }, ctx).contractEndDate).toBeUndefined();
  });
  test('probation end date is sent when filled', () => {
    expect(buildCreateRequest({ ...base, probationEndDate: '2027-01-30' }, ctx).probationEndDate).toBe('2027-01-30');
    expect(buildCreateRequest(base, ctx).probationEndDate).toBeUndefined();
  });
});

test.describe('CD30 statutory flags are never set from typing', () => {
  test('an IBAN or MOL ID leaves wpsEligible and eosbEligible to the server', () => {
    const body = buildCreateRequest({ ...base, iban: 'SA03 8000 0000 6080 1016 7519', molId: '1-123456' }, ctx);
    expect(body.payrollProfile).toBeDefined();
    expect(body.payrollProfile).not.toHaveProperty('wpsEligible');
    expect(body.payrollProfile).not.toHaveProperty('eosbEligible');
    expect(JSON.stringify(body)).not.toMatch(/wpsEligible|eosbEligible/);
  });
});

test.describe('CD27 Border Number and the second identity row', () => {
  test('Border Number is its own compliance field', () => {
    const body = buildCreateRequest({ ...base, nationalityCode: 'PK', idType: 'border', idNumber: '3000000004', idExpiry: '2026-12-31' }, ctx);
    expect(body.complianceRecords).toEqual([expect.objectContaining({ fieldKey: 'border_number', fieldValue: '3000000004', expiryDate: '2026-12-31' })]);
  });
  test('an expat with an Iqama also sends the passport number and expiry as a second record', () => {
    const body = buildCreateRequest({ ...base, nationalityCode: 'PK', idType: 'iqama', idNumber: '2000000006', idExpiry: '2027-01-01', passportNumber: 'ab1234567', passportExpiry: '2030-05-05' }, ctx);
    expect(body.complianceRecords).toHaveLength(2);
    expect(body.complianceRecords![0]).toMatchObject({ fieldKey: 'iqama_number', fieldValue: '2000000006', expiryDate: '2027-01-01' });
    expect(body.complianceRecords![1]).toMatchObject({ fieldKey: 'passport_number', fieldValue: 'AB1234567', expiryDate: '2030-05-05', isSensitive: true });
  });
  test('a passport-only hire is one record, and a blank passport adds none', () => {
    const one = buildCreateRequest({ ...base, nationalityCode: 'PK', idType: 'passport', idNumber: 'AB1234567', passportNumber: 'ZZ9999999' }, ctx);
    expect(one.complianceRecords).toHaveLength(1);
    const none = buildCreateRequest({ ...base, nationalityCode: 'PK', idType: 'iqama', idNumber: '2000000006' }, ctx);
    expect(none.complianceRecords).toHaveLength(1);
  });
  test('a passport without a primary ID still goes out', () => {
    const body = buildCreateRequest({ ...base, nationalityCode: 'PK', passportNumber: 'AB1234567' }, ctx);
    expect(body.complianceRecords).toEqual([expect.objectContaining({ fieldKey: 'passport_number', fieldValue: 'AB1234567' })]);
  });
});

test.describe('CD42 idempotency key', () => {
  test('payload.ts has no Math.random', () => {
    const src = fs.readFileSync(path.resolve(__dirname, '../src/lib/hire/payload.ts'), 'utf8');
    expect(src).not.toMatch(/Math\.random/);
  });
  test('the key comes from crypto.randomUUID and never from Math.random', () => {
    const real = Math.random;
    Math.random = () => { throw new Error('Math.random must not be used'); };
    try { expect(newIdempotencyKey()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/); } finally { Math.random = real; }
  });
  test('with no crypto.randomUUID it refuses rather than inventing a weak key', () => {
    const desc = Object.getOwnPropertyDescriptor(globalThis, 'crypto');
    Object.defineProperty(globalThis, 'crypto', { value: {}, configurable: true });
    try { expect(() => newIdempotencyKey()).toThrow(); } finally { if (desc) Object.defineProperty(globalThis, 'crypto', desc); }
  });
});
