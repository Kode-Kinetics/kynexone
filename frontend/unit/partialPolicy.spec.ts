import { test, expect } from '@playwright/test';
import { partialPolicyShapes, missingPolicyFields } from '../src/lib/partialPolicy';

test('an extracted entitlement stays populated while omitted booleans and accrual stay missing', () => {
  const value = [{ name: 'Annual leave', leaveTypeCode: 'ANNUAL', annualEntitlementDays: 22 }];
  const missing = missingPolicyFields(partialPolicyShapes.leavePolicies, value, 'leavePolicies');
  expect(value[0].annualEntitlementDays).toBe(22);
  expect(missing).toContain('leavePolicies[0].accrualMethod');
  expect(missing).toContain('leavePolicies[0].encashmentAllowed');
  expect(missing).not.toContain('leavePolicies[0].annualEntitlementDays');
  expect('encashmentAllowed' in value[0]).toBe(false);
});

test('explicit no and zero are answers; an omitted population scope is not company-wide', () => {
  expect(missingPolicyFields({ kind: 'boolean' }, false)).toEqual([]);
  expect(missingPolicyFields({ kind: 'number' }, 0)).toEqual([]);
  expect(missingPolicyFields({ kind: 'text', nullable: true }, undefined, 'gradeCode')).toEqual(['gradeCode']);
  expect(missingPolicyFields({ kind: 'text', nullable: true }, null)).toEqual([]);
});

test('partial completion restricts choices to operational setup values', () => {
  const shape = partialPolicyShapes.overtimePolicy;
  if (shape.kind !== 'object') throw new Error('Expected object');
  expect(missingPolicyFields(shape.fields.hourlyRateBasis, 'BasicSalary')).toEqual([]);
  expect(missingPolicyFields(shape.fields.hourlyRateBasis, 'Basic', 'hourlyRateBasis')).toEqual(['hourlyRateBasis']);
});

test('integer contract fields reject fractions while entitlement days permit decimals', () => {
  const shape = partialPolicyShapes.leavePolicies;
  if (shape.kind !== 'array' || shape.item.kind !== 'object') throw new Error('Expected leave fields');
  expect(missingPolicyFields(shape.item.fields.noticeRequiredDays, 0.5, 'noticeRequiredDays')).toEqual(['noticeRequiredDays']);
  expect(missingPolicyFields(shape.item.fields.noticeRequiredDays, 1)).toEqual([]);
  expect(missingPolicyFields(shape.item.fields.annualEntitlementDays, 22.5)).toEqual([]);
});
