import { msg } from '../i18n/translations';
import type { AttendancePolicy, AttendancePolicyRequest } from '../api/attendance';

/**
 * Setup → Attendance rules: the form's value rules. They are the ranges AttendancePoliciesController.ValidateAsync
 * enforces, so a value the server would refuse is stopped before it is sent, with the same reason in plain words.
 * Only the settings attendance processing reads are here (grace, early exit, half day, working day, break).
 */
export const ATTENDANCE_RULE_LIMITS = {
  graceMinutes: { min: 0, max: 120 },
  earlyExitThresholdMinutes: { min: 0, max: 240 },
  standardWorkMinutes: { min: 60, max: 960 },
} as const;

/** The editor's fields. Numbers are kept as typed so an empty box is "empty", not 0. */
export interface AttendanceRuleForm {
  code: string;
  name: string;
  /** '' = every branch / department / grade. */
  branchId: string;
  departmentId: string;
  gradeId: string;
  graceMinutes: string;
  earlyExitThresholdMinutes: string;
  halfDayThresholdMinutes: string;
  standardWorkMinutes: string;
  breakMinutes: string;
}

export type AttendanceRuleErrors = Partial<Record<keyof AttendanceRuleForm, string>>;

/** What a new policy starts from: the same figures the built-in DEFAULT policy uses. */
export const NEW_RULE_FORM: AttendanceRuleForm = {
  code: '', name: '', branchId: '', departmentId: '', gradeId: '',
  graceMinutes: '10', earlyExitThresholdMinutes: '15', halfDayThresholdMinutes: '240', standardWorkMinutes: '480', breakMinutes: '60',
};

const whole = (value: string): number | null => (/^-?\d+$/.test(value.trim()) ? Number(value.trim()) : null);

export function validateRuleForm(form: AttendanceRuleForm): AttendanceRuleErrors {
  const errors: AttendanceRuleErrors = {};
  if (!/^[A-Z0-9][A-Z0-9_-]{0,39}$/.test(form.code.trim().toUpperCase())) errors.code = msg('Use 1 to 40 letters, digits, hyphens or underscores, with no spaces.');
  if (!form.name.trim() || form.name.trim().length > 120) errors.name = msg('Give the policy a name of up to 120 characters.');
  const range = (field: 'graceMinutes' | 'earlyExitThresholdMinutes' | 'standardWorkMinutes', message: string) => {
    const value = whole(form[field]);
    const { min, max } = ATTENDANCE_RULE_LIMITS[field];
    if (value === null || value < min || value > max) errors[field] = message;
  };
  range('graceMinutes', msg('Enter a whole number of minutes from 0 to 120.'));
  range('earlyExitThresholdMinutes', msg('Enter a whole number of minutes from 0 to 240.'));
  range('standardWorkMinutes', msg('Enter a whole number of minutes from 60 to 960.'));
  const standard = whole(form.standardWorkMinutes);
  const rest = whole(form.breakMinutes);
  if (rest === null || rest < 0 || (standard !== null && rest >= standard)) errors.breakMinutes = msg('Enter a whole number of minutes, shorter than the working day.');
  const half = whole(form.halfDayThresholdMinutes);
  if (half === null || half < 1 || (standard !== null && half > standard)) errors.halfDayThresholdMinutes = msg('Enter a whole number of minutes above 0 and no more than the working day.');
  return errors;
}

export function ruleFormToRequest(form: AttendanceRuleForm): AttendancePolicyRequest {
  return {
    code: form.code.trim().toUpperCase(),
    name: form.name.trim(),
    branchId: form.branchId || null,
    departmentId: form.departmentId || null,
    gradeId: form.gradeId || null,
    graceMinutes: Number(form.graceMinutes),
    earlyExitThresholdMinutes: Number(form.earlyExitThresholdMinutes),
    halfDayThresholdMinutes: Number(form.halfDayThresholdMinutes),
    standardWorkMinutes: Number(form.standardWorkMinutes),
    breakMinutes: Number(form.breakMinutes),
  };
}

export function ruleFormFromPolicy(policy: AttendancePolicy): AttendanceRuleForm {
  return {
    code: policy.code, name: policy.name,
    branchId: policy.branchId ?? '', departmentId: policy.departmentId ?? '', gradeId: policy.gradeId ?? '',
    graceMinutes: String(policy.graceMinutes), earlyExitThresholdMinutes: String(policy.earlyExitThresholdMinutes),
    halfDayThresholdMinutes: String(policy.halfDayThresholdMinutes), standardWorkMinutes: String(policy.standardWorkMinutes),
    breakMinutes: String(policy.breakMinutes),
  };
}

export const isCompanyWide = (policy: Pick<AttendancePolicy, 'branchId' | 'departmentId' | 'gradeId'>) =>
  !policy.branchId && !policy.departmentId && !policy.gradeId;
