'use client';

import { useMemo } from 'react';
import type { EmployeeCostCentrePreview, EmployeeDetail } from '../../api/employees';
import type { CostCenterDto, DepartmentDto } from '../../api/organization';
import { useCostCentrePreview, type CostCentrePreviewState } from '../../hooks/useCostCentrePreview';
import {
  costCentreOptions,
  editCostCentreNeedsReason,
  editCostCentrePatch,
  needsDecision,
  patchCostCentreCode,
  previewResolution,
  resolveCostCentre,
  type CostCentreOption,
  type EditCostCentreInput,
  type EditCostCentrePatch,
  type Resolution,
} from '../../lib/employeeCostCentre';
import type { CostCentreIntent } from './useEmployeeEditDraft';

export type EditCostCentreProblem = 'decision' | 'reason' | null;

export interface EditCostCentre {
  canManage: boolean;
  isOverride: boolean;
  /** The override's code and label (for "Keep SALES (override)"). */
  overrideCode: string;
  /** Where the saved record's cost centre comes from (the source line). */
  current: Resolution;
  currentCode: string;
  currentLabel: string;
  departmentChanged: boolean;
  /** The department in the form, in the reader's language. */
  departmentLabel: string;
  /** The server's answer for the department in the form (only while it differs from the saved one). */
  departmentPreview: CostCentrePreviewState;
  retryDepartmentPreview: () => void;
  /** What following the department gives (the follow option and the drop radio). Null while unknown. */
  follow: { id: string | null; code: string; name: string } | null;
  options: CostCentreOption[];
  selectValue: string;
  extra: { value: string; label: string } | null;
  needsDecision: boolean;
  needsReason: boolean;
  problem: EditCostCentreProblem;
  patch: EditCostCentrePatch;
  /** The review row's value when it differs from the plain snapshot; null = the snapshot is right. */
  review: { kind: 'pick' | 'keep' | 'follow' | 'department'; from: string; to: string; department: string; reason: string } | null;
}

const nameIn = (locale: string, en: string | null | undefined, ar: string | null | undefined) => (locale === 'ar' && ar ? ar : en ?? '');

/**
 * Everything the edit flow's cost-centre block needs, from the server: the saved resolution (employee detail), what the person
 * inherits today (the no-parameter preview, which ignores an override), and — while the department in the form differs from
 * the saved one — what they would inherit there (the preview with that departmentId). Nothing here guesses a cost centre from
 * the lists except the source name while the plain preview is on its way.
 */
