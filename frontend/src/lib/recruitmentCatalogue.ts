/**
 * Requisition, opening and offer forms pick their department, designation and grade from the organisation's own
 * records. They used to be typed, and a name no record matched surfaced only when HR tried to activate the hire.
 *
 * The API is the authority (RequisitionsController, OpeningsController, OfferRules): these helpers only build the
 * request from the choice and carry a requisition's terms into the opening, so the form and the API cannot disagree.
 */

export interface CatalogueOption { id: string; name: string; code: string }

export interface GradeOption extends CatalogueOption {
  /** Null/absent when the caller may not see pay bands (a recruiter), or the band is not set. */
  minSalary?: number | null;
  midSalary?: number | null;
  maxSalary?: number | null;
  currency?: string | null;
}

export interface CatalogueOptions {
  departments: CatalogueOption[];
  designations: CatalogueOption[];
  grades: GradeOption[];
}

export interface RequisitionFormState {
  departmentId: string;
  designationId: string;
  gradeId: string;
  headCount: number;
  employmentType: string;
  priority: string;
  justification: string;
  requiredSkills: string;
  minExperienceYears: string;
  maxExperienceYears: string;
  budgetFrom: string;
  budgetTo: string;
  targetJoiningDate: string;
  requestedByName: string;
}

const nameOf = (list: CatalogueOption[], id: string) => list.find((x) => x.id === id)?.name ?? '';
const number = (v: string) => (v.trim() === '' ? undefined : Number(v));

export function requisitionRequestBody(form: RequisitionFormState, options: CatalogueOptions) {
  return {
    departmentId: form.departmentId || undefined,
    departmentName: nameOf(options.departments, form.departmentId),
    designationId: form.designationId || undefined,
    designationTitle: nameOf(options.designations, form.designationId),
    gradeId: form.gradeId || undefined,
    headCount: form.headCount,
    employmentType: form.employmentType,
    priority: form.priority,
    justification: form.justification,
    requiredSkills: form.requiredSkills,
    minExperienceYears: number(form.minExperienceYears),
    maxExperienceYears: number(form.maxExperienceYears),
    budgetFrom: number(form.budgetFrom),
    budgetTo: number(form.budgetTo),
    targetJoiningDate: form.targetJoiningDate || undefined,
    requestedByName: form.requestedByName,
  };
}

export interface RequisitionTerms {
  id: string;
  departmentId?: string | null;
  departmentName: string;
  designationId?: string | null;
  designationTitle: string;
  gradeId?: string | null;
  headCount: number;
  employmentType: string;
  requiredSkills: string;
  budgetFrom: number | null;
  budgetTo: number | null;
}

export interface OpeningFormDefaults {
  title: string;
  departmentId: string;
  designationId: string;
  gradeId: string;
  employmentType: string;
  headCount: number;
  requirements: string;
  salaryFrom: string;
  salaryTo: string;
}

/** What an opening starts with: the requisition's placement, headcount, budget range (as the salary range) and skills (as the requirements). */
export function openingFormDefaults(requisition: RequisitionTerms | null): OpeningFormDefaults {
  return {
    title: requisition?.designationTitle ?? '',
    departmentId: requisition?.departmentId ?? '',
    designationId: requisition?.designationId ?? '',
    gradeId: requisition?.gradeId ?? '',
    employmentType: requisition?.employmentType ?? 'Full-Time',
    headCount: requisition?.headCount ?? 1,
    requirements: requisition?.requiredSkills ?? '',
    salaryFrom: requisition?.budgetFrom != null ? String(requisition.budgetFrom) : '',
    salaryTo: requisition?.budgetTo != null ? String(requisition.budgetTo) : '',
  };
}

export interface BandWarning { side: 'below' | 'above'; limit: number; currency: string; gradeName: string }

/**
 * A heads-up when the offered basic is outside the grade's salary range. The range is a guideline (owner decision
 * 2026-11-08), so this warns and never blocks; the API repeats the check and answers with its own warning.
 */
export function offerBandWarning(basicSalary: number, grade: GradeOption | null): BandWarning | null {
  if (!grade || !(basicSalary > 0)) return null;
  const min = grade.minSalary ?? 0;
  const max = grade.maxSalary ?? 0;
  const currency = grade.currency ?? '';
  if (min > 0 && basicSalary < min) return { side: 'below', limit: min, currency, gradeName: grade.name };
  if (max > 0 && basicSalary > max) return { side: 'above', limit: max, currency, gradeName: grade.name };
  return null;
}
