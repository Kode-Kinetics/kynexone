import { expect, test } from '@playwright/test';
import { completionUnavailableKey } from '../src/lib/handoffNotices';
import { canOpenOnboardingTemplateSetup } from '../src/lib/handoffNotices';

/** Pure rules behind the demo-readiness screen fixes, stream A. */
test.describe('employee completion: why Self-Service is not available yet', () => {
  test('an employee who is not active yet is waiting for activation requirements', () => {
    expect(completionUnavailableKey('Draft')).toBe('The request will be available after the employee has eligible Self-Service access. HR must complete activation requirements first.');
    expect(completionUnavailableKey(undefined)).toBe('The request will be available after the employee has eligible Self-Service access. HR must complete activation requirements first.');
  });
  test('an active or invited employee is never told to complete activation again: they lack a sign-in', () => {
    for (const status of ['Active', 'Invited']) {
      const key = completionUnavailableKey(status);
      expect(key).not.toContain('activation requirements');
      expect(key).toContain('sign-in');
    }
  });
});

test.describe('who is shown the link to the onboarding checklist templates', () => {
  const access = (perms: string[], roles: string[]) => ({ hasPermission: (p: string) => perms.includes(p), hasRole: (r: string) => roles.includes(r) });
  test('needs the manage permission and an Admin or HR Manager role, as the Setup section does', () => {
    expect(canOpenOnboardingTemplateSetup(access(['onboarding.templates.manage'], ['HR Manager']))).toBe(true);
    expect(canOpenOnboardingTemplateSetup(access(['onboarding.templates.manage'], ['Admin']))).toBe(true);
    expect(canOpenOnboardingTemplateSetup(access(['onboarding.templates.manage'], ['HR Officer']))).toBe(false);
    expect(canOpenOnboardingTemplateSetup(access([], ['HR Manager']))).toBe(false);
  });
});

