'use client';

import { useState } from 'react';
import { AlertTriangle, Plus, X } from 'lucide-react';
import type { BenefitChildBalance, BenefitClaim, BenefitClaimRules, BenefitPaymentPolicy as Policy } from '@/src/api/benefits';
import { useCountryName } from '@/src/components/companySetup/CountryOptions';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import {
  CLAIM_TERMS, CLAIM_TERM_LABELS, COST_TYPES, COST_TYPE_LABELS, GCC_COUNTRIES, inferCoveredTypes, isGccSet, parseCountryList, splitLines,
  type ClaimTerm, type CostType,
} from '@/src/lib/benefitEducation';
import { INPUT, LABEL } from './benefitUi';

const CHECK = 'flex items-center gap-2 text-xs text-slate-600 dark:text-slate-300';
const termLabel = (term: string) => CLAIM_TERM_LABELS[term as ClaimTerm] ?? term;
const costLabel = (type: string) => COST_TYPE_LABELS[type as CostType] ?? type;

/** Plan form, Reimbursement section: child, invoice-line, country and term rules. Off by default; a legacy plan never shows them as set. */
export function EducationRulesEditor({ value, onChange }: { value: Policy; onChange: (value: Policy) => void }) {
  const { t } = useLocale(); const countryName = useCountryName();
  const child = value.claimFor === 'Child';
  const location = !value.allowedCountries ? 'Any' : isGccSet(value.allowedCountries) ? 'Gcc' : 'Listed';
  const [countryText, setCountryText] = useState(location === 'Listed' ? (value.allowedCountries ?? []).join(', ') : '');
  const anyRule = child || !!value.coveredCosts || !!value.allowedCountries || !!value.claimTerms;
  const set = (patch: Partial<Policy>) => onChange({ ...value, ...patch });
  const toggle = (list: readonly string[] | null | undefined, item: string, on: boolean, order: readonly string[]) => {
    const next = new Set(list ?? []); if (on) next.add(item); else next.delete(item);
    return order.filter(entry => next.has(entry));
  };
  return <details open={anyRule} className="rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="education-rules">
    <summary className="cursor-pointer text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Education rules')}</summary>
    <div className="mt-3 space-y-3">
      <p className="text-xs text-slate-500">{t('Use these for school fees and similar claims. Leave them off for an ordinary reimbursement.')}</p>

      <label className={CHECK}><input type="checkbox" checked={child} onChange={event => set(event.target.checked
        ? { claimFor: 'Child', limitPerChild: false, maxChildren: null, extraChildByException: false }
        : { claimFor: undefined, limitPerChild: undefined, maxChildren: undefined, extraChildByException: undefined })} />{t('Each claim is for one of the employee’s children')}</label>
      {child && <div className="space-y-3 ps-6">
        <label className={CHECK}><input type="checkbox" checked={!!value.limitPerChild} onChange={event => set({ limitPerChild: event.target.checked })} />{t('The claim limit applies to each child separately')}</label>
        <div className="grid gap-3 sm:grid-cols-2">
          <label className={LABEL}>{t('Most children covered (optional)')}<input type="number" className={INPUT} min="1" max="20" step="1" value={value.maxChildren ?? ''} onChange={event => {
            const next = event.target.value ? Number(event.target.value) : null;
            set({ maxChildren: next, extraChildByException: next == null ? false : value.extraChildByException });
          }} /></label>
        </div>
        {value.maxChildren != null && <label className={CHECK}><input type="checkbox" checked={!!value.extraChildByException} onChange={event => set({ extraChildByException: event.target.checked })} />{t('Allow more children as an HR exception')}</label>}
        {value.maxChildren != null && <p className="text-xs text-slate-500">{value.extraChildByException
          ? t('A claim for child {next} or later can still be submitted. It is marked as an exception for HR to decide.', { next: value.maxChildren + 1 })
          : t('A claim for child {next} or later is refused.', { next: value.maxChildren + 1 })}</p>}
      </div>}

      <label className={CHECK}><input type="checkbox" checked={!!value.coveredCosts} onChange={event => set({ coveredCosts: event.target.checked ? ['Tuition'] : undefined })} />{t('Only some invoice costs are covered')}</label>
      {value.coveredCosts && <fieldset className="ps-6">
        <legend className="text-xs text-slate-500">{t('Employees list each invoice line. Only the ticked costs count toward the claim.')}</legend>
        <div className="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-3">{COST_TYPES.map(type => <label key={type} className={CHECK}><input type="checkbox" checked={value.coveredCosts!.includes(type)} onChange={event => set({ coveredCosts: toggle(value.coveredCosts, type, event.target.checked, COST_TYPES) })} />{t(COST_TYPE_LABELS[type])}</label>)}</div>
      </fieldset>}

      <label className={LABEL}>{t('Where the school must be')}<select aria-label={t('Where the school must be')} className={INPUT} value={location} onChange={event => {
        const next = event.target.value;
        set({ allowedCountries: next === 'Any' ? undefined : next === 'Gcc' ? [...GCC_COUNTRIES] : parseCountryList(countryText) });
      }}>
        <option value="Any">{t('Any country')}</option>
        <option value="Gcc">{t('GCC countries only')}</option>
        <option value="Listed">{t('Only the countries I list')}</option>
      </select></label>
      {location === 'Gcc' && <p className="text-xs text-slate-500">{GCC_COUNTRIES.map(code => countryName(code)).join(' · ')}</p>}
      {location === 'Listed' && <label className={LABEL}>{t('Country codes')}<input className={INPUT} value={countryText} onChange={event => { setCountryText(event.target.value); set({ allowedCountries: parseCountryList(event.target.value) }); }} />
        <span className="mt-1 block font-normal text-slate-500">{(value.allowedCountries ?? []).length ? (value.allowedCountries ?? []).map(code => countryName(code)).join(' · ') : t('Two-letter codes separated by commas, for example SA, AE.')}</span></label>}

      <label className={CHECK}><input type="checkbox" checked={!!value.claimTerms} onChange={event => set({ claimTerms: event.target.checked ? [...CLAIM_TERMS] : undefined })} />{t('Ask which term the claim covers')}</label>
      {value.claimTerms && <fieldset className="ps-6">
        <legend className="text-xs text-slate-500">{t('All terms share the same yearly limit.')}</legend>
        <div className="mt-2 flex flex-wrap gap-4">{CLAIM_TERMS.map(term => <label key={term} className={CHECK}><input type="checkbox" checked={value.claimTerms!.includes(term)} onChange={event => set({ claimTerms: toggle(value.claimTerms, term, event.target.checked, CLAIM_TERMS) })} />{t(CLAIM_TERM_LABELS[term])}</label>)}</div>
      </fieldset>}
    </div>
  </details>;
}

