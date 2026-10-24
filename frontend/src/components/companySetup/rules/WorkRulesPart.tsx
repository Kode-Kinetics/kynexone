'use client';

import { useLocale } from '../../../contexts/LocaleContext';
import type { CompanySetupDraftBody, EmployeeNumbering } from '../../../api/companySetup';
import { WEEKEND_OPTIONS, chooseWeekend, numberingPreview } from '../../../lib/companySetupWizard';
import { WizardField, useWizardForm } from '../wizardForm';
import { PartHeading } from './rulesShared';

/**
 * Working week and employee numbering. Draft data: applied together at Create / Update, never saved live
 * (Addendum B4), so they stay part of the one reviewed commit.
 */
export function WorkRulesPart({ defaults }: { defaults: CompanySetupDraftBody | null }) {
  const { t } = useLocale();
  const { body, update, readOnly, baseline } = useWizardForm();
  // Existing mode: the counter only goes up (server: sequence_cannot_go_back).
  const sequenceFloor = body.existingCompanyId ? baseline?.rules.employeeNumbering.nextSequence : undefined;
  const rules = body.rules;
  const packWeekend = defaults?.rules.workWeek.weekendDays ?? rules.workWeek.weekendDays;
  const setRules = (patch: Partial<CompanySetupDraftBody['rules']>) => update(b => ({ ...b, rules: { ...b.rules, ...patch } }));
  const setNumbering = (patch: Partial<EmployeeNumbering>) => setRules({ employeeNumbering: { ...rules.employeeNumbering, ...patch } });
  const weekendLabel = (value: string) => t(WEEKEND_OPTIONS.find(o => o.value === value)?.label ?? value);
  const toggle = (checked: boolean, onChange: (v: boolean) => void, label: string, field: string) => (
    <label className="flex min-h-8 items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
      <input type="checkbox" data-field={field} className="h-4 w-4 rounded border-slate-300" checked={checked} disabled={readOnly} onChange={e => onChange(e.target.checked)} />
      {label}
    </label>
  );

  return (
    <div className="space-y-8">
      <section>
        <PartHeading title={t('Working week')} description={t('The weekend decides how leave days and overtime hours are counted.')} />
        <WizardField field="rules.workWeek.weekendDays" label={t('Weekend (days off)')} help={t('Country default: {days}.', { days: weekendLabel(packWeekend) })} className="max-w-sm">
          {control => <select {...control} className="select w-full" disabled={readOnly} value={rules.workWeek.weekendDays}
            onChange={e => setRules({ workWeek: chooseWeekend(rules.workWeek, e.target.value, packWeekend) })}>
            {WEEKEND_OPTIONS.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}
          </select>}
        </WizardField>
        <p className="mt-2 text-xs text-slate-600 dark:text-slate-400" data-testid="work-week-mode">{rules.workWeek.override ? t('This company will use its own working week.') : t('This company follows the country working week.')}</p>
      </section>

      <section>
        <PartHeading title={t('Employee numbering')} description={t('How employee numbers are made for this company.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-3">
          <WizardField field="rules.employeeNumbering.prefix" label={t('Prefix')}>
            {control => <input {...control} className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.prefix} onChange={e => setNumbering({ prefix: e.target.value.toUpperCase() })} />}
          </WizardField>
          <WizardField field="rules.employeeNumbering.paddingLength" label={t('Digits in the number')}>
            {control => <input {...control} type="number" min={1} max={10} inputMode="numeric" className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.paddingLength} onChange={e => setNumbering({ paddingLength: Number(e.target.value) })} />}
          </WizardField>
          <WizardField field="rules.employeeNumbering.nextSequence" label={t('Next number')} help={sequenceFloor ? t('Already at {number}; it can only go up.', { number: sequenceFloor }) : undefined}>
            {control => <input {...control} type="number" min={sequenceFloor ?? 1} inputMode="numeric" className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.nextSequence} onChange={e => setNumbering({ nextSequence: Number(e.target.value) })} />}
          </WizardField>
        </div>
        <div className="mt-3 flex flex-wrap gap-x-5 gap-y-1">
          {toggle(rules.employeeNumbering.useCountryPrefix, v => setNumbering({ useCountryPrefix: v }), t('Add the country code'), 'rules.employeeNumbering.useCountryPrefix')}
          {toggle(rules.employeeNumbering.useBranchPrefix, v => setNumbering({ useBranchPrefix: v }), t('Add the branch code'), 'rules.employeeNumbering.useBranchPrefix')}
          {toggle(rules.employeeNumbering.useDepartmentPrefix, v => setNumbering({ useDepartmentPrefix: v }), t('Add the department code'), 'rules.employeeNumbering.useDepartmentPrefix')}
          {toggle(rules.employeeNumbering.useYear, v => setNumbering({ useYear: v }), t('Add the joining year'), 'rules.employeeNumbering.useYear')}
        </div>
        <p className="mt-3 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-800 dark:bg-white/[0.04] dark:text-slate-200" data-testid="numbering-preview">{t('The next employee number will look like {example}.', { example: numberingPreview(body) })}</p>
        {body.existingCompanyId && <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t('Existing employee numbers stay as they are; the rule applies to people added from now on.')}</p>}
      </section>
    </div>
  );
}
