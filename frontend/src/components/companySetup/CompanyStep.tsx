'use client';

import { useState, type FocusEvent, type ReactNode } from 'react';
import { Lock } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { DEFAULT_WORK_EMAIL_PATTERN, WORK_EMAIL_PATTERNS, WORK_EMAIL_PATTERN_LABELS, isValidEmailDomain } from '../../api/organization';
import { assembleWorkEmail, deriveWorkEmailLocalPart } from '../../api/employees';
import type { CompanySetupCompany } from '../../api/companySetup';
import { COUNTRY_CURRENCY, CURRENCIES, firstPayrollMonthLocked, isSaudi, legacyWarnings } from '../../lib/companySetupWizard';
import {
  cleanEmailDomain, currencyLabel, digitsOnly, hasArabicLetters, isPastMonth, isPublicEmailDomain, isSaudiVatNumber, maskMol, maskRegistration, monthLabel, toAsciiDigits,
} from '../../lib/companySetupFields';
import { CountryOptions } from './CountryOptions';
import { Notice, SectionHeader, WizardField, useWizardForm } from './wizardForm';
import { ArabicSuggest } from './ArabicSuggest';

function assembleWorkEmailPreview(pattern: string | undefined, domain: string | undefined, placeholderDomain: string): string {
  const local = deriveWorkEmailLocalPart('John Smith', pattern || DEFAULT_WORK_EMAIL_PATTERN);
  return domain && isValidEmailDomain(domain) ? assembleWorkEmail(local, domain) : `${local}@${placeholderDomain}`;
}

/** A field's help line; an amber hint replaces it (same place, so nothing below moves) once the field was left. */
function Help({ hint, children }: { hint?: string | null; children?: ReactNode }) {
  if (hint) return <span className="font-medium text-amber-800 dark:text-amber-200">{hint}</span>;
  return <>{children}</>;
}

/** A locked field says why, with a lock so it reads as deliberate, not broken. */
function Locked({ children }: { children: ReactNode }) {
  return <span className="inline-flex items-start gap-1"><Lock className="mt-0.5 h-3 w-3 shrink-0" aria-hidden="true" /><span>{children}</span></span>;
}

