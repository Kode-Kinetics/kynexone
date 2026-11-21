import { SETUP_MODULE_SECTIONS } from './setupModuleSections';

const WAITING_FOR_ACTIVATION = 'The request will be available after the employee has eligible Self-Service access. HR must complete activation requirements first.';
const NEEDS_SIGN_IN = 'This employee has not set up their Self-Service sign-in yet. You can send this request once they have used their sign-in code.';

/** Why the "request employee details" handoff is not available yet. Follows the real record: an active or invited employee is not waiting for activation, they lack a sign-in. */
export function completionUnavailableKey(employeeStatus?: string | null): string {
  return employeeStatus === 'Active' || employeeStatus === 'Invited' ? NEEDS_SIGN_IN : WAITING_FOR_ACTIVATION;
}

/** Who may open Setup > Onboarding checklist templates: the same permission and roles the Setup section itself asks for. */
export function canOpenOnboardingTemplateSetup(access: { hasPermission: (p: string) => boolean; hasRole: (r: string) => boolean }): boolean {
  const section = SETUP_MODULE_SECTIONS.find(s => s.id === 'onboardingChecklistTemplates');
  if (!section) return false;
  return section.permissions.some(access.hasPermission) && (!section.roles?.length || section.roles.some(access.hasRole));
}

export const ONBOARDING_TEMPLATES_SETUP_HREF = '/setup?tab=onboardingChecklistTemplates';

/** The API says what "give sign-in code" will do for this reader: email the code, or hand back a slip to print. Absent = an older API. */
export type SignInCodeDelivery = 'Email' | 'PrintedSlip';

/** The button's label. Known delivery names exactly what will happen; unknown keeps the earlier wording. */
export function signInCodeButtonKey(delivery: SignInCodeDelivery | null | undefined, companyEmails: boolean): string {
  if (delivery === 'Email') return 'Email sign-in code';
  if (delivery === 'PrintedSlip') return 'Issue sign-in code and print slip';
  return companyEmails ? 'Issue sign-in code' : 'Give access';
}

/** The help line under the button: only when the API does not say what will happen, so the label alone cannot. */
export function signInCodeHelpKey(delivery: SignInCodeDelivery | null | undefined, companyEmails: boolean): string | null {
  if (delivery === 'Email' || delivery === 'PrintedSlip') return null;
  return companyEmails ? 'The code is emailed to the work email. If you entered that email yourself, a slip is printed for you to hand over.' : null;
}
