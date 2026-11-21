/**
 * Who sees what in Recruitment. HR (anyone with recruitment.read or recruitment.write) gets the full workspace. A line manager holds only two
 * narrow keys, so they get a small screen of their own: raise a requisition for their own department, and give feedback on the interviews they
 * are on. The menu, the page and the API agree because all three read the same keys (the API gates by the same keys in
 * LegacyRolePermissionResolver.NarrowRecruitmentActions).
 */
export const RECRUITMENT_NARROW_KEYS = ['recruitment.requisition.raise', 'recruitment.interview.feedback'] as const;
export const RECRUITMENT_BROAD_KEYS = ['recruitment.read', 'recruitment.write'] as const;

export interface RecruitmentAccess {
  /** HR: the whole workspace. */
  broad: boolean;
  canRaiseRequisition: boolean;
  canGiveFeedback: boolean;
}

export function recruitmentAccess(has: (permission: string) => boolean): RecruitmentAccess {
  return {
    broad: RECRUITMENT_BROAD_KEYS.some(has),
    canRaiseRequisition: has('recruitment.requisition.raise'),
    canGiveFeedback: has('recruitment.interview.feedback'),
  };
}

/** True when the person gets the manager screen instead of the HR workspace. */
export function isManagerRecruitmentUser(access: RecruitmentAccess): boolean {
  return !access.broad && (access.canRaiseRequisition || access.canGiveFeedback);
}
