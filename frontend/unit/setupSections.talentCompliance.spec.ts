import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleAccess } from '../src/lib/setupModuleSections';

/**
 * Who may open each Talent & employee services and Payroll & compliance section in Setup. A section opens for what its
 * old module tab opened for, narrowed to what the API serves for READ, so nobody is shown a screen that answers 403 on
 * load. Each case cites the backend rule (paths under backend-dotnet/Zayra.Api). countryPayrollKeys belongs to the
 * Access & workspace spec.
 */

const persona = (roles: string[], permissions: string[], overrides: Partial<SetupModuleAccess> = {}): SetupModuleAccess => ({
  hasPermission: key => permissions.includes(key),
  hasRole: role => roles.includes(role),
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
  ...overrides,
});
const canOpen = (id: string, access: SetupModuleAccess) => {
  const found = SETUP_MODULE_SECTIONS.find(section => section.id === id);
  expect(found, `Missing Setup section ${id}`).toBeDefined();
  return moduleSectionAvailable(found!, access);
};

const employee = persona(['Employee'], ['ess.read', 'leave.read', 'attendance.read', 'performance.read']);

test('onboarding_checklists_open_for_Admin_and_HR_Manager_only', () => {
  // Controllers/Recruitment/OnboardingController.cs:40-41 and :77-78: creating a checklist or a template task is
  // Admin, HR Manager. Reading is wider (:28-29, :61-62 add HR Officer and Recruiter), but this section is the editor,
  // so a Recruiter who holds recruitment.write would be shown a form whose every save is refused.
  const hrManager = persona(['HR Manager'], ['recruitment.read', 'recruitment.write']);
  const recruiter = persona(['Recruiter'], ['recruitment.read', 'recruitment.write']);
  const hrOfficer = persona(['HR Officer'], ['recruitment.read']);
  expect(canOpen('onboardingChecklists', hrManager)).toBe(true);
  expect(canOpen('onboardingChecklists', recruiter)).toBe(false);
  expect(canOpen('onboardingChecklists', hrOfficer)).toBe(false);
  expect(canOpen('onboardingChecklists', employee)).toBe(false);
});

test('review_scorecards_open_for_whoever_holds_performance_cycle_manage', () => {
  // Controllers/Performance/ScorecardTemplatesController.cs:20-21: the list is served to performance.read, .write,
  // .approve or .cycle_manage; creating, editing and deleting (:42-43, :84-85, :117-118) are Admin, HR Manager.
  // The old Scorecards tab was performance.cycle_manage (src/lib/performanceAccess.ts), which only the HR Manager
  // bundle holds among the seeded roles (AuthSeeder.cs "performance." prefix), so the old rule stays.
  const hrManager = persona(['HR Manager'], ['performance.read', 'performance.write', 'performance.approve', 'performance.cycle_manage']);
  const lineManager = persona(['Manager'], ['performance.read', 'performance.write']);
  expect(canOpen('performanceScorecards', hrManager)).toBe(true);
  expect(canOpen('performanceScorecards', lineManager)).toBe(false);
  expect(canOpen('performanceScorecards', employee)).toBe(false);
});

test('letter_templates_open_for_Admin_and_HR_Manager_but_not_for_an_HR_Officer', () => {
  // Controllers/HrLettersController.cs:57 `TemplateAdminRoles = "Admin,HR Manager"`, applied to GET templates (:115-116),
  // seed-defaults (:139-140) and PUT (:152-153). HrRoles (adding HR Officer) is for issuing and the register only.
  // The old tab was shown to anyone who could open the page (employees.read), and answered 403 to all but those two.
  const hrManager = persona(['HR Manager'], ['employees.read', 'employees.write']);
  const hrOfficer = persona(['HR Officer'], ['employees.read', 'employees.write']);
  const lineManager = persona(['Manager'], ['employees.read']);
  expect(canOpen('letterTemplates', hrManager)).toBe(true);
  expect(canOpen('letterTemplates', hrOfficer)).toBe(false);
  expect(canOpen('letterTemplates', lineManager)).toBe(false);
  expect(canOpen('letterTemplates', employee)).toBe(false);
});

