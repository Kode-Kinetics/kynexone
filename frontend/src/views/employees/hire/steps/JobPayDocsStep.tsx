'use client';

import { useLocale } from '@/src/contexts/LocaleContext';
import { formatAmount } from '@/src/lib/hire/money';
import { parseAmount } from '@/src/lib/hire/payload';
import { referenceOptions } from '@/src/lib/referenceLists';
import { defaultIdType, nationalityClass, type IdType } from '@/src/lib/hire/nationality';
import { validateIdNumber } from '@/src/lib/hire/saudiId';
import { DateField } from '../fields/DateField';
import { FieldShell, inputClass } from '../fields/FieldShell';
import type { StepProps } from '../types';

export const EMPLOYMENT_FALLBACK = ['Full-time', 'Part-time', 'Contract'];
export const CONTRACT_FALLBACK = ['Permanent', 'Fixed term'];

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <details open className="group rounded-xl border border-slate-200 p-3 dark:border-white/10">
      <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-100">{title}</summary>
      <div className="mt-3 grid gap-4 sm:grid-cols-2">{children}</div>
    </details>
  );
}

export function JobPayDocsStep({ form, set, errorFor, blur, lookups, currency, canOverrideChecksum, resident, onResident, onIdBlur }: StepProps & {
  canOverrideChecksum: boolean; resident: boolean; onResident: (v: boolean) => void; onIdBlur: () => void;
}) {
  const { t, locale } = useLocale();
  const employment = referenceOptions(lookups.refs.employmentType, locale, EMPLOYMENT_FALLBACK, form.employmentType);
  const contract = referenceOptions(lookups.refs.contractType, locale, CONTRACT_FALLBACK, form.contractType);
  const grade = lookups.grades.find(g => g.id === form.gradeId);
  const basic = parseAmount(form.basic);
  const outOfBand = !!grade && basic !== undefined && basic > 0 && grade.maxSalary > 0 && (basic < grade.minSalary || basic > grade.maxSalary);
  const klass = nationalityClass(form.nationalityCode);
  const idType = (form.idType || defaultIdType(form.nationalityCode, resident) || '') as IdType | '';
  const idOptions: Array<{ value: IdType; label: string }> = [
    { value: 'nationalId', label: t('National ID') }, { value: 'iqama', label: t('Iqama') },
    { value: 'gccId', label: t('GCC national ID') }, { value: 'passport', label: t('Passport') },
  ];
  const checksumBad = (idType === 'nationalId' || idType === 'iqama') && form.idNumber.trim() !== '' && validateIdNumber(idType, form.idNumber) === 'checksum';
  const money = (f: 'basic' | 'housing' | 'transport' | 'other', label: string) => (
    <FieldShell field={f} label={label} error={errorFor(f)} hint={f === 'basic' && grade && grade.maxSalary > 0 ? t('Grade band: {min} to {max}', { min: formatAmount(grade.minSalary, currency, locale), max: formatAmount(grade.maxSalary, currency, locale) }) : undefined}>
      {p => <input {...p} type="text" inputMode="decimal" dir="ltr" value={form[f]} onChange={e => set(f, e.target.value)} onBlur={() => blur(f)} className={`${inputClass} text-start`} />}
    </FieldShell>
  );
  const select = 'input w-full min-w-0';
  return (
    <div className="space-y-4">
      <Section title={t('Employment details')}>
        <FieldShell field="employmentType" label={t('Employment type')}>
          {p => <select {...p} value={form.employmentType} onChange={e => set('employmentType', e.target.value)} className={select}><option value="">{t('Not chosen')}</option>{employment.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
        </FieldShell>
        <FieldShell field="contractType" label={t('Contract type')}>
          {p => <select {...p} value={form.contractType} onChange={e => set('contractType', e.target.value)} className={select}><option value="">{t('Not chosen')}</option>{contract.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
        </FieldShell>
        <DateField field="contractEndDate" label={t('Contract end date')} value={form.contractEndDate} onChange={v => set('contractEndDate', v)} onBlur={() => blur('contractEndDate')} error={errorFor('contractEndDate')} />
        <FieldShell field="workLocation" label={t('Work location')}>
          {p => <input {...p} value={form.workLocation} onChange={e => set('workLocation', e.target.value)} className={inputClass} />}
        </FieldShell>
      </Section>
      <Section title={t('Payroll and bank')}>
        <FieldShell field="iban" label={t('IBAN')} error={errorFor('iban')} hint={t('Saudi IBANs have 24 characters and start with SA.')}>
          {p => <input {...p} dir="ltr" autoComplete="off" value={form.iban} onChange={e => set('iban', e.target.value)} onBlur={() => blur('iban')} className={`${inputClass} text-start`} />}
        </FieldShell>
        <FieldShell field="molId" label={t('MOL ID')}>
          {p => <input {...p} dir="ltr" value={form.molId} onChange={e => set('molId', e.target.value)} className={`${inputClass} text-start`} />}
        </FieldShell>
      </Section>
      <Section title={t('Salary')}>
        {money('basic', t('Basic salary'))}
        {money('housing', t('Housing allowance'))}
        {money('transport', t('Transport allowance'))}
        {money('other', t('Other allowance'))}
        {outOfBand && <p role="status" className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-xs text-amber-900 sm:col-span-2 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">{t('The basic salary is outside the pay band of the grade. You can still save; you will be asked for a reason.')}</p>}
      </Section>
      <Section title={t('Identity documents')}>
        <FieldShell field="idType" label={t('ID type')} hint={klass === 'unknown' ? t('Choose a nationality on the previous step to suggest the ID type.') : undefined}>
          {p => (
            <select {...p} value={idType} onChange={e => set('idType', e.target.value as IdType)} className={select}>
              <option value="">{t('Not chosen')}</option>
              {idOptions.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          )}
        </FieldShell>
        {klass === 'expat' && (
          <label className="flex items-center gap-2 self-end pb-2 text-sm text-slate-700 dark:text-slate-200">
            <input type="checkbox" checked={resident} onChange={e => onResident(e.target.checked)} />{t('Resident in Saudi Arabia')}
          </label>
        )}
        <FieldShell field="idNumber" label={t('ID number')} error={errorFor('idNumber')}>
          {p => <input {...p} dir="ltr" autoComplete="off" value={form.idNumber} onChange={e => set('idNumber', e.target.value)} onBlur={() => { blur('idNumber'); onIdBlur(); }} className={`${inputClass} text-start`} />}
        </FieldShell>
        <DateField field="idExpiry" label={t('ID expiry date')} value={form.idExpiry} onChange={v => set('idExpiry', v)} onBlur={() => blur('idExpiry')} error={errorFor('idExpiry')} />
        {checksumBad && canOverrideChecksum && (
          <FieldShell field="idChecksumOverrideReason" label={t('Reason to use this number anyway')} hint={t('Only for rare legacy numbers. The reason is recorded in the audit trail.')} className="sm:col-span-2">
            {p => <input {...p} value={form.idChecksumOverrideReason} onChange={e => set('idChecksumOverrideReason', e.target.value)} className={inputClass} />}
          </FieldShell>
        )}
      </Section>
    </div>
  );
}