/** Employee-facing summary of the same rules, under the plan's payment summary. Renders nothing for a legacy plan. */
export function EducationPolicySummary({ policy }: { policy: Policy }) {
  const { t } = useLocale(); const countryName = useCountryName();
  const lines: string[] = [];
  if (policy.claimFor === 'Child') {
    lines.push(policy.limitPerChild ? t('The limit applies to each child.') : t('Claims are made for your children.'));
    if (policy.maxChildren != null) lines.push(policy.extraChildByException
      ? t('Up to {max} children. More need an HR exception.', { max: policy.maxChildren })
      : t('Up to {max} children.', { max: policy.maxChildren }));
  }
  if (policy.coveredCosts) lines.push(t('Covered costs: {list}. Other invoice costs are not paid.', { list: policy.coveredCosts.map(type => t(costLabel(type))).join(', ') }));
  if (policy.allowedCountries) lines.push(isGccSet(policy.allowedCountries) ? t('Schools in GCC countries only.') : t('Schools in: {list}.', { list: policy.allowedCountries.map(code => countryName(code)).join(', ') }));
  if (policy.claimTerms) lines.push(t('Claim per: {list}.', { list: policy.claimTerms.map(term => t(termLabel(term))).join(', ') }));
  if (!lines.length) return null;
  return <ul className="mt-1 list-disc space-y-0.5 ps-4" data-testid="education-policy-summary">{lines.map(line => <li key={line}>{line}</li>)}</ul>;
}

export interface ClaimLineDraft { key: number; type: string; amount: string }

