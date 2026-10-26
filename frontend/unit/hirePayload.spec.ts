import { expect, test } from '@playwright/test';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { buildCreateRequest, newIdempotencyKey } from '../src/lib/hire/payload';

const ctx = { companyCountry: 'SA', currency: 'SAR' };

test.describe('create payload', () => {
  test('a fresh form has an empty start date (A7)', () => {
    expect(emptyHireForm().startDate).toBe('');
    expect(buildCreateRequest(emptyHireForm(), ctx).joiningDate).toBeUndefined();
  });
  test('draft with the four fields carries nothing else', () => {
    const body = buildCreateRequest({ ...emptyHireForm(), englishName: ' Alex Morgan ', companyId: 'c1', startDate: '2026-11-01', designationId: 'd1' }, ctx);
    expect(body).toMatchObject({ englishName: 'Alex Morgan', companyId: 'c1', joiningDate: '2026-11-01', designationId: 'd1' });
    for (const k of ['arabicName', 'dateOfBirth', 'nationality', 'nationalityCode', 'mobileNumber', 'salaryBreakdown', 'payrollProfile', 'complianceRecords', 'acknowledgedWarnings'] as const) {
      expect((body as unknown as Record<string, unknown>)[k]).toBeUndefined();
    }
  });
  test('nationality is stored as an ISO-2 code, never as typed text (A1)', () => {
    const body = buildCreateRequest({ ...emptyHireForm(), englishName: 'Alex', nationalityCode: 'PK' }, ctx);
    expect(body.nationalityCode).toBe('PK');
  });
  test('phone goes out as E.164; ID goes into the compliance record with its expiry', () => {
    const body = buildCreateRequest({ ...emptyHireForm(), englishName: 'Alex', nationalityCode: 'PK', mobileCountry: 'SA', mobileNational: '0512345678', idType: 'iqama', idNumber: '2000000006', idExpiry: '2027-01-01' }, ctx);
    expect(body.mobileNumber).toBe('+966512345678');
    expect(body.complianceRecords).toEqual([expect.objectContaining({ countryCode: 'SA', fieldKey: 'iqama_number', fieldValue: '2000000006', expiryDate: '2027-01-01', isSensitive: true })]);
  });
  test('amounts are numbers and zero/blank are dropped; override reasons only when given', () => {
    const body = buildCreateRequest({ ...emptyHireForm(), englishName: 'Alex', basic: '12,000.50', housing: '', transport: '0' }, ctx);
    expect(body.salaryBreakdown).toMatchObject({ basicSalary: 12000.5, currency: 'SAR' });
    expect(body.salaryBreakdown?.housingAllowance).toBeUndefined();
    const withAck = buildCreateRequest({ ...emptyHireForm(), englishName: 'Alex' }, ctx, { acknowledgedWarnings: [{ code: 'x', reason: 'because reasons' }], duplicateOverrideReason: 'different person' });
    expect(withAck.acknowledgedWarnings).toEqual([{ code: 'x', reason: 'because reasons' }]);
    expect(withAck.duplicateOverrideReason).toBe('different person');
  });
  test('idempotency keys are v4 UUIDs and differ per call', () => {
    const a = newIdempotencyKey(); const b = newIdempotencyKey();
    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(a).not.toBe(b);
  });
});
