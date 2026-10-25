import { expect, test } from '@playwright/test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { ATTENDANCE_RULE_LIMITS, ruleFormFromPolicy, ruleFormToRequest, validateRuleForm, type AttendanceRuleForm } from '../src/lib/attendanceRules';
import { SETUP_TOOLS } from '../src/lib/setupAreas';

/**
 * Setup → Attendance rules. The form checks the same ranges as AttendancePoliciesController.ValidateAsync, so a value the
 * server would refuse is stopped before it is sent, with the same reason in plain words.
 */
const good: AttendanceRuleForm = {
  code: 'RYD-DAY', name: 'Riyadh day', branchId: '', departmentId: '', gradeId: '',
  graceMinutes: '10', earlyExitThresholdMinutes: '15', halfDayThresholdMinutes: '240', standardWorkMinutes: '480', breakMinutes: '60',
};

test('a_complete_form_is_valid_and_becomes_a_numeric_request_with_an_upper_case_code', () => {
  expect(validateRuleForm({ ...good, code: ' ryd-day ' })).toEqual({});
  expect(ruleFormToRequest({ ...good, code: ' ryd-day ', branchId: 'b-1' })).toEqual({
    code: 'RYD-DAY', name: 'Riyadh day', branchId: 'b-1', departmentId: null, gradeId: null,
    graceMinutes: 10, earlyExitThresholdMinutes: 15, halfDayThresholdMinutes: 240, standardWorkMinutes: 480, breakMinutes: 60,
  });
});

test('every_range_the_server_enforces_is_enforced_before_sending', () => {
  const cases: [Partial<AttendanceRuleForm>, keyof AttendanceRuleForm][] = [
    [{ code: '' }, 'code'], [{ code: 'A B' }, 'code'], [{ code: 'x'.repeat(41) }, 'code'],
    [{ name: '  ' }, 'name'], [{ name: 'n'.repeat(121) }, 'name'],
    [{ graceMinutes: '-1' }, 'graceMinutes'], [{ graceMinutes: '121' }, 'graceMinutes'], [{ graceMinutes: '' }, 'graceMinutes'], [{ graceMinutes: '1.5' }, 'graceMinutes'],
    [{ earlyExitThresholdMinutes: '241' }, 'earlyExitThresholdMinutes'],
    [{ standardWorkMinutes: '59' }, 'standardWorkMinutes'], [{ standardWorkMinutes: '961' }, 'standardWorkMinutes'],
    [{ breakMinutes: '-5' }, 'breakMinutes'], [{ breakMinutes: '480' }, 'breakMinutes'],
    [{ halfDayThresholdMinutes: '0' }, 'halfDayThresholdMinutes'], [{ halfDayThresholdMinutes: '481' }, 'halfDayThresholdMinutes'],
  ];
  for (const [change, field] of cases) {
    expect(Object.keys(validateRuleForm({ ...good, ...change })), JSON.stringify(change)).toContain(field);
  }
  expect(ATTENDANCE_RULE_LIMITS.graceMinutes).toEqual({ min: 0, max: 120 });
});

test('editing_a_policy_round_trips_its_values_and_scope', () => {
  const form = ruleFormFromPolicy({
    id: 'p-1', code: 'GR1', name: 'Grade one', branchId: null, departmentId: 'd-1', gradeId: null,
    graceMinutes: 5, earlyExitThresholdMinutes: 0, halfDayThresholdMinutes: 200, standardWorkMinutes: 450, breakMinutes: 45, isActive: true,
  });
  expect(form).toMatchObject({ code: 'GR1', departmentId: 'd-1', branchId: '', graceMinutes: '5', earlyExitThresholdMinutes: '0' });
  expect(ruleFormToRequest(form)).toMatchObject({ code: 'GR1', departmentId: 'd-1', branchId: null, graceMinutes: 5, earlyExitThresholdMinutes: 0 });
});

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? sourceFiles(path) : /\.(ts|tsx)$/.test(name) ? [path] : [];
  });
}

test('the_policy_starter_is_retired_from_setup', () => {
  expect(Object.keys(SETUP_TOOLS)).not.toContain('policyStarter');
  const importsStudio = sourceFiles('src').filter(file => /from\s+['"][^'"]*AiSetupAssistant['"]/.test(readFileSync(file, 'utf8')));
  expect(importsStudio).toEqual([]);
  const linksToStudio = sourceFiles('src').filter(file => readFileSync(file, 'utf8').includes('path=policyStarter')
    && !file.endsWith('setupPathState.ts'));
  expect(linksToStudio).toEqual([]);
});

test('the_company_wizard_points_attendance_rules_at_their_setup_section', () => {
  const more = readFileSync('src/components/companySetup/MoreSettingsLinks.tsx', 'utf8');
  expect(more).toContain("id: 'attendanceRules'");
  expect(more).not.toMatch(/Attendance rules and HR approval rules have no screen/);
  const wizardCopy = readFileSync('src/i18n/companyWizard.ts', 'utf8');
  expect(wizardCopy).not.toContain('Policy starter');
});