test('survey_privacy_opens_for_engagement_manage_and_not_for_a_reader_of_results', () => {
  // Controllers/Engagement/EngagementController.cs:51-52 serves the settings to engagement.read or engagement.manage,
  // but saving them (:60-61) is engagement.manage, which is also what the old Privacy settings tab asked for.
  const hrManager = persona(['HR Manager'], ['engagement.read', 'engagement.manage']);
  const resultsReader = persona(['Manager'], ['engagement.read']);
  expect(canOpen('engagementPrivacy', hrManager)).toBe(true);
  expect(canOpen('engagementPrivacy', resultsReader)).toBe(false);
  expect(canOpen('engagementPrivacy', employee)).toBe(false);
});

test('tax_policies_open_for_the_roles_the_API_serves_and_hold_payroll_read', () => {
  // Controllers/CompanyGovernanceController.cs:22: the whole controller is Admin, HR Director, Payroll Manager, Finance
  // Approver, Auditor (writes narrow to Admin, Payroll Manager at :43 and :75). A Payroll Officer holds payroll.read
  // (AuthSeeder.cs) but is not in that list, so the screen would answer 403 on load.
  const payrollManager = persona(['Payroll Manager'], ['payroll.read', 'payroll.write']);
  const auditor = persona(['Auditor'], ['payroll.read', 'compliance.read']);
  const payrollOfficer = persona(['Payroll Officer'], ['payroll.read', 'payroll.write']);
  expect(canOpen('taxPolicies', payrollManager)).toBe(true);
  expect(canOpen('taxPolicies', auditor)).toBe(true);
  expect(canOpen('taxPolicies', payrollOfficer)).toBe(false);
  expect(canOpen('taxPolicies', employee)).toBe(false);
});

test('compliance_profiles_open_for_the_roles_the_API_serves_and_hold_compliance_read', () => {
  // Controllers/CompanyGovernanceController.cs:154: Admin, HR Director, HR Manager, Compliance Officer, Auditor
  // (writes Admin, Compliance Officer at :207, :246, :284). A Payroll Manager is not in the list.
  const complianceOfficer = persona(['Compliance Officer'], ['compliance.read', 'compliance.write']);
  const hrManager = persona(['HR Manager'], ['compliance.read']);
  const payrollManager = persona(['Payroll Manager'], ['payroll.read', 'compliance.read']);
  expect(canOpen('complianceProfiles', complianceOfficer)).toBe(true);
  expect(canOpen('complianceProfiles', hrManager)).toBe(true);
  expect(canOpen('complianceProfiles', payrollManager)).toBe(false);
  expect(canOpen('complianceProfiles', employee)).toBe(false);
});

test('saudi_compliance_settings_open_with_qiwa_read_and_not_with_compliance_read_alone', () => {
  // The screen opens on Qiwa: GET /api/qiwa/connection and readiness-summary need qiwa.read (QiwaController.cs:132-135,
  // :286-289); saving is qiwa.configure (:193-196). GOSI and WPS save employer IDs on the company, whose list is Admin,
  // HR Manager, HR Officer, Auditor (CompaniesController.cs:18,49) and whose update is Admin, HR Manager (:105-106).
  // The old Configure tab opened for compliance.read OR qiwa.read; a Compliance Officer holds compliance.read only
  // (AuthSeeder.cs "Compliance Officer"), so it showed them a Qiwa panel that answered 403.
  const hrManager = persona(['HR Manager'], ['qiwa.read', 'compliance.read']);
  const auditor = persona(['Auditor'], ['qiwa.read', 'compliance.read']);
  const complianceOfficer = persona(['Compliance Officer'], ['compliance.read', 'compliance.write']);
  expect(canOpen('saudiCompliance', hrManager)).toBe(true);
  expect(canOpen('saudiCompliance', auditor)).toBe(true);
  expect(canOpen('saudiCompliance', complianceOfficer)).toBe(false);
  expect(canOpen('saudiCompliance', employee)).toBe(false);
});

test('a_section_of_a_module_that_is_switched_off_is_not_offered', () => {
  // Their module pages are behind feature switches (recruitment, performance, engagement); Setup checks the same
  // switch because a section under /setup no longer passes through the module's route gate.
  const hrManagerAll = ['recruitment.write', 'performance.cycle_manage', 'engagement.manage'];
  const off = persona(['Admin', 'HR Manager'], hrManagerAll, { isFeatureEnabled: () => false });
  for (const id of ['onboardingChecklists', 'performanceScorecards', 'engagementPrivacy']) {
    expect(canOpen(id, off), id).toBe(false);
  }
  const on = persona(['Admin', 'HR Manager'], hrManagerAll);
  for (const id of ['onboardingChecklists', 'performanceScorecards', 'engagementPrivacy']) {
    expect(canOpen(id, on), id).toBe(true);
  }
});
