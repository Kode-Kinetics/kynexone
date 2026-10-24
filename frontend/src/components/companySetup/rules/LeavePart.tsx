'use client';

import { useLocale } from '../../../contexts/LocaleContext';
import { msg } from '../../../i18n/translations';
import type { CompanySetupDraftBody, CompanySetupPlan } from '../../../api/companySetup';
import type { LeaveEditorChange } from '../../../views/LeavePage';
import { useCountryName } from '../CountryOptions';
import { Notice, useWizardForm } from '../wizardForm';
import { LeaveEditors } from './LeaveEditors';
import { PartHeading, PlanLines, useRulesAccess } from './rulesShared';
import { SaveModePill } from './SaveModePill';
import styles from './motion.module.css';

/**
 * Leave & holidays: what the country pack adds at Create / Update (draft), then the real Leave policies, Holiday
 * calendars and Leave types editors, which save live through their own APIs — the same screens as Setup → Leave.
 */
export function LeavePart({ preview, previewState, onSaved }: {
  preview: CompanySetupPlan | null;
  previewState: 'idle' | 'loading' | 'error';
  onSaved?: (change: LeaveEditorChange) => void;
}) {
  const { t } = useLocale();
  const country = useCountryName();
  const { body, update, readOnly } = useWizardForm();
  const { canEditLeave } = useRulesAccess();
  const existing = !!body.existingCompanyId;
  const rules = body.rules;
  const countryName = country(body.company.countryCode) || '—';
  const setRules = (patch: Partial<CompanySetupDraftBody['rules']>) => update(b => ({ ...b, rules: { ...b.rules, ...patch } }));
  const toggle = (checked: boolean, onChange: (v: boolean) => void, label: string, field: string) => (
    <label className="flex min-h-8 items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
      <input type="checkbox" data-field={field} className="h-4 w-4 shrink-0 rounded border-slate-300" checked={checked} disabled={readOnly} onChange={e => onChange(e.target.checked)} />
      {label}
    </label>
  );

  return (
    <div className="space-y-8">
      <section aria-labelledby="rules-leave-pack">
        <PartHeading id="rules-leave-pack" title={t('From the {country} country pack', { country: countryName })}
          description={t('The statutory minimums. Added only if they do not exist yet; existing ones are never changed.')}
          aside={<SaveModePill mode={existing ? 'update' : 'create'} />} />
        <div className={styles.column}>
          <div className={styles.pair}>
            <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10" data-testid="pack-holidays">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Public holidays')}</p>
              <div className="mt-2">{toggle(rules.holidayCalendar.usePack, v => setRules({ holidayCalendar: { usePack: v } }), t('Use the {country} public holiday calendar', { country: countryName }), 'rules.holidayCalendar.usePack')}</div>
              {rules.holidayCalendar.usePack
                ? <PlanLines preview={preview} previewState={previewState} area="holidayCalendar" created={msg('Will be created')} reused={msg('Already exists — reused')} />
                : <p className="mt-1 text-xs leading-5 text-amber-800 dark:text-amber-200">{t('No holiday calendar will be added. Add one below, or later in Setup → Holiday calendars, before the first leave request.')}</p>}
            </div>
            <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10" data-testid="pack-leave">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Statutory leave policies')}</p>
              <div className="mt-2">{toggle(rules.leavePolicies.usePack, v => setRules({ leavePolicies: { usePack: v } }), t('Use the statutory leave policies for {country}', { country: countryName }), 'rules.leavePolicies.usePack')}</div>
              <p className="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-400">{t('The legal minimums cannot be lowered here. Add company extras in Leave policies below.')}</p>
              {rules.leavePolicies.usePack
                ? <PlanLines preview={preview} previewState={previewState} area="leavePolicy" created={msg('Will be created')} reused={msg('Already exists — reused')} />
                : <p className="mt-1 text-xs leading-5 text-amber-800 dark:text-amber-200">{t('No leave policies will be added. Employees cannot request leave until you add them below or in Setup → Leave policies.')}</p>}
            </div>
          </div>
        </div>
      </section>

      <section aria-labelledby="rules-leave-lists" data-testid="leave-editors">
        <PartHeading id="rules-leave-lists" title={t('Leave policies, holidays and leave types')}
          description={existing
            ? t('This company’s own leave types and policies. Holiday calendars can be this company’s or shared by the workspace.')
            : t('This company’s own leave types and policies, added after Create. Workspace-wide holiday calendars apply now.')} />
        {canEditLeave ? (
          <LeaveEditors companyId={body.existingCompanyId ?? undefined} onSaved={onSaved} />
        ) : (
          <Notice tone="info">{t('Only an Admin or HR Manager with leave access can change these lists. Ask your group super admin, or continue: the country pack above is still applied.')}</Notice>
        )}
      </section>
    </div>
  );
}
