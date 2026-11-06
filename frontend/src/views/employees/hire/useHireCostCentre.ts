'use client';

import { useMemo } from 'react';
import type { DepartmentDto } from '@/src/api/organization';
import { useCompanyCostCentres } from '@/src/hooks/useCompanyCostCentres';
import { useCostCentrePreview } from '@/src/hooks/useCostCentrePreview';
import { costCentreOptions, resolveCostCentre, type CostCentreOption } from '@/src/lib/employeeCostCentre';
import type { HireForm } from '@/src/lib/hire/hireForm';

export interface HireCostCentre {
  /** organization.costcenter.manage: only then is the dropdown offered and anything sent. */
  canManage: boolean;
  options: CostCentreOption[];
  /** What following the department gives is not known (no department yet, or the answer is on its way). */
  followPending: boolean;
  /** The choice in words, for Review and the read-only view: "CODE — name" or "Follow department · CODE — name". */
  label: string;
  departmentCostCentreId: string;
  /** A resumed draft already holds an override, so following again must send the explicit None. */
  hadOverride: boolean;
  loadFailed: boolean;
  reload: () => void;
}

/**
 * The hire wizard's cost centre (Wave A, D5), the create form's rules: the employing company's cost centres only, "Follow
 * department" first. Before the draft exists the follow option is worked out from the lists (accepted deviation: no
 * company-level preview); once the draft has an id the server's preview answers for the chosen department.
 */
export function useHireCostCentre({ form, departments, draftId, hadOverride, canManage, locale, t }: {
  form: HireForm;
  departments: DepartmentDto[];
  draftId: number | null;
  hadOverride: boolean;
  canManage: boolean;
  locale: string;
  t: (key: string, params?: Record<string, string | number>) => string;
}): HireCostCentre {
  const company = useCompanyCostCentres(form.companyId);
  const preview = useCostCentrePreview(draftId ?? 0, draftId && canManage && form.departmentId ? { departmentId: form.departmentId } : null);
  const department = departments.find((d) => d.id === form.departmentId);
  const departmentCostCentreId = department?.costCenterId ?? '';

  return useMemo(() => {
    const list = company.list;
    const nameOf = (c: { name: string; nameAr?: string | null }) => (locale === 'ar' && c.nameAr ? c.nameAr : c.name);
    let follow: { id: string | null; code: string; name: string } | null = null;
    if (preview.state.status === 'ready' && !preview.state.data.invalidReason) {
      const d = preview.state.data;
      follow = d.costCentreId && d.code ? { id: d.costCentreId, code: d.code, name: (locale === 'ar' && d.nameAr ? d.nameAr : d.nameEn) ?? '' } : null;
    } else if (form.companyId && form.departmentId) {
      const r = resolveCostCentre({
        companyId: form.companyId,
        employee: { costCenterId: null, departmentId: form.departmentId, positionId: null, costCenterIsOverride: false },
        departments: departments.map((d) => ({ id: d.id, nameEn: d.nameEn, parentDepartmentId: d.parentDepartmentId ?? null, costCenterId: d.costCenterId ?? null })),
        positions: [],
        costCentres: list,
        companyDefaultId: undefined,
      });
      const hit = r.costCentreId ? list.find((c) => c.id === r.costCentreId) : null;
      follow = hit ? { id: hit.id, code: hit.code, name: nameOf(hit) } : null;
    }
    const options = costCentreOptions(list, form.companyId, locale, follow ?? { id: null, code: '', name: '' });
    const chosen = form.costCenterId ? options.find((o) => o.value === form.costCenterId) : null;
    const label = chosen ? chosen.label : follow ? t('Follow department · {code}', { code: `${follow.code} — ${follow.name}` }) : t('Follow department');
    return { canManage, options, followPending: !follow, label, departmentCostCentreId, hadOverride, loadFailed: company.status === 'error', reload: company.reload };
  }, [canManage, company.list, company.reload, company.status, departmentCostCentreId, departments, form.companyId, form.costCenterId, form.departmentId, hadOverride, locale, preview.state, t]);
}
