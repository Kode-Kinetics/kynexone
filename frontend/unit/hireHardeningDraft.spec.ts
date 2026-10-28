import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { formFromEmployee, draftLoadProblem, isResumable } from '../src/lib/hire/draftHydrate';
import { duplicateStatusKey, isNameOnlyMatch } from '../src/lib/hire/duplicate';

const detail = {
  id: 44, status: 'Draft', updatedAtUtc: '2026-10-27T08:00:00Z', companyId: 'c1', englishName: 'Alex Morgan', arabicName: 'أليكس مورغان', preferredName: 'Alex',
  gender: 'Male', maritalStatus: 'Single', nationality: 'Pakistan', nationalityCode: 'PK', dateOfBirth: '1990-06-01', personalEmail: 'alex@mail.test', workEmail: 'alex.morgan@example.test',
  phone: '+966512345678', joiningDate: '2026-11-01', designationId: 'd1', gradeId: 'g1', branchId: 'b1', departmentId: 'dp1', managerEmployeeId: 17,
  employmentType: 'Full-time', contractType: 'Fixed term', contractEndDate: '2027-11-01', probationEndDate: '2027-01-30', workLocation: 'HQ',
  iqamaNumber: '2000000006', iqamaExpiryDate: '2027-01-01', passportNumber: 'AB1234567', passportExpiryDate: '2030-05-05', idNumber: '', borderNumber: '', civilId: '',
  payrollProfile: { iban: 'SA0380000000608010167519', molId: '1-123', wpsEligible: true, eosbEligible: true },
  salaryBreakdown: { basicSalary: 12000.5, housingAllowance: 3000, currency: 'SAR' }, complianceRecords: [], documents: [], history: [], transfers: [],
} as never;

test.describe('CD26 draft resume', () => {
  test('the employee record fills the wizard form', () => {
    const f = formFromEmployee(detail, { emailDomain: 'example.test', companyCountry: 'SA' });
    expect(f).toMatchObject({
      englishName: 'Alex Morgan', companyId: 'c1', startDate: '2026-11-01', designationId: 'd1', gradeId: 'g1', branchId: 'b1', departmentId: 'dp1', managerId: '17',
      arabicName: 'أليكس مورغان', preferredName: 'Alex', gender: 'Male', maritalStatus: 'Single', nationalityCode: 'PK', dob: '1990-06-01', personalEmail: 'alex@mail.test',
      workEmail: 'alex.morgan@example.test', mobileCountry: 'SA', mobileNational: '0512345678', employmentType: 'Full-time', contractType: 'Fixed term', contractEndDate: '2027-11-01',
      probationEndDate: '2027-01-30', workLocation: 'HQ', iban: 'SA0380000000608010167519', molId: '1-123', basic: '12000.5', housing: '3000',
      idType: 'iqama', idNumber: '2000000006', idExpiry: '2027-01-01', passportNumber: 'AB1234567', passportExpiry: '2030-05-05',
    });
  });
  test('a work email on another domain is kept whole', () => {
    const f = formFromEmployee({ ...(detail as object), workEmail: 'alex@other.test' } as never, { emailDomain: 'example.test', companyCountry: 'SA' });
    expect(f.workEmail).toBe('alex@other.test');
  });
  test('the ID type follows whichever number is stored; a passport-only person is a passport', () => {
    const only = { ...(detail as object), iqamaNumber: '', iqamaExpiryDate: undefined, passportNumber: 'AB1234567' } as never;
    const f = formFromEmployee(only, { companyCountry: 'SA' });
    expect(f.idType).toBe('passport');
    expect(f.idNumber).toBe('AB1234567');
    expect(f.passportNumber).toBe('');
    expect(formFromEmployee({ ...(detail as object), iqamaNumber: '', borderNumber: '3000000004' } as never, { companyCountry: 'SA' }).idType).toBe('border');
    expect(formFromEmployee({ ...(detail as object), iqamaNumber: '', idNumber: '1000000008', passportNumber: '' } as never, { companyCountry: 'SA' }).idType).toBe('nationalId');
  });
  test('only the same id, still a Draft, can be resumed', () => {
    expect(isResumable(detail, 44)).toBe(true);
    expect(isResumable(detail, 45)).toBe(false);
    expect(isResumable({ ...(detail as object), status: 'Active' } as never, 44)).toBe(false);
  });
  test('404 and 403 are the same friendly "unavailable"; nothing is guessed on a network failure', () => {
    expect(draftLoadProblem({ response: { status: 404 } })).toBe('unavailable');
    expect(draftLoadProblem({ response: { status: 403 } })).toBe('unavailable');
    expect(draftLoadProblem({})).toBe('network');
    expect(draftLoadProblem({ response: { status: 500 } })).toBe('network');
  });
});

test.describe('CD26 duplicate panel helpers', () => {
  test('name-only is a "possible" match whose signals name the name', () => {
    expect(isNameOnlyMatch({ matchType: 'possible', signals: ['Same name'] })).toBe(true);
    expect(isNameOnlyMatch({ matchType: 'possible', signals: ['name'] })).toBe(true);
    expect(isNameOnlyMatch({ matchType: 'strong', signals: ['Iqama number match'] })).toBe(false);
    expect(isNameOnlyMatch({ matchType: 'possible', signals: [] })).toBe(false);
  });
  test('every status has a key with an English and a different Arabic entry', () => {
    for (const s of ['Active', 'Draft', 'Terminated', 'Resigned', 'Exited', 'Archived', 'Offboarded', 'Retired', 'Absconded', 'Probation', 'OnLeave', 'Suspended', 'Inactive']) {
      const k = duplicateStatusKey(s);
      expect(LOCALE_DICTS.en[k], s).toBeTruthy();
      expect(LOCALE_DICTS.ar[k], s).toBeTruthy();
      expect(LOCALE_DICTS.ar[k], s).not.toBe(k);
    }
    expect(duplicateStatusKey('Mystery')).toBe('Mystery');
  });
});
