import test from 'node:test';
import assert from 'node:assert/strict';
import {
  companyCountry,
  dialCode,
  employmentStatusTone,
  expiringDocuments,
  identityRows,
} from '../src/features/profile/identityFields.ts';

const saudiRecord = {
  iqamaNumber: '2240060430', iqamaExpiry: '2027-05-15',
  passportNumber: 'A39336294', passportExpiry: '2030-06-30',
  emiratesId: '', emiratesIdExpiry: null, visaNumber: '',
};

test('a Saudi company shows Iqama/National ID first and no Emirates ID', () => {
  const rows = identityRows('SA', saudiRecord);
  assert.deepEqual(rows.map((r) => r.key), ['nationalId', 'passport']);
  assert.equal(rows[0].number, '2240060430');
});

test("the jurisdiction's own ID shows even when empty; another country's only when on the record", () => {
  assert.deepEqual(identityRows('AE', saudiRecord).map((r) => r.key), ['emiratesId', 'nationalId', 'passport']);
  assert.equal(identityRows('AE', saudiRecord)[0].number, null);
  assert.deepEqual(identityRows('QA', { qid: '123' }).map((r) => r.key), ['qid', 'passport']);
  assert.deepEqual(identityRows('BH', {}).map((r) => r.key), ['civilId', 'passport']);
});

test('unknown jurisdiction shows only documents on the record', () => {
  assert.deepEqual(identityRows(null, saudiRecord).map((r) => r.key), ['nationalId', 'passport']);
  assert.deepEqual(identityRows(null, { emiratesId: '784-1' }).map((r) => r.key), ['emiratesId', 'passport']);
});

test('country comes from the session companies only when they agree', () => {
  assert.equal(companyCountry([{ countryCode: 'SA' }, { countryCode: 'sa' }]), 'SA');
  assert.equal(companyCountry([{ countryCode: 'SA' }, { countryCode: 'AE' }]), null);
  assert.equal(companyCountry([{ countryCode: 'US' }]), null);
  assert.equal(companyCountry(undefined), null);
  assert.equal(dialCode('SA'), '+966');
  assert.equal(dialCode(null), null);
});

test('expiry alerts cover only shown documents within 60 days, soonest first', () => {
  const rows = identityRows('SA', { ...saudiRecord, iqamaExpiry: '2026-11-01', passportExpiry: '2026-09-01' });
  const alerts = expiringDocuments(rows, '2026-10-10');
  assert.deepEqual(alerts.map((a) => [a.key, a.daysLeft]), [['passport', -39], ['nationalId', 22]]);
});

test('employment status is read from the record; anything unknown is never "Active"', () => {
  assert.deepEqual(employmentStatusTone('Active'), { key: 'active', tone: 'success' });
  assert.equal(employmentStatusTone('Suspended').key, 'suspended');
  assert.equal(employmentStatusTone('Offboarded').key, 'offboarded');
  assert.equal(employmentStatusTone(undefined).key, 'unknown');
  assert.equal(employmentStatusTone('Whatever').key, 'unknown');
});
