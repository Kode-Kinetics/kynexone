'use client';

import { useEffect, useState } from 'react';
import { onboardingTemplatesApi, type HireTemplate } from '@/src/api/onboardingTemplates';

export interface HireTemplateChoices { templates: HireTemplate[]; suggestedId: string | null; ready: boolean }
type KeyedChoices = HireTemplateChoices & { key: string };

/**
 * The company's hire templates and the one that fits the designation, grade and branch chosen so far (server-side specificity:
 * designation > grade > branch; a tie suggests nothing). Whoever may not read templates, or a server that has none, shows no
 * picker at all: the wizard works exactly as before.
 */
export function useHireTemplate(companyId: string, designationId: string, gradeId: string, branchId: string): HireTemplateChoices {
  const key = [companyId, designationId, gradeId, branchId].join('|');
  const [state, setState] = useState<KeyedChoices>({ key: '', templates: [], suggestedId: null, ready: false });
  useEffect(() => {
    if (!companyId) { setState({ key, templates: [], suggestedId: null, ready: true }); return; }
    let live = true;
    Promise.all([
      onboardingTemplatesApi.hire.list(companyId),
      onboardingTemplatesApi.hire.resolve({ companyId, designationId: designationId || undefined, gradeId: gradeId || undefined, branchId: branchId || undefined }).catch(() => null),
    ]).then(([templates, resolved]) => {
      if (live) setState({ key, templates: templates.filter(x => x.isActive && !x.isDeleted), suggestedId: resolved?.suggestedId ?? null, ready: true });
    }).catch(() => { if (live) setState({ key, templates: [], suggestedId: null, ready: true }); });
    return () => { live = false; };
  }, [companyId, designationId, gradeId, branchId, key]);
  return state.key === key ? state : { templates: [], suggestedId: null, ready: false };
}
