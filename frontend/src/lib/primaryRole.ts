// Highest-privilege first. Employee is the baseline everyone holds, so it only shows when it is the
// only role. Roles not listed rank between the named ones and Employee.
const RANK = ['Super Admin', 'Admin', 'HR Manager', 'Payroll Manager', 'Finance Approver', 'Compliance Officer', 'Manager'];

/** The role to show beside a name: the most privileged one held, not whichever the API listed first. */
export function primaryRole(roles: readonly string[] | null | undefined): string | null {
  if (!roles?.length) return null;
  const score = (r: string) => {
    if (r.toLowerCase() === 'employee') return RANK.length + 1;
    const i = RANK.findIndex((x) => x.toLowerCase() === r.toLowerCase());
    return i === -1 ? RANK.length : i;
  };
  return [...roles].sort((a, b) => score(a) - score(b))[0];
}
