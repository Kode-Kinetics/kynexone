interface DesignationLike { departmentId?: string | null; companyId?: string | null }

/**
 * The designations the employee edit form offers. A designation is owned by a company and only optionally tied to a
 * department, so the list is the employee's company's designations that belong to the chosen department OR to no
 * department at all (company-wide, which is how Setup creates most of them). Filtering strictly by department hid every
 * company-wide designation and left one choice. A legacy row with no company is kept so it is not silently dropped.
 */
export function designationChoices<T extends DesignationLike>(
  designations: T[],
  scope: { companyId?: string | null; departmentId?: string | null },
): T[] {
  return designations.filter((item) => {
    if (scope.companyId && item.companyId && item.companyId !== scope.companyId) return false;
    if (scope.departmentId && item.departmentId && item.departmentId !== scope.departmentId) return false;
    return true;
  });
}