export function useEditCostCentre({ employee, departments, costCentres, departmentValue, originalDepartmentValue, canManage, canWrite, intent, locale }: {
  employee: EmployeeDetail;
  /** Already limited to the employee's company (or company-less). */
  departments: DepartmentDto[];
  /** The company's cost centres (active and inactive). */
  costCentres: CostCenterDto[];
  departmentValue: string;
  originalDepartmentValue: string;
  canManage: boolean;
  canWrite: boolean;
  intent: CostCentreIntent;
  locale: string;
}): EditCostCentre {
  const resolution = employee.costCentreResolution ?? null;
  const isOverride = typeof employee.costCenterIsOverride === 'boolean' ? employee.costCenterIsOverride : resolution?.source === 'Employee';
  const overrideId = isOverride ? employee.costCenterId ?? resolution?.costCenterId ?? null : null;

  const selectedDepartment = departments.find((d) => d.nameEn === departmentValue || d.code === departmentValue) ?? null;
  const departmentChanged = departmentValue !== originalDepartmentValue;
  const departmentLabel = selectedDepartment ? nameIn(locale, selectedDepartment.nameEn, selectedDepartment.nameAr) : departmentValue;

  // The plain preview needs only employees.read; a hypothetical department needs employees.write and is only asked by a
  // reader who may move the person (organization.costcenter.manage), the only one who can change the department here.
  const plain = useCostCentrePreview(employee.id, {});
  const moved = useCostCentrePreview(employee.id, canWrite && canManage && departmentChanged && selectedDepartment ? { departmentId: selectedDepartment.id } : null);
  const plainData: EmployeeCostCentrePreview | null = plain.state.status === 'ready' ? plain.state.data : null;
  const movedData: EmployeeCostCentrePreview | null = moved.state.status === 'ready' ? moved.state.data : null;

  return useMemo(() => {
    const byId = (id: string | null | undefined) => (id ? costCentres.find((c) => c.id === id) ?? null : null);
    const ccLabel = (code: string | null | undefined, name: string) => (code ? (name ? `${code} — ${name}` : code) : '');

    // The saved record: the server's resolution, with the source name from the plain preview (or the lists while it loads).
    const fallback = resolveCostCentre({
      companyId: employee.companyId,
      employee: { costCenterId: employee.costCenterId ?? null, departmentId: employee.departmentId ?? null, positionId: employee.positionId ?? null, costCenterIsOverride: employee.costCenterIsOverride ?? null, costCentreResolution: resolution },
      departments: departments.map((d) => ({ id: d.id, nameEn: nameIn(locale, d.nameEn, d.nameAr), parentDepartmentId: d.parentDepartmentId ?? null, costCenterId: d.costCenterId ?? null })),
      positions: [],
      costCentres,
      companyDefaultId: undefined,
    });
    const current: Resolution = !isOverride && plainData ? previewResolution(plainData, locale) : fallback;
    const currentCode = resolution?.code ?? byId(current.costCentreId)?.code ?? '';
    const currentLabel = ccLabel(currentCode, resolution ? nameIn(locale, resolution.name, resolution.nameAr) : nameIn(locale, byId(current.costCentreId)?.name, byId(current.costCentreId)?.nameAr));
    const overrideCc = byId(overrideId);
    const overrideCode = overrideCc?.code ?? (isOverride ? resolution?.code ?? '' : '');

    // What following the department gives: the department in the form when it changed, today's inheritance otherwise.
    // Null while unknown (the preview is on its way or failed): the screen then says "Follow department" without a code.
    const followDto = departmentChanged ? movedData : plainData;
    const follow = followDto
      ? { id: followDto.invalidReason ? null : followDto.costCentreId, code: followDto.invalidReason ? '' : followDto.code ?? '', name: followDto.invalidReason ? '' : nameIn(locale, followDto.nameEn, followDto.nameAr) }
      : !departmentChanged && !isOverride && resolution
        ? { id: resolution.costCenterId ?? null, code: resolution.code ?? '', name: nameIn(locale, resolution.name, resolution.nameAr) }
        : null;

    const input: EditCostCentreInput = {
      canManage,
      isOverride,
      overrideId,
      inheritedId: follow?.id ?? null,
      choice: intent.choice,
      departmentChanged,
      decision: intent.decision,
      reason: intent.reason,
      codeOf: (id) => byId(id)?.code ?? '',
    };
    const decisionDue = needsDecision(input);
    const reasonDue = editCostCentreNeedsReason(input);
    const patch = editCostCentrePatch(input);
    const problem: EditCostCentreProblem = decisionDue && !intent.decision ? 'decision' : reasonDue && !intent.reason.trim() ? 'reason' : null;

    const options = costCentreOptions(costCentres, employee.companyId, locale, follow ?? { id: null, code: '', name: '' });
    const selectValue = intent.choice ?? (isOverride ? overrideId ?? '' : '');
    const extra = isOverride && overrideId ? { value: overrideId, label: ccLabel(overrideCode, overrideCc ? nameIn(locale, overrideCc.name, overrideCc.nameAr) : '') || overrideCode } : null;

    const reason = intent.reason.trim();
    const departmentForReview = departmentChanged ? departmentLabel : nameIn(locale, employee.department, null);
    const sentCode = patchCostCentreCode(patch);
    const review: EditCostCentre['review'] = sentCode
      ? { kind: 'pick', from: currentCode, to: sentCode, department: departmentForReview, reason }
      : sentCode === ''
        ? { kind: 'follow', from: currentCode, to: follow?.code ?? '', department: departmentForReview, reason }
        : patch.costCenterDecision === 'keep'
          ? { kind: 'keep', from: currentCode, to: overrideCode, department: departmentForReview, reason }
          : patch.costCenterDecision === 'drop'
            ? { kind: 'follow', from: currentCode, to: follow?.code ?? '', department: departmentForReview, reason }
            : departmentChanged && !isOverride && canManage && movedData
              ? { kind: 'department', from: currentCode, to: follow?.code ?? '', department: departmentForReview, reason: '' }
              : null;

    return {
      canManage, isOverride, overrideCode, current, currentCode, currentLabel, departmentChanged, departmentLabel,
      departmentPreview: moved.state, retryDepartmentPreview: moved.retry, follow, options, selectValue, extra,
      needsDecision: decisionDue, needsReason: reasonDue, problem, patch, review,
    };
  }, [canManage, costCentres, departmentChanged, departmentLabel, departments, employee, intent, isOverride, locale, moved.retry, moved.state, movedData, overrideId, plainData, resolution]);
}
