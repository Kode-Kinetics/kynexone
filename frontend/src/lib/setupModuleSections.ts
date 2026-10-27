import { msg } from '../i18n/translations';
import { GL_AND_RATES_PERMISSIONS } from './setupAccess';

/**
 * The configuration screens of the other modules, shown as Setup sections. Setup is the one place configuration is
 * edited (as in BambooHR, HiBob, Personio, SuccessFactors' Admin Center, Salesforce Setup); a module keeps its daily
 * work and links here for its settings.
 *
 * Each section opens for exactly the people its old tab opened for, narrowed to what the API serves them:
 * - `permissions`: any one of them (as PermissionGate),
 * - `roles`: and, when listed, any one of them,
 * - `modulePath` + `featureKey`: and its module is switched on. A section under /setup no longer passes through its
 *   module's route gate (ModuleGate checks the path in the address bar), so it checks its module itself.
 *
 * Tab ids are the stable URL contract (`/setup?tab=…`); src/lib/setupLegacyRoutes.ts maps the old addresses here.
 */
export type SetupModuleSectionId =
  | 'openingBalances'
  | 'leaveTypes' | 'leavePolicies' | 'holidayCalendars' | 'shiftDefinitions' | 'shiftPolicy' | 'attendanceRules' | 'overtimePolicies' | 'attendanceDevices'
  | 'benefitPlans' | 'loanTypes' | 'loanPolicies' | 'advancePolicy' | 'bonusTypes' | 'payslipTemplates'
  | 'onboardingChecklists' | 'onboardingChecklistTemplates' | 'hireTemplates' | 'performanceScorecards' | 'letterTemplates' | 'engagementPrivacy'
  | 'taxPolicies' | 'complianceProfiles' | 'saudiCompliance' | 'countryPayrollKeys'
  | 'users' | 'roles' | 'permissionMatrix' | 'permissions' | 'permissionGrantors' | 'approvalAuthorities' | 'approvalDelegations' | 'signInSecurity' | 'accessLog'
  | 'localization' | 'branding' | 'helpText' | 'modules' | 'planBilling';

export type SetupModuleGroupId = 'getStarted' | 'timeLeave' | 'payBenefits' | 'talentServices' | 'payrollCompliance' | 'accessSecurity' | 'workspace';

export interface SetupModuleSection {
  id: SetupModuleSectionId;
  label: string;
  description: string;
  group: SetupModuleGroupId;
  permissions: readonly string[];
  roles?: readonly string[];
  /** The module's own page: its on/off switch is read for this path. */
  modulePath: string;
  featureKey?: string;
}

export interface SetupModuleAccess {
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
  isFeatureEnabled: (key: string) => boolean;
  verdictForPath: (path: string) => { allowed: boolean };
  flagStatus: 'loading' | 'ready' | 'error';
}

/** Who may change leave and shift settings (the APIs' writes); a settings section opens for whoever can change it. */
const HR_SETTINGS_EDITORS = ['Admin', 'HR Manager'] as const;
const LEAVE = ['leave.read', 'leave.write'] as const;
const LOANS = ['loans.read', 'loans.write'] as const;
// Users & access and the workspace are served by security.manage alone (AccessController.cs:23, TenantAdminController.cs:17):
// users.manage and roles.manage open nothing in them, so a section never lists them.
const SECURITY = ['security.manage'] as const;
const TENANT = SECURITY;