/** "Covered: X · Not covered: Y (Transport, VAT)" for the lines as typed. */
export function CoveredSummary({ lines, coveredCosts, currency }: { lines: { type: string; amount: number }[]; coveredCosts: readonly string[]; currency: string }) {
  const { t } = useLocale(); const format = useFormat();
  const split = splitLines(lines, coveredCosts);
  const covered = format.money(split.covered, currency), notCovered = format.money(split.notCovered, currency);
  return <p className="text-xs font-semibold text-slate-700 dark:text-slate-200" data-testid="claim-covered-summary">{split.notCoveredTypes.length
    ? t('Covered: {covered} · Not covered: {notCovered} ({types})', { covered, notCovered, types: split.notCoveredTypes.map(type => t(costLabel(type))).join(', ') })
    : t('Covered: {covered} · Not covered: {notCovered}', { covered, notCovered })}</p>;
}

/** Invoice lines on the claim form: one row per line on the school invoice, covered or not. */
export function ClaimLinesEditor({ lines, onChange, coveredCosts, currency }: { lines: ClaimLineDraft[]; onChange: (lines: ClaimLineDraft[]) => void; coveredCosts: readonly string[]; currency: string }) {
  const { t } = useLocale();
  const update = (key: number, patch: Partial<ClaimLineDraft>) => onChange(lines.map(line => line.key === key ? { ...line, ...patch } : line));
  const nextKey = Math.max(0, ...lines.map(line => line.key)) + 1;
  return <fieldset className="space-y-2 rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="claim-lines">
    <legend className="px-1 text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Invoice lines')}</legend>
    <p className="text-xs text-slate-500">{t('Enter every line on the invoice. Only covered costs are paid: {list}.', { list: coveredCosts.map(type => t(costLabel(type))).join(', ') })}</p>
    {lines.map((line, index) => <div key={line.key} className="flex items-end gap-2">
      <label className={`${LABEL} min-w-0 flex-1`}>{t('Line {n} cost', { n: index + 1 })}<select aria-label={t('Line {n} cost', { n: index + 1 })} className={INPUT} value={line.type} onChange={event => update(line.key, { type: event.target.value })}>
        {COST_TYPES.map(type => <option key={type} value={type}>{coveredCosts.includes(type) ? t('{cost} · covered', { cost: t(COST_TYPE_LABELS[type]) }) : t('{cost} · not covered', { cost: t(COST_TYPE_LABELS[type]) })}</option>)}
      </select></label>
      <label className={`${LABEL} w-28 shrink-0 sm:w-36`}>{t('Line {n} amount ({currency})', { n: index + 1, currency })}<input type="number" className={INPUT} required min="0.01" step="0.01" value={line.amount} onChange={event => update(line.key, { amount: event.target.value })} /></label>
      {lines.length > 1 && <button type="button" className="mb-2 shrink-0 p-1" aria-label={t('Remove line {n}', { n: index + 1 })} onClick={() => onChange(lines.filter(item => item.key !== line.key))}><X className="h-4 w-4" /></button>}
    </div>)}
    {lines.length < 10 && <button type="button" className="flex items-center gap-1 text-xs font-semibold text-sapphire" onClick={() => onChange([...lines, { key: nextKey, type: COST_TYPES.find(type => !lines.some(item => item.type === type)) ?? 'Other', amount: '' }])}><Plus className="h-3.5 w-3.5" />{t('Add invoice line')}</button>}
    <CoveredSummary lines={lines.map(line => ({ type: line.type, amount: Number(line.amount) }))} coveredCosts={coveredCosts} currency={currency} />
  </fieldset>;
}

/** Child picker: remaining amount per child, and why a child beyond the policy's limit needs an exception or is refused. */
export function ChildPicker({ options, rules, value, onChange, currency }: { options: BenefitChildBalance[]; rules: BenefitClaimRules; value: string; onChange: (id: string) => void; currency: string }) {
  const { t } = useLocale(); const format = useFormat();
  const selected = options.find(child => child.dependantId === value);
  if (!options.length) return <p role="alert" className="rounded-lg bg-amber-50 px-3 py-2 text-xs font-medium text-amber-800 dark:bg-amber-500/10 dark:text-amber-200" data-testid="claim-no-children">{t('No children are on file for this employee. HR needs to add the child under dependants before a claim can be made.')}</p>;
  const optionLabel = (child: BenefitChildBalance) => !child.withinChildLimit
    ? t('{name} · beyond the {max}-child limit', { name: child.name, max: rules.maxChildren ?? 0 })
    : rules.limitPerChild ? t('{name} · {amount} left', { name: child.name, amount: format.money(child.remainingAmount, currency) }) : child.name;
  return <div className="space-y-2">
    <label className={LABEL}>{t('Child')}<select aria-label={t('Child')} className={INPUT} required value={value} onChange={event => onChange(event.target.value)}>
      <option value="">{t('Choose a child…')}</option>
      {options.map(child => <option key={child.dependantId} value={child.dependantId}>{optionLabel(child)}</option>)}
    </select></label>
    {selected && !selected.withinChildLimit && <ChildLimitNotice rules={rules} />}
  </div>;
}

