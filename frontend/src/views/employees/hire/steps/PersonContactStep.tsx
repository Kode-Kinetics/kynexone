'use client';

import { ArabicNameField } from '@/src/components/ArabicNameField';
import { useLocale } from '@/src/contexts/LocaleContext';
import { referenceOptions } from '@/src/lib/referenceLists';
import { exampleMobile, mobileCountries } from '@/src/lib/hire/phone';
import { GCC_CODES, SAUDI, type CountryOption } from '@/src/lib/hire/nationality';
import { CountryPicker } from '../fields/CountryPicker';
import { DateField } from '../fields/DateField';
import { FieldShell, inputClass } from '../fields/FieldShell';
import type { StepProps } from '../types';

export const GENDER_FALLBACK = ['Male', 'Female'];
export const MARITAL_FALLBACK = ['Single', 'Married', 'Divorced', 'Widowed'];

export function PersonContactStep({ form, set, errorFor, blur, companyCountry, lookups, onNationality, countryOptions, onDobBlur }: StepProps & {
  onNationality: (code: string) => void; countryOptions?: CountryOption[]; onDobBlur: () => void;
}) {
  const { t, locale } = useLocale();
  const gender = referenceOptions(lookups.refs.gender, locale, GENDER_FALLBACK, form.gender);
  const marital = referenceOptions(lookups.refs.maritalStatus, locale, MARITAL_FALLBACK, form.maritalStatus);
  const country = form.mobileCountry || companyCountry;
  const example = exampleMobile(country);
  const display = new Intl.DisplayNames([locale], { type: 'region' });
  const dials = mobileCountries()
    .map(c => ({ ...c, name: display.of(c.code) ?? c.code }))
    .sort((a, b) => (a.code === companyCountry ? -1 : b.code === companyCountry ? 1 : (GCC_CODES as readonly string[]).includes(a.code) === (GCC_CODES as readonly string[]).includes(b.code) ? a.name.localeCompare(b.name, locale) : (GCC_CODES as readonly string[]).includes(a.code) ? -1 : 1));
  const select = 'input w-full min-w-0';
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <div className="sm:col-span-2"><ArabicNameField source={form.englishName} value={form.arabicName} onChange={v => set('arabicName', v)} /></div>
      <FieldShell field="preferredName" label={t('Preferred name')}>
        {p => <input {...p} value={form.preferredName} onChange={e => set('preferredName', e.target.value)} className={inputClass} />}
      </FieldShell>
      <FieldShell field="gender" label={t('Gender')}>
        {p => <select {...p} value={form.gender} onChange={e => set('gender', e.target.value)} className={select}><option value="">{t('Not chosen')}</option>{gender.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
      </FieldShell>
      <FieldShell field="maritalStatus" label={t('Marital status')}>
        {p => <select {...p} value={form.maritalStatus} onChange={e => set('maritalStatus', e.target.value)} className={select}><option value="">{t('Not chosen')}</option>{marital.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}</select>}
      </FieldShell>
      <CountryPicker field="nationalityCode" label={t('Nationality')} value={form.nationalityCode} options={countryOptions}
        onSelect={code => { set('nationalityCode', code); onNationality(code); }} onFreeText={text => set('nationalityCode', text)} onBlur={() => blur('nationalityCode')}
        error={errorFor('nationalityCode')} hint={t('Saudi Arabia is listed first. Pick a country from the list.')} />
      <DateField field="dob" label={t('Date of birth')} value={form.dob} onChange={v => set('dob', v)} onBlur={() => { blur('dob'); onDobBlur(); }} error={errorFor('dob')} />
      <div className="min-w-0 sm:col-span-2">
        <div className="grid grid-cols-[minmax(0,2fr)_minmax(0,3fr)] gap-2">
          <FieldShell field="mobileCountry" label={t('Mobile country code')}>
            {p => <select {...p} dir="ltr" value={country} onChange={e => set('mobileCountry', e.target.value)} className={select}>{dials.map(c => <option key={c.code} value={c.code}>{`${c.name} ${c.dial}`}</option>)}</select>}
          </FieldShell>
          <FieldShell field="mobileNational" label={t('Mobile number')} error={errorFor('mobileNational')} hint={example ? t('Example: {example}', { example }) : undefined}>
            {p => <input {...p} type="tel" inputMode="tel" dir="ltr" autoComplete="off" placeholder={example} value={form.mobileNational} onChange={e => set('mobileNational', e.target.value)} onBlur={() => blur('mobileNational')} className={`${inputClass} text-start`} />}
          </FieldShell>
        </div>
      </div>
      <FieldShell field="personalEmail" label={t('Personal email')} error={errorFor('personalEmail')} className="sm:col-span-2">
        {p => <input {...p} type="email" dir="ltr" value={form.personalEmail} onChange={e => set('personalEmail', e.target.value)} onBlur={() => blur('personalEmail')} className={`${inputClass} text-start`} />}
      </FieldShell>
    </div>
  );
}

export { SAUDI };
