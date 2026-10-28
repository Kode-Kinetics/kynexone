import { msg, type LocaleCode, type MessageParams } from '../i18n/translations';

/**
 * Plain-language names for the module part of a permission key (`payroll.read` → "Payroll"), so a
 * screen can say "Needs payroll access" instead of showing the raw key. Keyed by module, not by full
 * permission, so a new `payroll.*` permission is described without touching this file.
 */
export const PERMISSION_AREA_LABELS: Record<string, string> = {
  dashboard: msg('Dashboard'),
  ess: msg('Self-Service'),
  employees: msg('Employee records'),
  onboarding: msg('Onboarding'),
  attendance: msg('Attendance'),
  leave: msg('Leave'),
  overtime: msg('Overtime'),
  manager: msg('Team'),
  payroll: msg('Payroll'),
  loans: msg('Loans'),
  entitlements: msg('Benefits'),
  benefits: msg('Benefits'),
  contracts: msg('Contracts'),
  compensation: msg('Compensation'),
  recruitment: msg('Recruitment'),
  performance: msg('Performance'),
  engagement: msg('Engagement'),
  succession: msg('Succession'),
  learning: msg('Learning'),
  compliance: msg('Compliance'),
  qiwa: msg('Qiwa'),
  ai: msg('Kody'),
  reports: msg('Reports'),
  approvals: msg('Approvals'),
  users: msg('User management'),
  roles: msg('User management'),
  security: msg('Security'),
  organization: msg('Organization setup'),
  finance: msg('Finance'),
};

/** Names that keep their capital in the middle of an English sentence. */
const PROPER_NAMES = new Set(['Qiwa', 'Kody']);

type Translate = (key: string, params?: MessageParams) => string;

/** The module of a permission key, e.g. `payroll.rates.read` → `payroll`. */
export const permissionModule = (permission: string) => permission.split('.')[0] ?? permission;

/** A module with no entry above is still described in words (its key, de-snaked), never as a raw key. */
function areaLabel(module: string, locale: LocaleCode, t: Translate): string {
  const key = PERMISSION_AREA_LABELS[module];
  if (!key) return module.replace(/[_-]+/g, ' ');
  const label = t(key);
  // The keys are title-style nav words; mid-sentence, Latin-script languages want them in lower case.
  return PROPER_NAMES.has(key) || locale === 'ar' ? label : label.toLocaleLowerCase(locale);
}

/**
 * "Needs payroll access" / "Needs employee records or payroll access" for a list of permission keys,
 * any one of which opens the screen. Areas are de-duplicated and joined with the locale's own "or".
 */
export function permissionAccessDescription(permissions: readonly string[], locale: LocaleCode, t: Translate): string {
  const areas = [...new Set(permissions.map((permission) => areaLabel(permissionModule(permission), locale, t)))];
  if (areas.length === 0) return t('Open module');
  const joined = new Intl.ListFormat(locale, { style: 'long', type: 'disjunction' }).format(areas);
  return t('Needs {areas} access', { areas: joined });
}