function ChildLimitNotice({ rules }: { rules: BenefitClaimRules }) {
  const { t } = useLocale();
  const max = rules.maxChildren ?? 0;
  return rules.extraChildByException
    ? <div role="status" className="flex gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200" data-testid="claim-child-exception"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" /><span>{t('This policy covers up to {max} children, and {max} children already have claims this period. You can still submit: HR reviews this claim as an exception and may refuse it.', { max })}</span></div>
    : <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300" data-testid="claim-child-blocked">{t('This policy covers up to {max} children, and {max} children already have claims this period. A claim for another child cannot be submitted.', { max })}</p>;
}

/** Child, school, term, invoice lines and any exception on a saved claim (employee list and approval detail). */
export function ClaimEducationDetails({ claim, coveredCosts }: { claim: BenefitClaim; coveredCosts?: readonly string[] | null }) {
  const { t } = useLocale(); const format = useFormat(); const countryName = useCountryName();
  const lines = claim.lines ?? [];
  if (!claim.dependantName && !claim.schoolName && !claim.schoolCountry && !claim.claimTerm && !lines.length && !claim.exceptionReason) return null;
  // The claim records its lines and the claimed (covered) amount; the plan's covered-cost list is not on the claim, so it
  // is taken from the plan when the caller has it, otherwise worked out from the lines only when exactly one answer fits.
  const covered = coveredCosts ?? inferCoveredTypes(lines, claim.amount);
  const invoiceTotal = claim.invoiceTotal ?? splitLines(lines, null).total;
  return <div className="space-y-3" data-testid="claim-education-details">
    {claim.exceptionReason && <div role="status" className="flex gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200" data-testid="claim-exception-banner"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" /><span><strong className="block">{t('HR exception needed')}</strong>{claim.exceptionReason}</span></div>}
    <dl className="grid grid-cols-2 gap-3">
      {claim.dependantName && <div><dt className="text-slate-500">{t('Child')}</dt><dd className="break-words">{claim.dependantName}</dd></div>}
      {claim.claimTerm && <div><dt className="text-slate-500">{t('Claim term')}</dt><dd>{t(termLabel(claim.claimTerm))}</dd></div>}
      {claim.schoolName && <div><dt className="text-slate-500">{t('School')}</dt><dd className="break-words">{claim.schoolName}</dd></div>}
      {claim.schoolCountry && <div><dt className="text-slate-500">{t('School country')}</dt><dd>{countryName(claim.schoolCountry)}</dd></div>}
    </dl>
    {lines.length > 0 && <div>
      <p className="text-slate-500">{t('Invoice lines')}</p>
      <ul className="mt-1 divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-white/5 dark:border-white/10">{lines.map((line, index) => <li key={index} className="flex flex-wrap items-center justify-between gap-2 px-3 py-1.5">
        <span>{t(costLabel(line.type))}{covered && <span className={`ms-2 rounded-full px-2 py-0.5 text-caption font-semibold ${covered.includes(line.type) ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300' : 'bg-slate-200 text-slate-600 dark:bg-white/10 dark:text-slate-300'}`}>{covered.includes(line.type) ? t('Covered') : t('Not covered')}</span>}</span>
        <span className="font-semibold">{format.money(line.amount, claim.currency)}</span>
      </li>)}</ul>
      <p className="mt-1 font-semibold text-slate-700 dark:text-slate-200">{t('Invoice total {total} · claimed {claimed} · not covered {notCovered}', { total: format.money(invoiceTotal, claim.currency), claimed: format.money(claim.amount, claim.currency), notCovered: format.money(Math.max(0, Math.round((invoiceTotal - claim.amount) * 100) / 100), claim.currency) })}</p>
    </div>}
  </div>;
}
