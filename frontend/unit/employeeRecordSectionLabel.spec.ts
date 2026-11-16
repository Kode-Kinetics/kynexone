import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import { employeeWorkspace } from '../src/i18n/employeeWorkspace';
import { translate } from '../src/i18n/translations';

/**
 * "التوظيف" is the Recruitment menu item. The employee record's own Employment section carries its own key so it can read
 * differently in Arabic without changing the wizard step or any other screen that shares the plain "Employment" key.
 */
const SECTION_KEY = 'Employee record section: Employment';

test.describe('the employee record Employment section label', () => {
  test('has its own key, in English and Arabic, and the Arabic is not the Recruitment label', () => {
    expect(employeeWorkspace.en[SECTION_KEY]).toBe('Employment');
    expect(employeeWorkspace.ar[SECTION_KEY]).toBe('بيانات الوظيفة');
    // What the screen actually shows, after every dictionary has been merged.
    expect(translate('en', SECTION_KEY)).toBe('Employment');
    expect(translate('ar', SECTION_KEY)).toBe('بيانات الوظيفة');
    expect(translate('ar', SECTION_KEY)).not.toBe(translate('ar', 'Recruitment'));
  });

  test('the shared Employment key keeps its wording for the other screens that use it', () => {
    expect(employeeWorkspace.ar['Employment']).toBe('التوظيف');
  });

  test('the record navigation reads the section key, not the shared one', () => {
    const shell = fs.readFileSync(path.join(__dirname, '../src/views/employees/EmployeeWorkspaceShell.tsx'), 'utf8');
    expect(shell).toContain(`'${SECTION_KEY}'`);
    expect(shell).not.toMatch(/id: 'employment', label: 'Employment'/);
  });
});
