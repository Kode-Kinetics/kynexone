import { test, expect } from '@playwright/test';
import { isManagerRecruitmentUser, recruitmentAccess } from '../src/lib/recruitmentAccess';
import { navigationItems } from '../src/routes/navigation';

const holding = (...keys: string[]) => (permission: string) => keys.includes(permission);

test.describe('who gets which Recruitment screen', () => {
  test('HR with recruitment read or write gets the full workspace', () => {
    for (const keys of [['recruitment.read'], ['recruitment.write'], ['recruitment.read', 'recruitment.write', 'recruitment.approve']]) {
      const access = recruitmentAccess(holding(...keys));
      expect(access.broad).toBe(true);
      expect(isManagerRecruitmentUser(access)).toBe(false);
    }
  });

  test('a line manager with only the two narrow keys gets the manager screen', () => {
    const access = recruitmentAccess(holding('recruitment.requisition.raise', 'recruitment.interview.feedback'));
    expect(access).toEqual({ broad: false, canRaiseRequisition: true, canGiveFeedback: true });
    expect(isManagerRecruitmentUser(access)).toBe(true);
  });

  test('either narrow key alone is enough, and a person with neither is not a manager-screen user', () => {
    expect(isManagerRecruitmentUser(recruitmentAccess(holding('recruitment.requisition.raise')))).toBe(true);
    expect(isManagerRecruitmentUser(recruitmentAccess(holding('recruitment.interview.feedback')))).toBe(true);
    expect(isManagerRecruitmentUser(recruitmentAccess(holding('employees.read')))).toBe(false);
  });

  test('the menu opens Recruitment for the narrow keys too, the same keys the API gates on', () => {
    const recruitment = navigationItems.find(item => item.path === '/recruitment');
    expect(recruitment?.requiredPermissions).toEqual(expect.arrayContaining([
      'recruitment.read', 'recruitment.write', 'recruitment.requisition.raise', 'recruitment.interview.feedback',
    ]));
  });
});
