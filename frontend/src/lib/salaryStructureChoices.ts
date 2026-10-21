/**
 * Which salary structures an employee's salary may be put on.
 *
 * The Add/Edit Employee forms used to ask for a free-text "salary structure reference"; whatever was typed
 * became a brand-new structure. They now offer a choice of the ACTIVE structures that belong to the employee's
 * company or are group-wide (no company), exactly the set the server will accept. The server refuses any other
 * code (422 unknown_salary_structure), so this list is a convenience, not the guard.
 *
 * The code the employee is already on is always kept as a choice — even when it has since been superseded or
 * deactivated — so opening the edit form never silently changes it.
 */
export interface SalaryStructureLike {
  code: string;
  name: string;
  companyId: string | null;
  isActive: boolean;
}

export interface SalaryStructureChoice {
  /** The code written to salaryBreakdown.salaryStructureCode. */
  value: string;
  name: string;
  /** True when the structure is group-wide rather than the company's own. */
  groupWide: boolean;
  /** True for the employee's current code when it is no longer an active choice. */
  retainedCurrent: boolean;
}

export function salaryStructureChoices(
  structures: readonly SalaryStructureLike[],
  companyId: string | null | undefined,
  currentCode?: string | null,
): SalaryStructureChoice[] {
  const byCode = new Map<string, SalaryStructureChoice>();
  // Company-specific first, so a company's own structure wins over a group-wide one with the same code
  // (the server resolves the same way).
  const eligible = structures
    .filter((s) => s.isActive && s.code.trim() && (s.companyId == null || (!!companyId && s.companyId === companyId)))
    .sort((a, b) => Number(a.companyId == null) - Number(b.companyId == null));
  for (const s of eligible) {
    const value = s.code.trim();
    if (!byCode.has(value)) byCode.set(value, { value, name: s.name.trim() || value, groupWide: s.companyId == null, retainedCurrent: false });
  }
  const choices = [...byCode.values()].sort((a, b) => a.name.localeCompare(b.name) || a.value.localeCompare(b.value));
  const current = currentCode?.trim();
  if (current && !byCode.has(current)) {
    const known = structures.find((s) => s.code.trim() === current);
    choices.unshift({ value: current, name: known?.name.trim() || current, groupWide: known ? known.companyId == null : false, retainedCurrent: true });
  }
  return choices;
}
