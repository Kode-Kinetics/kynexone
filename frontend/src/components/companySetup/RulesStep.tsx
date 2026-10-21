'use client';

import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';
import type { CompanySetupDraftBody, CompanySetupPlan, EmployeeNumbering, PlanArea } from '../../api/companySetup';
import { WEEKEND_OPTIONS, numberingPreview } from '../../lib/companySetupWizard';
import { useCountryName } from './CountryOptions';
import { SectionHeader, WizardField, useWizardForm } from './wizardForm';

/**
 * Step 5. Rules come from the country pack; the screen shows what the pack resolves to and what the
 * commit will do with it (from the same planner the review uses), and asks only about what differs.
 */
export function RulesStep({ defaults, preview, previewState }: {
  defaults: CompanySetupDraftBody | null;
  preview: CompanySetupPlan | null;
  previewState: 'idle' | 'loading' | 'error';
}) {
  const t = useT();
  const country = useCountryName();
  const { body, update, readOnly } = useWizardForm();
  const rules = body.rules;
  const packWeekend = defaults?.rules.workWeek.weekendDays ?? rules.workWeek.weekendDays;
  const setRules = (patch: Partial<CompanySetupDraftBody['rules']>) => update(b => ({ ...b, rules: { ...b.rules, ...patch } }));
  const setNumbering = (patch: Partial<EmployeeNumbering>) => setRules({ employeeNumbering: { ...rules.employeeNumbering, ...patch } });
  const weekendLabel = (value: string) => t(WEEKEND_OPTIONS.find(o => o.value === value)?.label ?? value);
  const countryName = country(body.company.countryCode);
  const items = (area: PlanArea) => preview?.items.filter(i => i.area === area) ?? [];
  const planLine = (area: PlanArea, created: string, reused: string) => {
    if (previewState === 'loading') return <p className="text-xs text-slate-500">{t('Checking what already exists…')}</p>;
    const rows = items(area);
    if (previewState === 'error' || rows.length === 0) return <p className="text-xs text-slate-500">{t('Checked again on the review step.')}</p>;
    return (
      <ul className="mt-1 space-y-0.5">
        {rows.map(i => <li key={`${i.area}-${i.code}`} className="text-xs text-slate-600 dark:text-slate-400"><bdi>{i.label}</bdi>: {i.action === 'reuse' ? t(reused) : i.action === 'create' ? t(created) : t('Conflict — see the review step')}</li>)}
      </ul>
    );
  };
  const toggle = (checked: boolean, onChange: (v: boolean) => void, label: string, field: string) => (
    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
      <input type="checkbox" data-field={field} className="h-4 w-4 rounded border-slate-300" checked={checked} disabled={readOnly} onChange={e => onChange(e.target.checked)} />
      {label}
    </label>
  );

  return (
    <div className="space-y-8">
      <section>
        <SectionHeader title={t('Working week')} description={t('The weekend decides how leave days and overtime hours are counted.')} />
        <WizardField field="rules.workWeek.weekendDays" label={t('Weekend (days off)')} help={t('Country default: {days}.', { days: weekendLabel(packWeekend) })} className="max-w-sm">
          {control => <select {...control} className="select w-full" disabled={readOnly} value={rules.workWeek.weekendDays}
            onChange={e => setRules({ workWeek: { weekendDays: e.target.value, override: e.target.value !== packWeekend } })}>
            {WEEKEND_OPTIONS.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}
          </select>}
        </WizardField>
        <p className="mt-2 text-xs text-slate-600 dark:text-slate-400" data-testid="work-week-mode">{rules.workWeek.override ? t('This company will use its own working week.') : t('This company follows the country working week.')}</p>
      </section>

      <section className="grid gap-4 md:grid-cols-3">
        <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('End-of-service settings')}</p>
          <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t('Gratuity rules for {country}. Created from the country pack only if they do not exist yet; existing settings are never changed.', { country: countryName || '—' })}</p>
          {planLine('gccSetting', msg('Will be created from the country pack'), msg('Already set — kept as they are'))}
        </div>
        <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Public holidays')}</p>
          <div className="mt-2">{toggle(rules.holidayCalendar.usePack, v => setRules({ holidayCalendar: { usePack: v } }), t('Use the {country} public holiday calendar', { country: countryName || '—' }), 'rules.holidayCalendar.usePack')}</div>
          {rules.holidayCalendar.usePack ? planLine('holidayCalendar', msg('Will be created'), msg('Already exists — reused')) : <p className="mt-1 text-xs text-amber-800 dark:text-amber-200">{t('No holiday calendar will be added. Add one later in Setup → Holiday calendars before the first leave request.')}</p>}
        </div>
        <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Statutory leave policies')}</p>
          <div className="mt-2">{toggle(rules.leavePolicies.usePack, v => setRules({ leavePolicies: { usePack: v } }), t('Use the statutory leave policies for {country}', { country: countryName || '—' }), 'rules.leavePolicies.usePack')}</div>
          <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t('The legal minimums cannot be lowered here. Company extras are set later in Leave policies.')}</p>
          {rules.leavePolicies.usePack ? planLine('leavePolicy', msg('Will be created'), msg('Already exists — reused')) : <p className="mt-1 text-xs text-amber-800 dark:text-amber-200">{t('No leave policies will be added. Employees cannot request leave until you add them in Setup → Leave policies.')}</p>}
        </div>
      </section>

      <section>
        <SectionHeader title={t('Employee numbering')} description={t('How employee numbers are made for this company.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-3">
          <WizardField field="rules.employeeNumbering.prefix" label={t('Prefix')}>
            {control => <input {...control} className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.prefix} onChange={e => setNumbering({ prefix: e.target.value.toUpperCase() })} />}
          </WizardField>
          <WizardField field="rules.employeeNumbering.paddingLength" label={t('Digits in the number')}>
            {control => <input {...control} type="number" min={1} max={10} inputMode="numeric" className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.paddingLength} onChange={e => setNumbering({ paddingLength: Number(e.target.value) })} />}
          </WizardField>
          <WizardField field="rules.employeeNumbering.nextSequence" label={t('Next number')}>
            {control => <input {...control} type="number" min={1} inputMode="numeric" className="input w-full" readOnly={readOnly} value={rules.employeeNumbering.nextSequence} onChange={e => setNumbering({ nextSequence: Number(e.target.value) })} />}
          </WizardField>
        </div>
        <div className="mt-3 flex flex-wrap gap-x-5 gap-y-2">
          {toggle(rules.employeeNumbering.useCountryPrefix, v => setNumbering({ useCountryPrefix: v }), t('Add the country code'), 'rules.employeeNumbering.useCountryPrefix')}
          {toggle(rules.employeeNumbering.useBranchPrefix, v => setNumbering({ useBranchPrefix: v }), t('Add the branch code'), 'rules.employeeNumbering.useBranchPrefix')}
          {toggle(rules.employeeNumbering.useDepartmentPrefix, v => setNumbering({ useDepartmentPrefix: v }), t('Add the department code'), 'rules.employeeNumbering.useDepartmentPrefix')}
          {toggle(rules.employeeNumbering.useYear, v => setNumbering({ useYear: v }), t('Add the joining year'), 'rules.employeeNumbering.useYear')}
        </div>
        <p className="mt-3 text-sm text-slate-800 dark:text-slate-200" data-testid="numbering-preview">{t('The next employee number will look like {example}.', { example: numberingPreview(body) })}</p>
      </section>
    </div>
  );
}
