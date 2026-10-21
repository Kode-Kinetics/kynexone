'use client';

import { useT } from '../../hooks/useT';
import { DEFAULT_WORK_EMAIL_PATTERN, WORK_EMAIL_PATTERNS, WORK_EMAIL_PATTERN_LABELS, isValidEmailDomain } from '../../api/organization';
import { assembleWorkEmail, deriveWorkEmailLocalPart } from '../../api/employees';
import type { CompanySetupCompany } from '../../api/companySetup';
import { COUNTRY_CURRENCY, CURRENCIES, firstPayrollMonthLocked, isSaudi } from '../../lib/companySetupWizard';
import { CountryOptions } from './CountryOptions';
import { SectionHeader, WizardField, useWizardForm } from './wizardForm';

function assembleWorkEmailPreview(pattern: string | undefined, domain: string | undefined): string {
  const local = deriveWorkEmailLocalPart('John Smith', pattern || DEFAULT_WORK_EMAIL_PATTERN);
  return domain && isValidEmailDomain(domain) ? assembleWorkEmail(local, domain) : local;
}

/** Step 2: the legal entity. Saudi rules (Arabic name, 10-digit CR, MOL number, SAR) apply when the country is SA. */
export function CompanyStep() {
  const t = useT();
  const { body, update, readOnly } = useWizardForm();
  const c = body.company;
  const saudi = isSaudi(c.countryCode);
  // Addendum A4: an existing company keeps its country and currency (payroll, GOSI and bank files depend on them),
  // and its first payroll month once payroll has started.
  const existing = !!body.existingCompanyId;
  const monthLocked = existing && firstPayrollMonthLocked(c);
  const identityReason = t('Fixed for an existing company: payroll, GOSI and bank files depend on it.');
  const set = (patch: Partial<CompanySetupCompany>) => update(b => ({ ...b, company: { ...b.company, ...patch } }));
  const text = (key: keyof CompanySetupCompany, opts: { arabic?: boolean; inputMode?: 'numeric' | 'text'; placeholder?: string; autoComplete?: string } = {}) =>
    (control: object) => <input {...control} className="input w-full" readOnly={readOnly} dir={opts.arabic ? 'rtl' : undefined} lang={opts.arabic ? 'ar' : undefined}
      inputMode={opts.inputMode} autoComplete={opts.autoComplete ?? 'off'} placeholder={opts.placeholder}
      value={String(c[key] ?? '')} onChange={e => set({ [key]: e.target.value } as Partial<CompanySetupCompany>)} />;

  return (
    <div className="space-y-8">
      <section>
        <SectionHeader title={t('Legal entity')} description={t('Enter the details exactly as they appear on the commercial registration.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.legalNameEn" label={t('Legal name (English)')} required>{text('legalNameEn', { placeholder: t('For example, Riyadh Trading Company LLC') })}</WizardField>
          <WizardField field="company.legalNameAr" label={t('Legal name (Arabic)')} required={saudi} help={saudi ? undefined : t('Optional outside Saudi Arabia.')}>{text('legalNameAr', { arabic: true })}</WizardField>
          <WizardField field="company.tradeName" label={t('Trade name (optional)')}>{text('tradeName')}</WizardField>
          <WizardField field="company.countryCode" label={t('Country')} required help={existing ? identityReason : undefined}>
            {control => <select {...control} className="select w-full" disabled={readOnly || existing} value={c.countryCode} onChange={e => {
              const country = e.target.value;
              const currency = COUNTRY_CURRENCY[country];
              set({ countryCode: country, ...(isSaudi(country) ? { currency: 'SAR' } : currency && !c.currency ? { currency } : {}) });
            }}><CountryOptions /></select>}
          </WizardField>
          <WizardField field="company.currency" label={t('Salary currency')} required help={existing ? identityReason : saudi ? t('Saudi companies pay salaries in SAR.') : t('The currency salaries and grade bands are paid in.')}>
            {control => <select {...control} className="select w-full" disabled={readOnly || saudi || existing} value={c.currency} onChange={e => set({ currency: e.target.value })}>
              <option value="">{t('Choose a currency…')}</option>
              {CURRENCIES.map(code => <option key={code} value={code}>{code}</option>)}
            </select>}
          </WizardField>
          <WizardField field="company.firstPayrollMonth" label={t('First payroll month')} required
            help={monthLocked ? t('Payroll has already started for this company, so its first payroll month stays as it is.') : existing ? t('The first month this company runs payroll in KynexOne. You can change it until payroll starts.') : t('The first month you will run payroll for this company in KynexOne. Defaults to next month.')}>
            {control => <input {...control} type="month" className="input w-full" readOnly={readOnly || monthLocked} aria-readonly={monthLocked || undefined} value={c.firstPayrollMonth} onChange={e => set({ firstPayrollMonth: e.target.value })} />}
          </WizardField>
        </div>
      </section>

      <section>
        <SectionHeader title={t('Registration numbers')} description={saudi ? t('Saudi registrations are checked here so the first wage file is not refused later.') : t('Enter the numbers your authorities issued. Only the registration number is required.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.registrationNumber" label={t('Commercial registration (CR) number')} required help={saudi ? t('10 digits, as printed on the CR certificate.') : undefined}>{text('registrationNumber', { inputMode: saudi ? 'numeric' : 'text' })}</WizardField>
          <WizardField field="company.molEstablishmentNumber" label={t('MOL establishment number')} required={saudi} help={t('Shown in Qiwa, for example 7-1234567. Used on every wage file.')}>
            {control => <input {...control} className="input w-full" readOnly={readOnly} inputMode="numeric" autoComplete="off" value={c.molEstablishmentNumber ?? ''} onChange={e => {
              const mol = e.target.value;
              // Qiwa id follows the MOL number until someone types a different one.
              const followQiwa = !c.qiwaEstablishmentId || c.qiwaEstablishmentId === (c.molEstablishmentNumber ?? '');
              set({ molEstablishmentNumber: mol, ...(followQiwa ? { qiwaEstablishmentId: mol } : {}) });
            }} />}
          </WizardField>
          <WizardField field="company.gosiEmployerId" label={t('GOSI employer number (optional)')}>{text('gosiEmployerId', { inputMode: 'numeric' })}</WizardField>
          {saudi && <WizardField field="company.unifiedNationalNumber" label={t('Unified National Number (optional)')} help={t('10 digits starting with 7. Needed for automatic WPS upload with some banks.')}>{text('unifiedNationalNumber', { inputMode: 'numeric' })}</WizardField>}
          <WizardField field="company.taxNumber" label={t('VAT number (optional)')}>{text('taxNumber', { inputMode: 'numeric' })}</WizardField>
          <WizardField field="company.qiwaEstablishmentId" label={t('Qiwa establishment ID (optional)')} help={t('Usually the same as the MOL establishment number, so it is filled in for you.')}>{text('qiwaEstablishmentId', { inputMode: 'numeric' })}</WizardField>
        </div>
      </section>

      <section>
        <SectionHeader title={t('Email and finance')} description={t('Optional. You can set these later in Setup → Companies.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.emailDomain" label={t('Work email domain (optional)')} help={t('For example acme.sa. KynexOne never creates mailboxes; people sign in with their company email on this domain.')}>{text('emailDomain', { placeholder: 'acme.sa' })}</WizardField>
          <WizardField field="company.workEmailPattern" label={t('Work email pattern (optional)')} help={t('Example: {email}', { email: assembleWorkEmailPreview(c.workEmailPattern, c.emailDomain) })}>
            {control => <select {...control} className="select w-full" disabled={readOnly} value={c.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN} onChange={e => set({ workEmailPattern: e.target.value })}>
              {WORK_EMAIL_PATTERNS.map(p => <option key={p} value={p}>{WORK_EMAIL_PATTERN_LABELS[p]}</option>)}
            </select>}
          </WizardField>
          <WizardField field="company.erpCompanyCode" label={t('ERP company code (optional)')} help={t('The code your ERP uses for this company on journal exports. Leave blank to use the CR number.')}>{text('erpCompanyCode')}</WizardField>
        </div>
      </section>
    </div>
  );
}
