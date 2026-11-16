import { test, expect } from '@playwright/test';
import {
  requisitionRequestBody,
  openingFormDefaults,
  offerBandWarning,
  type CatalogueOptions,
} from '../src/lib/recruitmentCatalogue';
import { LOCALE_DICTS } from '../src/i18n/translations';

const options: CatalogueOptions = {
  departments: [{ id: 'd1', name: 'Finance', code: 'FIN' }],
  designations: [{ id: 'g1', name: 'Accountant', code: 'ACC' }],
  grades: [{ id: 'gr1', name: 'Grade 4', code: 'G4', minSalary: 8000, midSalary: 10000, maxSalary: 12000, currency: 'SAR' }],
};

test.describe('requisition form sends catalogue records, not typed text', () => {
  test('the chosen department, designation and grade go as ids with the catalogue names', () => {
    const body = requisitionRequestBody({
      departmentId: 'd1', designationId: 'g1', gradeId: 'gr1', headCount: 2, employmentType: 'Full-Time', priority: 'High',
      justification: 'Growth', requiredSkills: 'IFRS', minExperienceYears: '3', maxExperienceYears: '', budgetFrom: '9000', budgetTo: '11000',
      targetJoiningDate: '', requestedByName: 'Hana',
    }, options);
    expect(body.departmentId).toBe('d1');
    expect(body.departmentName).toBe('Finance');
    expect(body.designationId).toBe('g1');
    expect(body.designationTitle).toBe('Accountant');
    expect(body.gradeId).toBe('gr1');
    expect(body.minExperienceYears).toBe(3);
    expect(body.maxExperienceYears).toBeUndefined();
    expect(body.budgetFrom).toBe(9000);
    expect(body.targetJoiningDate).toBeUndefined();
  });

  test('an unpicked department leaves no name behind to be mistaken for a record', () => {
    const body = requisitionRequestBody({
      departmentId: '', designationId: 'g1', gradeId: '', headCount: 1, employmentType: 'Full-Time', priority: 'Medium',
      justification: 'x', requiredSkills: '', minExperienceYears: '', maxExperienceYears: '', budgetFrom: '', budgetTo: '',
      targetJoiningDate: '', requestedByName: '',
    }, options);
    expect(body.departmentId).toBeUndefined();
    expect(body.departmentName).toBe('');
    expect(body.gradeId).toBeUndefined();
  });
});

test.describe('an opening starts from its requisition', () => {
  const requisition = {
    id: 'r1', departmentId: 'd1', departmentName: 'Finance', designationId: 'g1', designationTitle: 'Accountant', gradeId: 'gr1',
    headCount: 2, employmentType: 'Contract', requiredSkills: 'IFRS\nExcel', budgetFrom: 9000, budgetTo: 11000,
  };

  test('department, designation, grade, headcount, budget range and skills are carried over', () => {
    const form = openingFormDefaults(requisition);
    expect(form).toMatchObject({
      departmentId: 'd1', designationId: 'g1', gradeId: 'gr1', title: 'Accountant', headCount: 2, employmentType: 'Contract',
      salaryFrom: '9000', salaryTo: '11000', requirements: 'IFRS\nExcel',
    });
  });

  test('a stand-alone opening starts empty', () => {
    const form = openingFormDefaults(null);
    expect(form).toMatchObject({ departmentId: '', designationId: '', gradeId: '', salaryFrom: '', salaryTo: '', requirements: '', headCount: 1 });
  });
});

test.describe('offer grade-band check', () => {
  const grade = options.grades[0];

  test('inside the band gives no warning', () => {
    expect(offerBandWarning(10000, grade)).toBeNull();
    expect(offerBandWarning(8000, grade)).toBeNull();
    expect(offerBandWarning(12000, grade)).toBeNull();
  });

  test('outside the band warns with the direction and the limit; it never blocks', () => {
    expect(offerBandWarning(7999, grade)).toEqual({ side: 'below', limit: 8000, currency: 'SAR', gradeName: 'Grade 4' });
    expect(offerBandWarning(12001, grade)).toEqual({ side: 'above', limit: 12000, currency: 'SAR', gradeName: 'Grade 4' });
  });

  test('no grade, an unset band or no amount gives no warning', () => {
    expect(offerBandWarning(5000, null)).toBeNull();
    expect(offerBandWarning(5000, { ...grade, minSalary: 0, maxSalary: 0 })).toBeNull();
    expect(offerBandWarning(0, grade)).toBeNull();
  });
});

test.describe('wording', () => {
  test('every new label has an Arabic translation', () => {
    const ar = LOCALE_DICTS.ar;
    for (const key of [
      'Grade', 'Choose a department', 'Choose a designation', 'Choose a grade',
      'The offer is below the minimum of {gradeName} ({currency} {limit}).',
      'The offer is above the maximum of {gradeName} ({currency} {limit}).',
    ]) expect(ar[key], key).toBeTruthy();
  });
});
