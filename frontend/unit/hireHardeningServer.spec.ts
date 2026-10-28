import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { serverFieldIssue } from '../src/lib/hire/problem';
import { serverFieldToForm } from '../src/lib/hire/serverFields';
import { issueText } from '../src/views/employees/hire/issueText';

const say = (field: Parameters<typeof serverFieldIssue>[0], code: string, locale: 'en' | 'ar' = 'en') => {
  const i = serverFieldIssue(field, code);
  return issueText({ key: i.key, params: i.params }, (k, p) => translate(locale, k, p), locale);
};

test.describe('CD26 server codes say the true thing', () => {
  test('value_too_long names the field and its own limit', () => {
    expect(say('workLocation', 'value_too_long')).toBe('Use 120 characters or fewer in Work location.');
    expect(say('molId', 'value_too_long')).toBe('Use 40 characters or fewer in MOL ID.');
    expect(say('arabicName', 'value_too_long')).toBe('Use 180 characters or fewer in Arabic full name.');
    expect(say('englishName', 'value_too_long')).toBe('Use 150 characters or fewer.');
    expect(say('workLocation', 'value_too_long', 'ar')).toContain('120');
    expect(say('workLocation', 'value_too_long', 'ar')).toContain('موقع العمل');
  });
  test('a field with no known limit still never claims "150"', () => {
    expect(say('maritalStatus', 'value_too_long')).not.toContain('150');
  });
  test('dob_future and dob_out_of_range are different sentences with no "between 15 and 80"', () => {
    expect(say('dob', 'dob_future')).toBe('Date of birth cannot be in the future.');
    expect(say('dob', 'dob_out_of_range')).toBe('Date of birth cannot be more than 100 years ago.');
  });
  test('IBAN checksum and format are different sentences', () => {
    expect(say('iban', 'iban_checksum_invalid')).toBe('This IBAN is not valid. Check the digits.');
    expect(say('iban', 'iban_invalid')).toBe('This IBAN is not valid. Check the digits and length.');
    expect(say('iban', 'iban_invalid', 'ar')).toMatch(/[؀-ۿ]/);
  });
  test('contract and probation codes', () => {
    expect(say('contractEndDate', 'contract_end_before_start')).toBe('The contract end date must be after the start date.');
    expect(say('contractEndDate', 'contract_end_without_fixed_term')).toBe('A contract end date needs a fixed-term contract type.');
  });
});

test.describe('server field names land on the new form fields', () => {
  test('probation end date, work email and contract end date', () => {
    expect(serverFieldToForm('probationEndDate')).toBe('probationEndDate');
    expect(serverFieldToForm('workEmail')).toBe('workEmail');
    expect(serverFieldToForm('contractEndDate')).toBe('contractEndDate');
  });
  test('the second compliance record is the passport', () => {
    expect(serverFieldToForm('complianceRecords[0].fieldValue')).toBe('idNumber');
    expect(serverFieldToForm('complianceRecords[1].fieldValue')).toBe('passportNumber');
    expect(serverFieldToForm('complianceRecords[1].expiryDate')).toBe('passportExpiry');
  });
});
