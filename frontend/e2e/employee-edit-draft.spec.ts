import { expect, test } from '@playwright/test';
import type { EmployeeDetail } from '../src/api/employees';
import { LOCAL_FIELD_CATALOG, type EmployeeEditField, type ResolvedFieldCatalog } from '../src/api/employeeFieldCatalog';
import { buildEmployeeEditChanges, buildEmployeeEditSnapshot } from '../src/lib/employeeEditDraft';

const identityFields: EmployeeEditField[] = [
  { section: 'Compliance Documents', key: 'iqamaNumber', label: 'Iqama number' },
  { section: 'Compliance Documents', key: 'iqamaExpiryDate', label: 'Iqama expiry', type: 'date' },
  { section: 'Compliance Documents', key: 'passportExpiryDate', label: 'Passport expiry', type: 'date' },
];
const catalog: ResolvedFieldCatalog = {
  ...LOCAL_FIELD_CATALOG,
  countryCompliance: {
    ...LOCAL_FIELD_CATALOG.countryCompliance,
    SA: [
      { fieldKey: 'iqama_number', fieldLabel: 'Iqama', entityKey: 'iqamaNumber', expiryEntityKey: 'iqamaExpiryDate' },
      { fieldKey: 'passport_number', fieldLabel: 'Passport', entityKey: 'passportNumber', expiryEntityKey: 'passportExpiryDate' },
    ],
  },
};
const employee = (overrides: Partial<EmployeeDetail> = {}): EmployeeDetail => ({
  id: 42,
  countryCode: 'SA',
  iqamaNumber: 'TEST-RESIDENCE',
  complianceRecords: [
    { countryCode: 'AE', fieldKey: 'iqama_number', fieldLabel: 'Other country', expiryDate: '2031-01-01', isSensitive: true, isRequired: false },
    { countryCode: 'KSA', fieldKey: 'iqama_number', fieldLabel: 'Iqama', expiryDate: '2027-11-04', isSensitive: true, isRequired: false },
    { countryCode: 'SA', fieldKey: 'passport_number', fieldLabel: 'Passport', expiryDate: '2030-02-01', isSensitive: true, isRequired: false },
  ],
  ...overrides,
} as EmployeeDetail);

test('does not present stale compliance mirrors as current values when the API omits scalar expiry fields', () => {
  const snapshot = buildEmployeeEditSnapshot(employee(), identityFields, catalog, 'Saudi Arabia');
  expect(snapshot).toEqual({ iqamaNumber: 'TEST-RESIDENCE', iqamaExpiryDate: '', passportExpiryDate: '' });
  expect(buildEmployeeEditSnapshot(employee({ countryCode: '', complianceRecords: [] }), identityFields, catalog))
    .toEqual({ iqamaNumber: 'TEST-RESIDENCE', iqamaExpiryDate: '', passportExpiryDate: '' });
});

test('does not resurrect cleared scalar dates or overwrite scalar values from old compliance records', () => {
  const persisted = employee({ iqamaExpiryDate: '2028-05-09T00:00:00Z', passportExpiryDate: null as unknown as string });
  expect(buildEmployeeEditSnapshot(persisted, identityFields, catalog, 'SA')).toEqual({
    iqamaNumber: 'TEST-RESIDENCE', iqamaExpiryDate: '2028-05-09', passportExpiryDate: '',
  });
});

test('hydrates only supported payroll profile keys and keeps a masked response empty', () => {
  const fields: EmployeeEditField[] = [
    { section: 'Payroll & Banking', key: 'accountNumber', label: 'Account number' },
    { section: 'Payroll & Banking', key: 'bankRoutingCode', label: 'Routing code' },
    { section: 'Payroll & Banking', key: 'bankName', label: 'Bank name' },
  ];
  const persisted = employee({ bankName: '', payrollProfile: { accountNumber: 'TEST-ACCOUNT', bankRoutingCode: 'TEST-ROUTING', bankName: 'Old nested name', wpsEligible: true, eosbEligible: true } });
  expect(buildEmployeeEditSnapshot(persisted, fields, catalog, 'SA')).toEqual({ accountNumber: 'TEST-ACCOUNT', bankRoutingCode: 'TEST-ROUTING', bankName: '' });
  expect(buildEmployeeEditSnapshot(employee({ payrollProfile: undefined, complianceRecords: [] }), fields, catalog, 'SA'))
    .toEqual({ accountNumber: '', bankRoutingCode: '', bankName: '' });
});

const editFields: EmployeeEditField[] = [
  { section: 'Personal', key: 'preferredName', label: 'Preferred name' },
  { section: 'Personal', key: 'phone', label: 'Mobile number' },
  { section: 'Personal', key: 'dateOfBirth', label: 'Date of birth', type: 'date' },
  { section: 'Employment', key: 'department', label: 'Department' },
  { section: 'Employment', key: 'joiningDate', label: 'Joining date', type: 'date' },
  { section: 'Employment', key: 'managerEmployeeId', label: 'Line manager', type: 'number' },
  { section: 'Payroll & Banking', key: 'salary', label: 'Salary', type: 'number' },
];

test('serializes deliberate clears with server-compatible values and omits untouched fields', () => {
  const form = { preferredName: '  ', phone: 'unchanged', department: ' ', dateOfBirth: '', managerEmployeeId: '', salary: ' 7200.25 ' };
  expect(buildEmployeeEditChanges(form, editFields, ['preferredName', 'department', 'dateOfBirth', 'managerEmployeeId', 'salary']))
    .toEqual({ preferredName: '', department: '', dateOfBirth: null, managerEmployeeId: null, salary: 7200.25 });
});

test('refuses unsupported clears and malformed numeric values before a patch can be sent', () => {
  for (const key of ['salary', 'joiningDate']) {
    expect(() => buildEmployeeEditChanges({ [key]: '' }, editFields, [key])).toThrow(/cannot be cleared/);
  }
  expect(() => buildEmployeeEditChanges({ salary: 'NaN' }, editFields, ['salary'])).toThrow(/valid number/);
  expect(() => buildEmployeeEditChanges({ managerEmployeeId: '2.5' }, editFields, ['managerEmployeeId'])).toThrow(/whole number/);
  expect(buildEmployeeEditChanges({ salary: '0', managerEmployeeId: '17' }, editFields, ['salary', 'managerEmployeeId']))
    .toEqual({ salary: 0, managerEmployeeId: 17 });
});

test('refuses a removed catalog key or missing draft value instead of silently clearing it', () => {
  expect(() => buildEmployeeEditChanges({ removedField: 'value' }, editFields, ['removedField'])).toThrow(/no longer available/);
  expect(() => buildEmployeeEditChanges({}, editFields, ['preferredName'])).toThrow(/no longer available/);
});
