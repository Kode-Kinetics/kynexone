import test from 'node:test';
import assert from 'node:assert/strict';
import { mapEmployeeProfile } from '../src/api/profileMapper.ts';

test('maps EssEmployeeProfileDto identity expiry fields and phone', () => {
  const profile = mapEmployeeProfile({
    id: 7,
    employeeCode: 'EMP-007',
    fullName: 'Aisha Rahman',
    phone: '+966500000007',
    passportExpiryDate: '2027-01-02',
    visaExpiryDate: '2027-03-04',
    iqamaExpiryDate: '2027-05-06',
    emiratesIdExpiryDate: '2027-07-08',
  });
  assert.equal(profile.employeeNumber, 'EMP-007');
  assert.equal(profile.mobilePhone, '+966500000007');
  assert.equal(profile.passportExpiry, '2027-01-02');
  assert.equal(profile.visaExpiry, '2027-03-04');
  assert.equal(profile.iqamaExpiry, '2027-05-06');
  assert.equal(profile.emiratesIdExpiry, '2027-07-08');
});