/** Step 2: the legal entity. Saudi rules (Arabic name, 10-digit CR, MOL number, SAR) apply when the country is SA. */
export function CompanyStep() {
  const t = useT();
  const { locale } = useLocale();
  const { body, update, readOnly, context, baseline } = useWizardForm();
  const c = body.company;
  const saudi = isSaudi(c.countryCode);
  // Addendum A4: an existing company keeps its country and currency (payroll, GOSI and bank files depend on them),
  // and its first payroll month once payroll has started.
  const existing = !!body.existingCompanyId;
  const monthLocked = existing && firstPayrollMonthLocked(c);
  const identityReason = t('Fixed for an existing company: payroll, GOSI and bank files depend on it.');
  const set = (patch: Partial<CompanySetupCompany>) => update(b => ({ ...b, company: { ...b.company, ...patch } }));
  // Hints (never blockers) wait until the field was left, like the errors do.
  const [left, setLeft] = useState<ReadonlySet<string>>(() => new Set());
  const onBlurCapture = (event: FocusEvent<HTMLDivElement>) => {
    const field = (event.target as HTMLElement).getAttribute?.('data-field');
    if (field && !left.has(field)) setLeft(prev => new Set([...prev, field]));
  };
  const text = (key: keyof CompanySetupCompany, opts: { arabic?: boolean; code?: boolean; inputMode?: 'numeric' | 'text' | 'decimal'; placeholder?: string; autoComplete?: string; maxLength?: number; mask?: (v: string) => string; onBlur?: () => void } = {}) =>
    (control: object) => <input {...control} className="input w-full" readOnly={readOnly}
      dir={opts.arabic ? 'rtl' : opts.code ? 'ltr' : undefined} lang={opts.arabic ? 'ar' : undefined}
      inputMode={opts.inputMode} autoComplete={opts.autoComplete ?? 'off'} placeholder={opts.placeholder} maxLength={opts.maxLength}
      spellCheck={opts.code ? false : undefined} onBlur={opts.onBlur}
      value={String(c[key] ?? '')} onChange={e => set({ [key]: opts.mask ? opts.mask(e.target.value) : e.target.value } as Partial<CompanySetupCompany>)} />;

  const changeCountry = (country: string) => {
    // The currency follows the country while it is still the previous country's own currency; one the reader chose stays.
    const before = COUNTRY_CURRENCY[c.countryCode] ?? '';
    const after = COUNTRY_CURRENCY[country] ?? '';
    const currency = isSaudi(country) ? 'SAR' : !c.currency || c.currency === before ? after || c.currency : c.currency;
    // Qiwa exists only in Saudi Arabia: leaving it drops a Qiwa id that merely followed the MOL number.
    const qiwaFollowed = !c.qiwaEstablishmentId || c.qiwaEstablishmentId === (c.molEstablishmentNumber ?? '');
    set({ countryCode: country, currency, ...(!isSaudi(country) && qiwaFollowed ? { qiwaEstablishmentId: '' } : {}) });
  };

  const domain = (c.emailDomain ?? '').trim().toLowerCase();
  const arabicNameHint = left.has('company.legalNameAr') && (c.legalNameAr ?? '').trim() && !hasArabicLetters(c.legalNameAr)
    ? t('This name has no Arabic letters. Enter it as written on the CR certificate.') : null;
  const vatHint = saudi && left.has('company.taxNumber') && (c.taxNumber ?? '').trim() && !isSaudiVatNumber(c.taxNumber ?? '')
    ? t('A Saudi VAT number is 15 digits, starting and ending with 3. Check the ZATCA certificate.') : null;
  const domainHint = domain && isPublicEmailDomain(domain)
    ? t('{domain} is a public email service. Use the domain of your company email, such as acme.sa.', { domain }) : null;
  const month = monthLabel(c.firstPayrollMonth, locale);
  const monthHint = !monthLocked && month && isPastMonth(c.firstPayrollMonth)
    ? t('{month} is in the past. You will need opening balances for the months before it.', { month }) : null;

  // Existing mode: values saved before today's rules and not changed here do not block; they are flagged instead.
  const legacy = existing ? legacyWarnings({ body, context, baseline }) : [];
  return (
    <div className="space-y-8" onBlurCapture={onBlurCapture}>
      {legacy.length > 0 && (
        <div data-testid="legacy-warnings"><Notice tone="warning" role="status">
          <p className="font-semibold">{t('These were saved before today’s checks. You can continue, and correct them when you can.')}</p>
          <ul className="mt-1 list-disc space-y-0.5 ps-5">{legacy.map(w => <li key={w.field}>{t(w.message, w.params)}</li>)}</ul>
        </Notice></div>
      )}
      <section>
        <SectionHeader title={t('Legal entity')} description={t('Enter the details exactly as they appear on the commercial registration.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.legalNameEn" label={t('Legal name (English)')} required>{text('legalNameEn', { placeholder: t('For example, Riyadh Trading Company LLC'), maxLength: 200, autoComplete: 'organization' })}</WizardField>
          <WizardField field="company.legalNameAr" label={t('Legal name (Arabic)')} required={saudi}
            help={<Help hint={arabicNameHint}>{saudi ? t('As written on the CR certificate.') : t('Optional outside Saudi Arabia.')}</Help>}>
            {control => <>
              {text('legalNameAr', { arabic: true, placeholder: 'شركة الرياض للتجارة', maxLength: 200 })(control)}
              <ArabicSuggest source={c.legalNameEn} value={c.legalNameAr ?? ''} inputId={control.id} readOnly={readOnly} onAccept={legalNameAr => set({ legalNameAr })} />
            </>}
          </WizardField>
          <WizardField field="company.tradeName" label={t('Trade name (optional)')} help={t('The name customers know, if it differs from the legal name.')}>{text('tradeName', { maxLength: 200 })}</WizardField>
          <WizardField field="company.countryCode" label={t('Country')} required help={existing ? <Locked>{identityReason}</Locked> : t('Sets the labour law, public holidays and wage-file rules.')}>
            {control => <select {...control} className="select w-full" disabled={readOnly || existing} value={c.countryCode} onChange={e => changeCountry(e.target.value)}><CountryOptions /></select>}
          </WizardField>
          <WizardField field="company.currency" label={t('Salary currency')} required
            help={existing ? <Locked>{identityReason}</Locked> : saudi ? <Locked>{t('Saudi companies pay salaries in SAR.')}</Locked> : t('Filled in from the country. Change it only if salaries are paid in another currency.')}>
            {control => <select {...control} className="select w-full" disabled={readOnly || saudi || existing} value={c.currency} onChange={e => set({ currency: e.target.value })}>
              <option value="">{t('Choose a currency…')}</option>
              {CURRENCIES.map(code => <option key={code} value={code}>{currencyLabel(code, locale)}</option>)}
            </select>}
          </WizardField>
          <WizardField field="company.firstPayrollMonth" label={t('First payroll month')} required
            help={<Help hint={monthHint}>{monthLocked
              ? <Locked>{t('Payroll has already started for this company, so its first payroll month stays as it is.')}</Locked>
              : month ? t('Payroll in KynexOne starts with {month}. You can change it until payroll starts.', { month })
                : t('The first month you will run payroll for this company in KynexOne. Defaults to next month.')}</Help>}>
            {control => <input {...control} type="month" className="input w-full" dir="ltr" readOnly={readOnly || monthLocked} aria-readonly={monthLocked || undefined} min="2000-01" max="2099-12" placeholder="2026-11" value={c.firstPayrollMonth} onChange={e => set({ firstPayrollMonth: e.target.value })} />}
          </WizardField>
        </div>
      </section>

      <section>
        <SectionHeader title={t('Registration numbers')} description={saudi ? t('Saudi registrations are checked here so the first wage file is not refused later.') : t('Enter the numbers your authorities issued. Only the registration number is required.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.registrationNumber" label={t('Commercial registration (CR) number')} required
            help={saudi ? t('10 digits, as printed on the CR certificate. Arabic digits are fine.') : t('As printed on the registration certificate or trade licence.')}>
            {text('registrationNumber', { code: true, inputMode: saudi ? 'numeric' : 'text', placeholder: saudi ? '1010123456' : undefined, maxLength: saudi ? 10 : 40, mask: v => maskRegistration(v, saudi) })}
          </WizardField>
          <WizardField field="company.molEstablishmentNumber" label={t('MOL establishment number')} required={saudi}
            help={saudi
              ? t('Labour office number, a dash, then the sequence, as shown in Qiwa. Example: 7-1234567. On a phone, type a dot for the dash.')
              : t('Optional. The establishment number your labour ministry gives, if your wage file uses one.')}>
            {control => <input {...control} className="input w-full" dir="ltr" spellCheck={false} readOnly={readOnly} inputMode="decimal" autoComplete="off" placeholder={saudi ? '7-1234567' : undefined} maxLength={15}
              value={c.molEstablishmentNumber ?? ''} onChange={e => {
                const mol = maskMol(e.target.value);
                // Qiwa id follows the MOL number until someone types a different one (Qiwa exists only in Saudi Arabia).
                const followQiwa = saudi && (!c.qiwaEstablishmentId || c.qiwaEstablishmentId === (c.molEstablishmentNumber ?? ''));
                set({ molEstablishmentNumber: mol, ...(followQiwa ? { qiwaEstablishmentId: mol } : {}) });
              }} />}
          </WizardField>
          <WizardField field="company.gosiEmployerId" label={t('GOSI employer number (optional)')}
            help={saudi ? t('The establishment’s subscription number on the GOSI portal, digits only.') : t('Your social-insurance employer number, if you have one.')}>
            {text('gosiEmployerId', { code: true, inputMode: 'numeric', maxLength: 20, mask: v => saudi ? digitsOnly(v, 20) : toAsciiDigits(v) })}
          </WizardField>
          {saudi && <WizardField field="company.unifiedNationalNumber" label={t('Unified National Number (optional)')}
            help={t('The 700 number: 10 digits starting with 7, shown on the CR certificate. Needed for automatic WPS upload with some banks.')}>
            {text('unifiedNationalNumber', { code: true, inputMode: 'numeric', placeholder: '7001234567', maxLength: 10, mask: v => digitsOnly(v, 10) })}
          </WizardField>}
          <WizardField field="company.taxNumber" label={t('VAT number (optional)')}
            help={<Help hint={vatHint}>{saudi ? t('15 digits, starting and ending with 3, as on the ZATCA VAT certificate.') : t('As printed on the tax registration certificate.')}</Help>}>
            {text('taxNumber', { code: true, inputMode: saudi ? 'numeric' : 'text', placeholder: saudi ? '300000000000003' : undefined, maxLength: saudi ? 15 : 30, mask: v => saudi ? digitsOnly(v, 15) : toAsciiDigits(v) })}
          </WizardField>
          {saudi && <WizardField field="company.qiwaEstablishmentId" label={t('Qiwa establishment ID (optional)')} help={t('Usually the same as the MOL establishment number, so it is filled in for you.')}>
            {text('qiwaEstablishmentId', { code: true, inputMode: 'decimal', maxLength: 15, mask: maskMol })}
          </WizardField>}
        </div>
      </section>

      <section>
        <SectionHeader title={t('Email and finance')} description={t('Optional. You can set these later in Setup → Companies.')} />
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="company.emailDomain" label={t('Work email domain (optional)')}
            help={<Help hint={domainHint}>{t('For example acme.sa. KynexOne never creates mailboxes; people sign in with their company email on this domain.')}</Help>}>
            {text('emailDomain', { code: true, placeholder: 'acme.sa', inputMode: 'text', maxLength: 253, onBlur: () => {
              const cleaned = cleanEmailDomain(c.emailDomain);
              if (cleaned !== (c.emailDomain ?? '')) set({ emailDomain: cleaned });
            } })}
          </WizardField>
          <WizardField field="company.workEmailPattern" label={t('Work email pattern (optional)')}
            help={t('Example: {email}', { email: assembleWorkEmailPreview(c.workEmailPattern, c.emailDomain, 'your-domain') })}>
            {control => <select {...control} className="select w-full" dir="ltr" disabled={readOnly} value={c.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN} onChange={e => set({ workEmailPattern: e.target.value })}>
              {WORK_EMAIL_PATTERNS.map(p => <option key={p} value={p}>{WORK_EMAIL_PATTERN_LABELS[p]}</option>)}
            </select>}
          </WizardField>
          <WizardField field="company.erpCompanyCode" label={t('ERP company code (optional)')}
            help={t('The code your ERP uses for this company on journal exports. Leave blank to use the CR number. Only a finance user can set it.')}>
            {text('erpCompanyCode', { code: true, maxLength: 30 })}
          </WizardField>
        </div>
      </section>
    </div>
  );
}
