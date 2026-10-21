'use client';

import Link from 'next/link';
import { useFormat } from '../../hooks/useFormat';
import { useLocale } from '../../contexts/LocaleContext';
import type { CompanySetupContext, CompanySetupDraftSummary, StartFrom } from '../../api/companySetup';
import { planUsage, stepCopy, type StepId } from '../../lib/companySetupWizard';
import { Notice, WizardField } from './wizardForm';
import { CountryOptions } from './CountryOptions';

export interface StartChoice { startFrom: StartFrom; countryCode: string; copyFromCompanyId?: string }

/**
 * Step 1. Says up front whether a company can be created at all (plan limit, creation mode), lists
 * drafts to resume, and picks the starting point. Nothing is written until Start.
 */
export function StartStep({ context, drafts, currentDraftId, choice, onChoice, onResume, onDiscard, started }: {
  context: CompanySetupContext;
  drafts: CompanySetupDraftSummary[];
  currentDraftId: string | null;
  choice: StartChoice;
  onChoice: (choice: StartChoice) => void;
  onResume: (id: string) => void;
  onDiscard: (summary: CompanySetupDraftSummary) => void;
  /** True once this screen holds a draft: the starting point is then fixed. */
  started: boolean;
}) {
  const { t } = useLocale();
  const fmt = useFormat();
  const usage = planUsage(context);
  const others = drafts.filter(d => d.id !== currentDraftId);
  const sisters = context.sisterCompanies;
  const sisterName = sisters.find(s => s.id === choice.copyFromCompanyId)?.legalNameEn;
  return (
    <div className="space-y-6">
      {usage && (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-white/10 dark:bg-white/[0.03]" data-testid="plan-usage">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">
            {usage.allowed === null
              ? t('{used, plural, one {# company} other {# companies}} in this workspace', { used: usage.used })
              : t('{used} of {allowed} companies on your plan', { used: usage.used, allowed: usage.allowed })}
          </p>
          {usage.allowed !== null && (
            <div className="h-2 min-w-[8rem] flex-1 overflow-hidden rounded-full bg-slate-200 dark:bg-white/10" aria-hidden="true">
              <div className={`h-full rounded-full ${usage.full ? 'bg-amber-500' : 'bg-sapphire'}`} style={{ width: `${Math.min(100, Math.round((usage.used / Math.max(1, usage.allowed)) * 100))}%` }} />
            </div>
          )}
        </div>
      )}

      {!context.canCreate && (
        <Notice tone="warning" role="alert">
          <p className="font-semibold">{t('A new company cannot be created right now.')}</p>
          <p className="mt-1">{context.refusalMessage || t('Your workspace does not allow another company.')}</p>
          <p className="mt-1">{refusalNextStep(context.refusalCode, t)}</p>
        </Notice>
      )}

      {others.length > 0 && (
        <section aria-labelledby="ccw-resume-heading" className="space-y-2" data-testid="resume-drafts">
          <h3 id="ccw-resume-heading" className="text-base font-semibold text-slate-900 dark:text-white">{t('Continue where you left off')}</h3>
          <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
            {others.map(d => (
              <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{d.legalNameEn || t('Untitled company')}</bdi></p>
                  <p className="text-xs text-slate-600 dark:text-slate-400">
                    {t('Stopped at step {step}: {title}', { step: d.step, title: t(stepCopy((Math.min(Math.max(d.step, 1), 7)) as StepId, d.existingCompanyId ? 'existing' : 'create').title) })}
                    {' · '}{t('Saved {when} by {name}', { when: fmt.dateTime(d.updatedAtUtc), name: d.createdByName })}
                  </p>
                </div>
                <div className="flex gap-2">
                  <button type="button" className="btn-secondary" onClick={() => onDiscard(d)}>{t('Discard')}<span className="sr-only"> {d.legalNameEn}</span></button>
                  <button type="button" className="btn-primary" onClick={() => onResume(d.id)}>{t('Resume')}<span className="sr-only"> {d.legalNameEn}</span></button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      {context.canCreate && (
        <fieldset className="space-y-3" disabled={started}>
          <legend className="text-base font-semibold text-slate-900 dark:text-white">{t('Start from')}</legend>
          {started && <p className="text-sm text-slate-600 dark:text-slate-400">{t('This draft has already started. Its starting point stays as chosen; change any detail on the next steps.')}</p>}
          <div className="grid gap-3 md:grid-cols-2">
            <label className={`flex cursor-pointer gap-3 rounded-xl border p-4 ${choice.startFrom === 'defaults' ? 'border-sapphire bg-sapphire/[0.04] dark:border-cyanAccent' : 'border-slate-200 dark:border-white/10'}`}>
              <input type="radio" name="ccw-start-from" className="mt-1" checked={choice.startFrom === 'defaults'} onChange={() => onChoice({ ...choice, startFrom: 'defaults', copyFromCompanyId: undefined })} />
              <span className="min-w-0">
                <span className="block text-sm font-semibold text-slate-900 dark:text-white">{t('Country defaults')}</span>
                <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{t('A head office, the statutory leave policies, public holidays and working week for the country. You review every item.')}</span>
              </span>
            </label>
            <label className={`flex gap-3 rounded-xl border p-4 ${sisters.length === 0 ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'} ${choice.startFrom === 'copy' ? 'border-sapphire bg-sapphire/[0.04] dark:border-cyanAccent' : 'border-slate-200 dark:border-white/10'}`}>
              <input type="radio" name="ccw-start-from" className="mt-1" disabled={sisters.length === 0} checked={choice.startFrom === 'copy'} onChange={() => onChoice({ ...choice, startFrom: 'copy', copyFromCompanyId: sisters[0]?.id, countryCode: sisters[0]?.countryCode ?? choice.countryCode })} />
              <span className="min-w-0">
                <span className="block text-sm font-semibold text-slate-900 dark:text-white">{sisterName ? t('Copy from {company}', { company: sisterName }) : t('Copy from a sister company')}</span>
                <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{sisters.length === 0
                  ? t('Available once the group has an active company.')
                  : t('Copies branches, cost centres, the employee numbering rule, holidays and leave policies into this draft for you to edit. Nothing is shared between the two companies.')}</span>
              </span>
            </label>
          </div>
          {choice.startFrom === 'defaults'
            ? <WizardField field="startCountry" label={t('Country')} required help={t('The country decides the labour law, leave entitlements and holidays the defaults come from.')} className="max-w-sm">
                {control => <select {...control} className="select w-full" value={choice.countryCode} onChange={e => onChoice({ ...choice, countryCode: e.target.value })}><CountryOptions /></select>}
              </WizardField>
            : <WizardField field="copyFromCompanyId" label={t('Company to copy from')} required className="max-w-sm">
                {control => <select {...control} className="select w-full" value={choice.copyFromCompanyId ?? ''} onChange={e => {
                  const sister = sisters.find(s => s.id === e.target.value);
                  onChoice({ ...choice, copyFromCompanyId: e.target.value || undefined, countryCode: sister?.countryCode ?? choice.countryCode });
                }}>
                  <option value="">{t('Choose a company…')}</option>
                  {sisters.map(s => <option key={s.id} value={s.id}>{s.legalNameEn}</option>)}
                </select>}
              </WizardField>}
        </fieldset>
      )}

      {!context.canCreate && <Link href="/setup?tab=companies" className="btn-secondary">{t('Back to companies')}</Link>}
    </div>
  );
}

function refusalNextStep(code: string | null | undefined, t: (key: string) => string): string {
  switch (code) {
    case 'company_limit_reached': return t('To add another company, ask your account manager to raise the company limit on your plan.');
    case 'account_type_single_company': return t('This is a single-company account. Ask your account manager to switch it to a group account to add companies.');
    case 'company_creation_platform_controlled': return t('Companies for this account are created by the KynexOne team. Ask your account manager to add it.');
    case 'company_creation_platform_approval': return t('New companies need platform activation, which is currently switched off. Ask your account manager to enable it; no draft is kept in the meantime.');
    case 'not_group_scope': return t('Only a group administrator can create a company. Ask your group super admin.');
    default: return t('Ask your account manager or group super admin what is needed.');
  }
}
