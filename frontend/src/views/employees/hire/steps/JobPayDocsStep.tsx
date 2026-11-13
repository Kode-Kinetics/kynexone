'use client';

import { useMemo } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { SalaryAllowancesEditor } from '@/src/components/payroll/SalaryAllowancesEditor';
import { gradeLinesFrom } from '@/src/hooks/useCashAllowanceCatalog';
import { decodeValue, encodeValue } from '@/src/lib/salaryAllowances';
import { formatAmount } from '@/src/lib/hire/money';
import { isFixedTerm, parseAmount } from '@/src/lib/hire/payload';
import { bandStatus, hirePackage } from '@/src/lib/hire/gradePackage';
import { effectiveNoticeDays, gosiCohortFor } from '@/src/lib/hire/gosiNotice';
import { bankNameFor } from '@/src/lib/hire/iban';
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

export function JobPayDocsStep({ form, set, errorFor, blur, lookups, currency, companyCountry, banks, allowanceCatalog, gradeFill, hireDefaults, today, canOverrideChecksum, resident, onResident, onIdBlur }: StepProps & {
  canOverrideChecksum: boolean; resident: boolean; onResident: (v: boolean) => void; onIdBlur: () => void;
}) {
  const { t, locale } = useLocale();
  const employment = referenceOptions(lookups.refs.employmentType, locale, EMPLOYMENT_FALLBACK, form.employmentType);
  const contract = referenceOptions(lookups.refs.contractType, locale, CONTRACT_FALLBACK, form.contractType);
  const grade = lookups.grades.find(g => g.id === form.gradeId);
  const basic = parseAmount(form.basic);
  // What the grade gives (what an untouched section means: the server fills a first package from it).
  const gradeLines = useMemo(() => gradeLinesFrom(allowanceCatalog.catalog, form.gradeId), [allowanceCatalog.catalog, form.gradeId]);
  // The package as it will be saved: typed figures plus what the grade adds to a blank allowance. The grade's band is checked on this TOTAL, as the server does.
  const pkg = hirePackage(form, gradeFill);
  const fromGrade = pkg.rows.filter(r => r.fromGrade).reduce((sum, r) => sum + r.amount, 0);
  const band = bandStatus(pkg.total, grade);
  const klass = nationalityClass(form.nationalityCode);
  const cohort = klass === 'saudi' ? gosiCohortFor(form.gosiFirstRegisteredOn) : null;
  const idType = (form.idType || defaultIdType(form.nationalityCode, resident) || '') as IdType | '';
  const idOptions: Array<{ value: IdType; label: string }> = [
    { value: 'nationalId', label: t('National ID') }, { value: 'iqama', label: t('Iqama') }, { value: 'border', label: t('Border Number') },
    { value: 'gccId', label: t('GCC national ID') }, { value: 'passport', label: t('Passport') },
  ];
  const checksumBad = (idType === 'nationalId' || idType === 'iqama') && form.idNumber.trim() !== '' && validateIdNumber(idType, form.idNumber) === 'checksum';
  const fixed = isFixedTerm(form.contractType);
  const bank = companyCountry === 'SA' ? bankNameFor(form.iban, banks, locale) : '';
  const money = (f: 'basic' | 'housing' | 'transport' | 'other', label: string) => {
    const given = f === 'basic' || form[f].trim() !== '' ? undefined : pkg.rows.find(r => r.field === f && r.fromGrade)?.amount;
    return (
      <FieldShell field={f} label={label} error={errorFor(f)} hint={given !== undefined ? t('Left blank, the grade adds {amount} a month. Enter 0 for none.', { amount: formatAmount(given, currency, locale) }) : undefined}>
        {p => <input {...p} type="text" inputMode="decimal" dir="ltr" value={form[f]} onChange={e => set(f, e.target.value)} onBlur={() => blur(f)} className={`${inputClass} text-start`} />}
      </FieldShell>
    );
  };
  const select = 'input w-full min-w-0';
  return (
    <div className="space-y-4">
      <Section title={t('Employment details')}>
        <FieldShell field="employmentType" label={t('Employment type')}>
          {p => <select {...p} value={form.employmentType} onChange={e => set('employmentType', e.target.value)} className={select}><option value="">{t('Not chosen')}</option>{employment.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
        </FieldShell>
        <FieldShell field="contractType" label={t('Contract type')}>
          {p => <select {...p} value={form.contractType} onChange={e => { set('contractType', e.target.value); if (!isFixedTerm(e.target.value)) set('contractEndDate', ''); }} className={select}><option value="">{t('Not chosen')}</option>{contract.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
        </FieldShell>
        <DateField field="contractEndDate" label={t('Contract end date')} value={form.contractEndDate} disabled={!fixed} hint={fixed ? undefined : t('Choose a fixed-term contract type to set an end date.')}
          onChange={v => set('contractEndDate', v)} onBlur={() => blur('contractEndDate')} error={errorFor('contractEndDate')} />
        <DateField field="probationEndDate" label={t('Probation end date')} disabled={form.probationWaived}
          hint={form.probationWaived ? t('No probation: confirmed from the start date.') : t('More than 180 days after the start date needs a reason when you save.')}
          value={form.probationEndDate} onChange={v => set('probationEndDate', v)} onBlur={() => blur('probationEndDate')} error={errorFor('probationEndDate')} />
        <label className="flex items-center gap-2 self-end pb-2 text-sm text-slate-700 dark:text-slate-200">
          <input type="checkbox" data-testid="probation-waived" checked={form.probationWaived}
            onChange={e => { set('probationWaived', e.target.checked); if (e.target.checked) set('probationEndDate', ''); }} />{t('No probation (exception)')}
        </label>
        <FieldShell field="workLocation" label={t('Work location')}>
          {p => <input {...p} value={form.workLocation} onChange={e => set('workLocation', e.target.value)} className={inputClass} />}
        </FieldShell>
        <FieldShell field="noticePeriodDays" label={t('Notice period (days)')} error={errorFor('noticePeriodDays')}
          hint={hireDefaults.noticePeriodDays !== null ? t('The company default is {days} days. Change it for this person if their contract says otherwise.', { days: hireDefaults.noticePeriodDays }) : t('Days of notice either side must give. Leave blank if it is not set yet.')}>
          {p => <input {...p} type="text" inputMode="numeric" dir="ltr" value={form.noticePeriodDays !== '' ? form.noticePeriodDays : String(effectiveNoticeDays('', hireDefaults.noticePeriodDays) ?? '')}
            onChange={e => set('noticePeriodDays', e.target.value)} onBlur={() => blur('noticePeriodDays')} className={`${inputClass} text-start`} />}
        </FieldShell>
      </Section>
      <Section title={t('Payroll and bank')}>
        <FieldShell field="iban" label={t('IBAN')} error={errorFor('iban')} hint={companyCountry === 'SA' ? t('Saudi IBANs have 24 characters and start with SA.') : t('Enter the IBAN with its country code.')}
          extra={{ id: 'hire-iban-bank', node: bank ? <span data-testid="iban-bank">{t('Bank: {bank}', { bank })}</span> : null }}>
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
        <SalaryAllowancesEditor idPrefix="hire-allowances" baseline={gradeLines} baselineKind="grade" noGrade={!form.gradeId}
          value={decodeValue(form.allowances)} onChange={v => set('allowances', encodeValue(v))} basic={basic} currency={currency}
          catalog={allowanceCatalog.catalog} onRetry={allowanceCatalog.retry} showProblems={!!errorFor('allowances')} />
        {errorFor('allowances') && <p role="alert" className="text-sm text-red-600 sm:col-span-2">{errorFor('allowances')}</p>}
        {pkg.total > 0 && (
          <p className="text-sm text-slate-700 sm:col-span-2 dark:text-slate-200" data-testid="hire-package-total">
            <span className="font-semibold">{t('Total monthly pay')}</span>{' '}
            <bdi dir="ltr">{formatAmount(pkg.total, currency, locale)}</bdi>
            {fromGrade > 0 && <span className="text-slate-500"> {t('({amount} of it from the grade)', { amount: formatAmount(fromGrade, currency, locale) })}</span>}
            {grade && grade.maxSalary > 0 && <span className="block text-xs text-slate-500">{t('Grade band: {min} to {max}', { min: formatAmount(grade.minSalary, currency, locale), max: formatAmount(grade.maxSalary, currency, locale) })}</span>}
          </p>
        )}
        {band && <p role="status" className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-xs text-amber-900 sm:col-span-2 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">{t('The total pay is outside the pay band of the grade. You can still save; you will be asked for a reason.')}</p>}
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
        <FieldShell field="idNumber" label={t('ID number')} error={errorFor('idNumber')} hint={checksumBad && !canOverrideChecksum ? t('An HR Manager can approve this exception.') : undefined}>
          {p => <input {...p} dir="ltr" autoComplete="off" value={form.idNumber} onChange={e => set('idNumber', e.target.value)} onBlur={() => { blur('idNumber'); onIdBlur(); }} className={`${inputClass} text-start`} />}
        </FieldShell>
        <DateField field="idExpiry" label={t('ID expiry date')} value={form.idExpiry} onChange={v => set('idExpiry', v)} onBlur={() => blur('idExpiry')} error={errorFor('idExpiry')} />
        {klass === 'saudi' && (
          <DateField field="gosiFirstRegisteredOn" label={t('First GOSI registration date')} value={form.gosiFirstRegisteredOn} max={today}
            onChange={v => set('gosiFirstRegisteredOn', v)} onBlur={() => blur('gosiFirstRegisteredOn')} error={errorFor('gosiFirstRegisteredOn')}
            hint={cohort === 'newEntrant' ? t('On or after 3 July 2024: the new-entrant GOSI schedule applies.')
              : cohort === 'existing' ? t('Before 3 July 2024: the existing GOSI schedule applies.')
              : t('Optional. The date GOSI first registered this person decides the GOSI rate schedule: before 3 July 2024 is the existing schedule, on or after is the new-entrant schedule. Leave blank if you do not know it; payroll then uses the existing schedule and flags it.')} />
        )}
        {klass === 'expat' && idType !== 'passport' && (
          <>
            <FieldShell field="passportNumber" label={t('Passport number')} error={errorFor('passportNumber')}>
              {p => <input {...p} dir="ltr" autoComplete="off" value={form.passportNumber} onChange={e => set('passportNumber', e.target.value)} onBlur={() => blur('passportNumber')} className={`${inputClass} text-start`} />}
            </FieldShell>
            <DateField field="passportExpiry" label={t('Passport expiry date')} value={form.passportExpiry} onChange={v => set('passportExpiry', v)} onBlur={() => blur('passportExpiry')} error={errorFor('passportExpiry')} />
          </>
        )}
        {checksumBad && canOverrideChecksum && (
          <FieldShell field="idChecksumOverrideReason" label={t('Reason to use this number anyway')} hint={t('Only for rare legacy numbers. The reason is recorded in the audit trail.')} className="sm:col-span-2">
            {p => <input {...p} value={form.idChecksumOverrideReason} onChange={e => set('idChecksumOverrideReason', e.target.value)} className={inputClass} />}
          </FieldShell>
        )}
      </Section>
    </div>
  );
}