export const SETUP_MODULE_SECTIONS: readonly SetupModuleSection[] = [
  // Get started
  { id: 'openingBalances', group: 'getStarted', label: msg('Opening balances'), description: msg('Load leave, loan and end-of-service balances when going live.'), permissions: ['employees.write', 'payroll.write'], roles: ['Admin', 'HR Manager'], modulePath: '/opening-balances' },

  // Time & leave
  { id: 'leaveTypes', group: 'timeLeave', label: msg('Leave types'), description: msg('The kinds of leave people can take, with their entitlement and pay rules.'), permissions: LEAVE, roles: HR_SETTINGS_EDITORS, modulePath: '/leave' },
  { id: 'leavePolicies', group: 'timeLeave', label: msg('Leave policies'), description: msg('Who gets which leave, accrual, carry-forward and encashment.'), permissions: LEAVE, roles: HR_SETTINGS_EDITORS, modulePath: '/leave' },
  { id: 'holidayCalendars', group: 'timeLeave', label: msg('Holiday calendars'), description: msg('Public holidays for each country and company.'), permissions: LEAVE, roles: HR_SETTINGS_EDITORS, modulePath: '/leave' },
  { id: 'shiftDefinitions', group: 'timeLeave', label: msg('Shifts'), description: msg('Shift times, breaks and grace periods.'), permissions: ['attendance.read'], roles: HR_SETTINGS_EDITORS, modulePath: '/shifts', featureKey: 'shifts' },
  { id: 'shiftPolicy', group: 'timeLeave', label: msg('Shift policy'), description: msg('Rotation, rest-day and roster rules.'), permissions: ['attendance.read'], roles: ['Admin', 'HR Manager'], modulePath: '/shifts', featureKey: 'shifts' }, // GET /api/shifts/policy is Admin, HR Manager only
  { id: 'attendanceRules', group: 'timeLeave', label: msg('Attendance rules'), description: msg('What counts as late, an early exit or a half day, and the length of the working day.'), permissions: ['attendance.policy_manage'], modulePath: '/attendance' }, // every /api/attendance/policies action needs attendance.policy_manage (Admin, HR Manager, HR Director)
  { id: 'overtimePolicies', group: 'timeLeave', label: msg('Overtime policies'), description: msg('Overtime rates, caps and approval rules.'), permissions: ['overtime.read', 'overtime.write'], roles: ['Admin', 'HR Manager'], modulePath: '/overtime', featureKey: 'overtime' },
  { id: 'attendanceDevices', group: 'timeLeave', label: msg('Attendance devices'), description: msg('Biometric devices and other punch sources, and their connection keys.'), permissions: ['attendance.read', 'attendance.write'], roles: ['Admin', 'HR Manager', 'HR Officer', 'Auditor'], modulePath: '/attendance' },

  // Pay & benefits
  { id: 'benefitPlans', group: 'payBenefits', label: msg('Benefit plans'), description: msg('The benefits on offer and their eligibility rules.'), permissions: ['employees.write'], roles: ['Admin', 'HR Manager', 'HR Officer', 'Finance', 'Auditor'], modulePath: '/benefits' },
  { id: 'loanTypes', group: 'payBenefits', label: msg('Loan types'), description: msg('The kinds of loan employees can request.'), permissions: LOANS, roles: ['Admin', 'Finance', 'Finance Approver', 'HR Manager', 'HR Director'], modulePath: '/loans' },
  { id: 'loanPolicies', group: 'payBenefits', label: msg('Loan policies'), description: msg('Loan limits by grade, instalments and eligibility.'), permissions: LOANS, roles: ['Admin', 'HR Manager', 'HR Director'], modulePath: '/loans' },
  { id: 'advancePolicy', group: 'payBenefits', label: msg('Salary advance policy'), description: msg('How much can be advanced, how often, and how it is repaid.'), permissions: LOANS, roles: ['Admin', 'Finance', 'Finance Approver', 'HR Manager', 'HR Director'], modulePath: '/loans' },
  { id: 'bonusTypes', group: 'payBenefits', label: msg('Bonus types'), description: msg('The kinds of bonus paid through payroll.'), permissions: LOANS, roles: ['Admin', 'Finance', 'Finance Approver', 'HR Manager', 'HR Director'], modulePath: '/loans' },
  { id: 'payslipTemplates', group: 'payBenefits', label: msg('Payslip templates'), description: msg('The layout of the payslips employees receive.'), permissions: ['payroll.read'], roles: ['Admin', 'HR Manager', 'Payroll Manager', 'Payroll Officer'], modulePath: '/payroll/templates', featureKey: 'payslip_template_designer' },

  // Talent & employee services
  { id: 'onboardingChecklists', group: 'talentServices', label: msg('Onboarding checklists'), description: msg('The tasks a new joiner and their team complete, as reusable checklists.'), permissions: ['recruitment.write'], roles: ['Admin', 'HR Manager'], modulePath: '/recruitment', featureKey: 'recruitment' },
  { id: 'onboardingChecklistTemplates', group: 'talentServices', label: msg('Onboarding checklist templates'), description: msg('The tasks every new hire gets, who does them and when they are due, per company.'), permissions: ['onboarding.templates.manage'], roles: ['Admin', 'HR Manager'], modulePath: '/people' },
  { id: 'hireTemplates', group: 'talentServices', label: msg('Hire templates'), description: msg('Presets that fill the Add Employee form for a kind of hire and pick its onboarding checklist.'), permissions: ['onboarding.templates.manage'], roles: ['Admin', 'HR Manager'], modulePath: '/people' },
  { id: 'performanceScorecards', group: 'talentServices', label: msg('Review scorecards'), description: msg('The review forms and rating scales used in performance cycles.'), permissions: ['performance.cycle_manage'], modulePath: '/performance', featureKey: 'performance' },
  { id: 'letterTemplates', group: 'talentServices', label: msg('Letter templates'), description: msg('Salary certificates, experience letters and other HR letters.'), permissions: ['employees.write'], roles: ['Admin', 'HR Manager'], modulePath: '/hr-letters' },
  { id: 'engagementPrivacy', group: 'talentServices', label: msg('Survey privacy'), description: msg('The smallest group whose survey results can be shown.'), permissions: ['engagement.manage'], modulePath: '/engagement', featureKey: 'engagement' },

  // Payroll & compliance
  { id: 'taxPolicies', group: 'payrollCompliance', label: msg('Tax policies'), description: msg('Tax and contribution rules applied during payroll.'), permissions: ['payroll.read'], roles: ['Admin', 'HR Director', 'Payroll Manager', 'Finance Approver', 'Auditor'], modulePath: '/tax-policies' },
  { id: 'complianceProfiles', group: 'payrollCompliance', label: msg('Compliance profiles'), description: msg('Country and company rules that compliance checks run against.'), permissions: ['compliance.read'], roles: ['Admin', 'HR Director', 'HR Manager', 'Compliance Officer', 'Auditor'], modulePath: '/compliance-profiles' },
  { id: 'saudiCompliance', group: 'payrollCompliance', label: msg('Saudi compliance'), description: msg('Qiwa, GOSI, Mudad and WPS details for Saudi companies.'), permissions: ['qiwa.read'], modulePath: '/saudi-compliance' },
  { id: 'countryPayrollKeys', group: 'payrollCompliance', label: msg('Country payroll keys'), description: msg('Extra per-country payroll values, such as weekend days.'), permissions: TENANT, modulePath: '/tenant-admin' },

  // Users & access
  { id: 'users', group: 'accessSecurity', label: msg('Users'), description: msg('Who can sign in, their roles and companies.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'roles', group: 'accessSecurity', label: msg('Roles'), description: msg('Roles and what each one may do.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'permissionMatrix', group: 'accessSecurity', label: msg('Permission matrix'), description: msg('Every role against every permission, on one grid.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'permissions', group: 'accessSecurity', label: msg('Permissions'), description: msg('The catalogue of permissions the system knows.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'permissionGrantors', group: 'accessSecurity', label: msg('Permission grantors'), description: msg('Who may grant which permissions to others.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'approvalAuthorities', group: 'accessSecurity', label: msg('Approval authorities'), description: msg('Who approves what, and up to which amount.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'approvalDelegations', group: 'accessSecurity', label: msg('Approval delegations'), description: msg('Approvers standing in for others while they are away.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'signInSecurity', group: 'accessSecurity', label: msg('Sign-in security'), description: msg('Passwords, two-step verification and session limits.'), permissions: SECURITY, modulePath: '/user-management' },
  { id: 'accessLog', group: 'accessSecurity', label: msg('Sign-in and access log'), description: msg('Sign-ins, failed attempts and access changes.'), permissions: SECURITY, roles: ['Admin'], modulePath: '/user-management' },

  // Workspace
  { id: 'localization', group: 'workspace', label: msg('Language and region'), description: msg('Languages, calendar, date and number formats, and currency.'), permissions: TENANT, modulePath: '/tenant-admin' },
  { id: 'branding', group: 'workspace', label: msg('Branding'), description: msg('Your logo, names and portal title.'), permissions: TENANT, modulePath: '/tenant-admin' },
  { id: 'helpText', group: 'workspace', label: msg('Field help text'), description: msg('The hints shown beside form fields.'), permissions: TENANT, modulePath: '/tenant-admin' },
  { id: 'modules', group: 'workspace', label: msg('Modules'), description: msg('Switch modules on or off for your organisation.'), permissions: TENANT, modulePath: '/tenant-admin' },
  { id: 'planBilling', group: 'workspace', label: msg('Plan and billing'), description: msg('Your subscription, usage and invoices.'), permissions: TENANT, modulePath: '/tenant-admin' },
];

/** Opens /setup (route gate, menu entry and ⌘K): any permission of any Setup section. */
export const SETUP_ROUTE_PERMISSIONS: readonly string[] = [...new Set<string>([
  'organization.read', 'organization.write', 'organization.establishment.write',
  ...GL_AND_RATES_PERMISSIONS,
  ...SETUP_MODULE_SECTIONS.flatMap(section => section.permissions),
])];

export function moduleSectionAvailable(section: SetupModuleSection, access: SetupModuleAccess): boolean {
  if (!section.permissions.some(access.hasPermission)) return false;
  if (section.roles?.length && !section.roles.some(access.hasRole)) return false;
  // Until the module switches are known, a module's settings are not offered (ModuleGate waits the same way).
  if (access.flagStatus === 'loading') return false;
  if (section.featureKey && !access.isFeatureEnabled(section.featureKey)) return false;
  return access.verdictForPath(section.modulePath).allowed;
}

export function isSetupModuleSectionId(id: string): id is SetupModuleSectionId {
  return SETUP_MODULE_SECTIONS.some(section => section.id === id);
}
