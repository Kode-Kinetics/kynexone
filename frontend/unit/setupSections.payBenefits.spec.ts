import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleAccess } from '../src/lib/setupModuleSections';

/**
 * Pay & benefits sections in Setup: who may open each one. A section opens for what its old module tab opened for,
 * narrowed to what the API serves for READ, so nobody is shown a screen that answers 403 on load. Every persona names
 * the backend line that decides it (paths under backend-dotnet/Zayra.Api/Controllers).
 */

const persona = (roles: string[], permissions: string[]): SetupModuleAccess => ({
  hasPermission: key => permissions.includes(key),
  hasRole: role => roles.includes(role),
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
});
const opens = (id: string, access: SetupModuleAccess) => {
  const section = SETUP_MODULE_SECTIONS.find(item => item.id === id);
  expect(section, `Missing Setup section ${id}`).toBeDefined();
  return moduleSectionAvailable(section!, access);
};

test('benefit_plans_open_for_the_roles_the_plan_and_enrolment_lists_serve', () => {
  // BenefitsController.cs:204-205 (GET plans) and :538-540 (GET enrollments, which the panel counts from):
  // Roles = Admin, HR Manager, HR Officer, Finance, Auditor; and enrollments also employees.write / payroll.read / finance.gl.read.
  expect(opens('benefitPlans', persona(['HR Officer'], ['employees.write']))).toBe(true);
  expect(opens('benefitPlans', persona(['HR Manager'], ['employees.write']))).toBe(true);
  // HR Director holds employees.write (the old page gate) but is not in either list's Roles: both calls answer 403.
  expect(opens('benefitPlans', persona(['HR Director'], ['employees.write']))).toBe(false);
  // The old page gate was employees.write.
  expect(opens('benefitPlans', persona(['Finance'], ['payroll.read']))).toBe(false);
});

test('loan_types_open_for_the_finance_and_hr_roles_that_see_the_loans_module_tabs', () => {
  // LoansController.cs:18 [Authorize] and :40 (GET types) carry no role or permission: the list never refuses.
  // The tab stays with the roles that saw it (LoansPage canViewFinanceModules); writes need loans.write (:48).
  expect(opens('loanTypes', persona(['Finance'], ['loans.read']))).toBe(true);
  // A line manager reads team loans only: the module never showed them this tab.
  expect(opens('loanTypes', persona(['Manager'], ['loans.read']))).toBe(false);
});

test('loan_policies_open_for_the_hr_policy_owners_only', () => {
  // LoansController.Policies.cs:15 IsHrLoanActor() (Admin, HR Manager, HR Director) and :26-30 (create policy), and
  // LoansController.GradeLimits.cs:26 ("Finance and Finance Approver hold neither key"). Reads: Policies.cs:16, GradeLimits.cs:31 (loans.read).
  expect(opens('loanPolicies', persona(['HR Director'], ['loans.read']))).toBe(true);
  expect(opens('loanPolicies', persona(['Finance'], ['loans.read', 'loans.write']))).toBe(false);
});

test('the_advance_policy_opens_for_the_roles_that_saw_the_tab', () => {
  // AdvancesController.cs:41 GET policy is open to any signed-in user; POST :50-51 needs loans.write.
  expect(opens('advancePolicy', persona(['Finance Approver'], ['loans.read']))).toBe(true);
  // HR Officer never saw the module's configuration tabs, and an employee reads only their own loans (loans.self).
  expect(opens('advancePolicy', persona(['HR Officer'], ['loans.read']))).toBe(false);
  expect(opens('advancePolicy', persona(['Employee'], ['loans.self']))).toBe(false);
});

test('bonus_types_open_for_the_roles_that_saw_the_tab', () => {
  // BonusesController.cs:68 GET types is open to any signed-in user; create, edit and delete need payroll.write (:77-78, :108-109, :138-139).
  expect(opens('bonusTypes', persona(['HR Manager'], ['loans.write']))).toBe(true);
  expect(opens('bonusTypes', persona(['Manager'], ['loans.read']))).toBe(false);
});

test('payslip_templates_open_for_the_roles_the_template_list_serves', () => {
  // PayslipTemplatesController.cs:49-50 (GET list), :67-68 (GET one), :80-81 (versions):
  // Roles = Admin, HR Manager, Payroll Manager, Payroll Officer. There is no permission on them; payroll.read was the old page's only rule.
  expect(opens('payslipTemplates', persona(['Payroll Officer'], ['payroll.read']))).toBe(true);
  expect(opens('payslipTemplates', persona(['HR Manager'], ['payroll.read']))).toBe(true);
  // Finance holds payroll.read but is not in the Roles: the list answers 403 and the page showed an empty table.
  expect(opens('payslipTemplates', persona(['Finance'], ['payroll.read']))).toBe(false);
  expect(opens('payslipTemplates', persona(['Payroll Officer'], []))).toBe(false);
});
