'use client';

import { useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { Download, FileText, Plus, X } from 'lucide-react';
import { benefitsApi, benefitsErrorMessage, type BenefitClaim, type BenefitClaimBalance, type BenefitPaymentPolicy, type BenefitReceipt } from '@/src/api/benefits';
import type { ApprovalDetailRendererProps } from '@/src/components/approvals/approvalDetailRenderers';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { useCountryName } from '@/src/components/companySetup/CountryOptions';
import { CLAIM_TERM_LABELS, COST_TYPE_LABELS, claimRules, educationClaimFields, hasEducationRules, isGccSet, splitLines, type ClaimTerm, type CostType } from '@/src/lib/benefitEducation';
import { CARD, FormError, INPUT, LABEL, Modal, PRIMARY, SECONDARY, today } from './benefitUi';
import { ChildPicker, ClaimEducationDetails, ClaimLinesEditor, CoveredSummary, type ClaimLineDraft } from './BenefitEducationRules';

export interface ClaimBenefit { status?: string; id: string; planName: string; currency: string; effectiveFrom: string; effectiveTo: string | null; paymentPolicy?: BenefitPaymentPolicy | null; claimBalance?: BenefitClaimBalance | null; }
const STATUS_LABELS = { AwaitingApproval: 'Awaiting approval', AwaitingPayroll: 'Approved · awaiting payroll', IncludedInPayroll: 'Included in payroll', Paid: 'Paid', Rejected: 'Rejected', Withdrawn: 'Withdrawn' };

export function ClaimBalance({ balance, currency }: { balance?: BenefitClaimBalance | null; currency: string }) {
  const { t } = useLocale(); const format = useFormat();
  if (!balance) return null;
  return <div className="mt-3 rounded-lg bg-slate-50 p-3 text-xs dark:bg-white/5" data-testid="claim-balance">
    <div className="flex flex-wrap items-center justify-between gap-2"><span className="font-semibold text-slate-700 dark:text-slate-200">{t('Available to claim')}</span><strong className="text-sm text-slate-800 dark:text-slate-100">{format.money(balance.remainingAmount, currency)}</strong></div>
    <p className="mt-1 text-slate-500">{t('Pending {pending} · approved {approved}', { pending: format.money(balance.reservedAmount, currency), approved: format.money(balance.approvedAmount, currency) })}</p>
    {!balance.periodFrom.startsWith('0001') && <p className="mt-1 text-slate-500">{format.date(balance.periodFrom)} – {format.date(balance.periodTo)}</p>}
    {balance.rules?.limitPerChild && !!balance.children?.length && <ul className="mt-2 space-y-1 border-t border-slate-200 pt-2 dark:border-white/10" data-testid="claim-child-balances">{balance.children.map(child => <li key={child.dependantId} className="flex flex-wrap justify-between gap-2"><span className="text-slate-600 dark:text-slate-300">{child.name}</span><span className="font-semibold text-slate-700 dark:text-slate-200">{child.withinChildLimit ? t('{amount} left', { amount: format.money(child.remainingAmount, currency) }) : t('Beyond the child limit')}</span></li>)}</ul>}
  </div>;
}

export function BenefitClaimForm({ benefit, employeeId, onClose, onSubmitted }: { benefit: ClaimBenefit; employeeId?: number; onClose: () => void; onSubmitted: (claim: BenefitClaim) => void }) {
  const { t } = useLocale(); const format = useFormat();
  const { hasPermission, hasRole } = useAuth();
  const canConfigure = employeeId != null && hasPermission('approvals.manage') && (hasRole('Admin') || hasRole('HR Manager'));
  const [amount, setAmount] = useState('');
  const [expenseDate, setExpenseDate] = useState(benefit.effectiveTo && benefit.effectiveTo < today() ? benefit.effectiveTo : today());
  const [invoice, setInvoice] = useState(''); const [description, setDescription] = useState('');
  const [receipts, setReceipts] = useState<BenefitReceipt[]>([]);
  const [uploading, setUploading] = useState(false); const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null); const [missingRoute, setMissingRoute] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const policy = benefit.paymentPolicy;
  const [balance, setBalance] = useState(benefit.claimBalance);
  const [checkingBalance, setCheckingBalance] = useState(false);
  const [balanceError, setBalanceError] = useState<string | null>(null);
  useEffect(() => {
    if (!expenseDate) return;
    let live = true; setCheckingBalance(true); setBalanceError(null);
    benefitsApi.claimBalance(benefit.id, expenseDate, employeeId == null).then(value => { if (live) setBalance(value); })
      .catch(err => { if (live) { setBalance(null); setBalanceError(benefitsErrorMessage(err, t('Could not check the available claim balance.'))); } })
      .finally(() => { if (live) setCheckingBalance(false); });
    return () => { live = false; };
  }, [benefit.id, employeeId, expenseDate, t]);
  // Education-style rules (child, school, country, term, invoice lines). All absent on a legacy plan, which keeps the original form.
  const rules = claimRules(policy, balance);
  const education = hasEducationRules(rules);
  const countryName = useCountryName();
  const [dependantId, setDependantId] = useState('');
  const [schoolName, setSchoolName] = useState(''); const [schoolCountry, setSchoolCountry] = useState(''); const [claimTerm, setClaimTerm] = useState('');
  const [lines, setLines] = useState<ClaimLineDraft[]>(() => [{ key: 1, type: rules.coveredCosts?.[0] ?? 'Tuition', amount: '' }]);
  const children = balance?.children ?? [];
  const child = children.find(item => item.dependantId === dependantId);
  const typedLines = lines.map(line => ({ type: line.type, amount: Number(line.amount) }));
  const split = rules.coveredCosts ? splitLines(typedLines, rules.coveredCosts) : null;
  const claimAmount = split ? split.covered : Number(amount);
  const childBlocked = rules.claimFor === 'Child' && (children.length === 0 || (!!child && !child.withinChildLimit && !rules.extraChildByException));
  const maximum = rules.limitPerChild && child ? Math.min(child.remainingAmount, balance?.remainingAmount ?? child.remainingAmount) : balance?.remainingAmount;
  const upload = async (files: FileList | null) => {
    if (!files?.length) return;
    setError(null);
    if (receipts.length + files.length > 10) { setError(t('Attach up to 10 receipts.')); return; }
    const selected = Array.from(files);
    if (selected.some(file => file.size > 10 * 1024 * 1024 || !/\.(pdf|jpe?g|png)$/i.test(file.name))) { setError(t('Use PDF, JPG or PNG files up to 10 MB each.')); return; }
    setUploading(true);
    try {
      for (const file of selected) {
        const receipt = await benefitsApi.uploadReceipt(file, employeeId);
        setReceipts(current => [...current, receipt]);
      }
    } catch (err) { setError(benefitsErrorMessage(err, t('Could not upload the receipt. Uploaded files remain attached.'))); }
    finally { setUploading(false); }
  };
  const review = (event: React.FormEvent) => {
    event.preventDefault(); setError(null);
    if (rules.claimFor === 'Child' && !child) { setError(t('Choose the child this claim is for.')); return; }
    if (childBlocked) { setError(t('This policy covers up to {max} children.', { max: rules.maxChildren ?? 0 })); return; }
    if (split && split.covered <= 0) { setError(t('No covered cost is on this invoice, so there is nothing to claim. Covered costs: {list}.', { list: (rules.coveredCosts ?? []).map(type => t(COST_TYPE_LABELS[type as CostType] ?? type)).join(', ') })); return; }
    if (split && maximum != null && split.covered > maximum) { setError(t('The covered amount {amount} is more than the {available} available to claim.', { amount: format.money(split.covered, benefit.currency), available: format.money(maximum, benefit.currency) })); return; }
    if (!Number.isFinite(claimAmount) || claimAmount <= 0) { setError(t('Enter a claim amount greater than zero.')); return; }
    if (policy?.receiptRequired && receipts.length === 0) { setError(t('Attach the required receipt before continuing.')); return; }
    setReviewing(true);
  };
  const submit = async () => {
    setSaving(true); setError(null); setMissingRoute(false);
    try { onSubmitted(await benefitsApi.submitClaim({ enrollmentId: benefit.id, amount: claimAmount, expenseDate, invoiceReference: invoice.trim(), description: description.trim(), documentIds: receipts.map(receipt => receipt.id), ...educationClaimFields(rules, { dependantId, schoolName, schoolCountry, claimTerm, lines: typedLines }) }, employeeId == null)); }
    catch (err) {
      setError(benefitsErrorMessage(err, t('Could not submit the claim.')));
      setMissingRoute((err as { response?: { data?: { code?: string } } }).response?.data?.code === 'approval_route_not_configured');
    } finally { setSaving(false); }
  };
  return <Modal title={t('Claim {benefit}', { benefit: benefit.planName })} onClose={() => { if (!saving && !uploading) onClose(); }}>
    <div className="space-y-4"><FormError message={error} />
      {missingRoute && (canConfigure ? <Link href="/benefits#benefit-claim-approval" className="inline-block text-sm font-semibold text-sapphire">{t('Configure claim approvals')}</Link> : <p className="text-sm text-amber-700">{t('Ask an administrator to configure benefit claim approvals, then retry this claim.')}</p>)}
      {reviewing ? <>
        <dl className="grid grid-cols-2 gap-3 rounded-xl bg-slate-50 p-4 text-sm dark:bg-white/5"><div><dt className="text-xs text-slate-500">{t('Claim amount')}</dt><dd className="font-semibold">{format.money(claimAmount, benefit.currency)}</dd></div>
          {child && <div><dt className="text-xs text-slate-500">{t('Child')}</dt><dd className="break-words">{child.name}</dd></div>}
          {rules.claimTerms && <div><dt className="text-xs text-slate-500">{t('Claim term')}</dt><dd>{t(CLAIM_TERM_LABELS[claimTerm as ClaimTerm] ?? claimTerm)}</dd></div>}
          {rules.claimFor === 'Child' && <div><dt className="text-xs text-slate-500">{t('School')}</dt><dd className="break-words">{schoolName}</dd></div>}
          {rules.allowedCountries && <div><dt className="text-xs text-slate-500">{t('School country')}</dt><dd>{countryName(schoolCountry)}</dd></div>}
          {split && <div className="col-span-2"><CoveredSummary lines={typedLines} coveredCosts={rules.coveredCosts ?? []} currency={benefit.currency} /></div>}<div><dt className="text-xs text-slate-500">{t('Expense date')}</dt><dd>{format.date(expenseDate)}</dd></div><div className="col-span-2"><dt className="text-xs text-slate-500">{t('Invoice reference')}</dt><dd className="break-words">{invoice}</dd></div><div className="col-span-2"><dt className="text-xs text-slate-500">{t('Description')}</dt><dd className="whitespace-pre-wrap break-words">{description}</dd></div></dl>
        <ul className="space-y-1 text-xs">{receipts.map(receipt => <li className="break-all" key={receipt.id}>{receipt.fileName}</li>)}</ul>
        {child && !child.withinChildLimit && <p role="status" className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">{t('This claim goes to HR as an exception: the policy covers up to {max} children.', { max: rules.maxChildren ?? 0 })}</p>}
        <p className="text-xs text-slate-500">{t('Your claim will be sent for approval. Payment follows the payroll process after approval.')}</p>
        <div className="flex justify-end gap-2"><button type="button" className={SECONDARY} disabled={saving} onClick={() => setReviewing(false)}>{t('Back')}</button><button type="button" className={PRIMARY} disabled={saving} onClick={() => void submit()}>{saving ? t('Submitting…') : t('Submit claim')}</button></div>
      </> : <form className="space-y-4" onSubmit={review}>
        <ClaimBalance balance={balance} currency={benefit.currency} />
        <FormError message={balanceError} />
        {checkingBalance && <p role="status" className="text-xs text-slate-500">{t('Checking available balance…')}</p>}
        {policy?.instructions && <p className="whitespace-pre-wrap text-xs text-slate-600 dark:text-slate-300">{policy.instructions}</p>}
        {rules.claimFor === 'Child' && balance && <ChildPicker options={children} rules={rules} value={dependantId} onChange={setDependantId} currency={benefit.currency} />}
        <div className="grid gap-3 sm:grid-cols-2"><label className={LABEL}>{t('Expense date')}<input type="date" className={INPUT} required min={benefit.effectiveFrom} max={benefit.effectiveTo && benefit.effectiveTo < today() ? benefit.effectiveTo : today()} value={expenseDate} onChange={event => setExpenseDate(event.target.value)} /></label>
          {split ? <div className={LABEL}>{t('Claim amount ({currency})', { currency: benefit.currency })}<p className="mt-1 rounded-lg bg-slate-50 px-3 py-2 text-sm font-semibold text-slate-800 dark:bg-white/5 dark:text-slate-100" data-testid="claim-amount-covered">{format.money(split.covered, benefit.currency)}</p><span className="mt-1 block font-normal text-slate-500">{t('The sum of the covered invoice lines.')}</span></div>
            : <label className={LABEL}>{t('Claim amount ({currency})', { currency: benefit.currency })}<input type="number" className={INPUT} required min="0.01" max={maximum} step="0.01" value={amount} onChange={event => setAmount(event.target.value)} /></label>}
          {rules.claimTerms && <label className={LABEL}>{t('Claim term')}<select aria-label={t('Claim term')} className={INPUT} required value={claimTerm} onChange={event => setClaimTerm(event.target.value)}><option value="">{t('Choose a term…')}</option>{rules.claimTerms.map(term => <option key={term} value={term}>{t(CLAIM_TERM_LABELS[term as ClaimTerm] ?? term)}</option>)}</select></label>}</div>
        {education && rules.claimFor === 'Child' && <label className={LABEL}>{t('School name')}<input className={INPUT} required maxLength={200} value={schoolName} onChange={event => setSchoolName(event.target.value)} /></label>}
        {rules.allowedCountries && <label className={LABEL}>{t('School country')}<select aria-label={t('School country')} className={INPUT} required value={schoolCountry} onChange={event => setSchoolCountry(event.target.value)}><option value="">{t('Choose a country…')}</option>{rules.allowedCountries.map(code => <option key={code} value={code}>{countryName(code)}</option>)}</select>
          <span className="mt-1 block font-normal text-slate-500">{isGccSet(rules.allowedCountries) ? t('Only schools in GCC countries are covered.') : t('Only schools in these countries are covered.')}</span></label>}
        {rules.coveredCosts && <ClaimLinesEditor lines={lines} onChange={setLines} coveredCosts={rules.coveredCosts} currency={benefit.currency} />}
        {policy?.claimWindowDays && <p className="text-xs text-slate-500">{t('Submit within {days} days of the expense.', { days: policy.claimWindowDays })}</p>}
        <label className={LABEL}>{t('Invoice reference')}<input className={INPUT} required maxLength={120} value={invoice} onChange={event => setInvoice(event.target.value)} /></label>
        <label className={LABEL}>{t('What was this expense for?')}<textarea className={INPUT} required rows={2} maxLength={2000} value={description} onChange={event => setDescription(event.target.value)} /></label>
        <label className={LABEL}>{policy?.receiptLabel || t('Receipt or invoice')}{policy?.receiptRequired ? ` · ${t('Required')}` : ` · ${t('Optional')}`}<input className={`${INPUT} text-xs`} type="file" accept=".pdf,.jpg,.jpeg,.png" multiple disabled={uploading || receipts.length >= 10} onChange={event => { void upload(event.target.files); event.target.value = ''; }} /><span className="mt-1 block font-normal">{t('PDF, JPG or PNG · up to 10 MB each')}</span></label>
        {uploading && <p role="status" className="text-xs text-slate-500">{t('Uploading receipts…')}</p>}
        <ul className="space-y-2">{receipts.map(receipt => <li key={receipt.id} className="flex items-center justify-between gap-2 rounded-lg bg-slate-50 p-2 text-xs dark:bg-white/5"><span className="min-w-0 break-all">{receipt.fileName}</span><button type="button" className="shrink-0 p-1" disabled={uploading} aria-label={t('Remove {file}', { file: receipt.fileName })} onClick={() => setReceipts(current => current.filter(item => item.id !== receipt.id))}><X className="h-4 w-4" /></button></li>)}</ul>
        <div className="flex justify-end gap-2"><button type="button" className={SECONDARY} disabled={uploading} onClick={onClose}>{t('Cancel')}</button><button type="submit" className={PRIMARY} disabled={uploading || checkingBalance || !!balanceError || !balance?.canClaim || childBlocked}>{t('Review claim')}</button></div>
      </form>}
    </div>
  </Modal>;
}

function ClaimDetails({ claim, selfService = false, coveredCosts }: { claim: BenefitClaim; selfService?: boolean; coveredCosts?: readonly string[] | null }) {
  const { t } = useLocale(); const format = useFormat(); const [error, setError] = useState<string | null>(null); const [downloading, setDownloading] = useState<string | null>(null);
  const download = async (receipt: BenefitReceipt) => {
    setError(null); setDownloading(receipt.id);
    try {
      const blob = await benefitsApi.downloadReceipt(claim.id, receipt.id, selfService);
      const url = URL.createObjectURL(blob); const anchor = document.createElement('a'); anchor.href = url; anchor.download = receipt.fileName; anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (err) { setError(benefitsErrorMessage(err, t('Could not download the receipt.'))); }
    finally { setDownloading(null); }
  };
  return <div className="space-y-3 text-xs"><FormError message={error} /><ClaimEducationDetails claim={claim} coveredCosts={coveredCosts} /><dl className="grid grid-cols-2 gap-3"><div><dt className="text-slate-500">{t('Expense date')}</dt><dd>{format.date(claim.expenseDate)}</dd></div><div><dt className="text-slate-500">{t('Invoice reference')}</dt><dd className="break-words">{claim.invoiceReference}</dd></div><div className="col-span-2"><dt className="text-slate-500">{t('Description')}</dt><dd className="whitespace-pre-wrap break-words">{claim.description}</dd></div></dl>
    <div className="flex flex-wrap gap-2">{claim.receipts.map(receipt => <button key={receipt.id} type="button" className={`${SECONDARY} max-w-full`} disabled={downloading !== null} onClick={() => void download(receipt)}><Download className="h-3.5 w-3.5 shrink-0" /><span className="min-w-0 break-all text-start">{receipt.fileName}</span></button>)}</div>
  </div>;
}

export function BenefitClaimsPanel({ employeeId, benefits, canWrite, onChanged }: { employeeId?: number; benefits: ClaimBenefit[]; canWrite: boolean; onChanged: () => void }) {
  const toast = useAppToast();
  const { t } = useLocale(); const format = useFormat();
  const [claims, setClaims] = useState<BenefitClaim[]>([]); const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null); const [claiming, setClaiming] = useState<ClaimBenefit | null>(null);
  const [success, setSuccess] = useState(false); const [withdrawing, setWithdrawing] = useState<string | null>(null);
  const load = useCallback(async () => { setLoading(true); setError(null); try { setClaims(await benefitsApi.claims(employeeId)); } catch (err) { setError(benefitsErrorMessage(err, t('Could not load benefit claims.'))); } finally { setLoading(false); } }, [employeeId, t]);
  useEffect(() => { void load(); }, [load]);
  const withdraw = async (id: string) => { setWithdrawing(id); setError(null); try { await benefitsApi.withdrawClaim(id, employeeId == null); await load(); onChanged(); } catch (err) { setError(benefitsErrorMessage(err, t('Could not withdraw the claim.'))); } finally { setWithdrawing(null); } };
  const reimbursable = benefits.filter(item => item.paymentPolicy?.delivery === 'Reimbursement');
  return <section className={`${CARD} space-y-3 p-4`} aria-label={t('Benefit claims')} data-testid="benefit-claims">
    <div><h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800 dark:text-slate-100"><FileText className="h-4 w-4" />{t('Benefit claims')}</h3><p className="mt-1 text-xs text-slate-500">{t('Track approval, payroll inclusion and payment here.')}</p></div>
    <FormError message={error} />{success && <p role="status" className="text-xs text-emerald-700 dark:text-emerald-300">{t('Claim submitted for approval.')}</p>}
    {reimbursable.length > 0 && <div className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,18rem),1fr))] gap-3">{reimbursable.map(benefit => <div key={benefit.id} className="min-w-0 rounded-xl border border-slate-200 p-3 dark:border-white/10"><p className="text-sm font-semibold">{benefit.planName}</p><ClaimBalance balance={benefit.claimBalance} currency={benefit.currency} />{canWrite && benefit.status === 'Active' && benefit.effectiveFrom <= today() && <button type="button" className={`${PRIMARY} mt-3`} onClick={() => { setSuccess(false); setClaiming(benefit); }}><Plus className="h-3.5 w-3.5" />{t('Submit a claim')}</button>}{!benefit.claimBalance?.canClaim && <p className="mt-2 text-xs text-slate-500">{t('No claim is available for the current period. Choose an expense date to check another period, or contact HR.')}</p>}</div>)}</div>}
    {loading ? <p role="status" className="text-xs text-slate-500">{t('Loading claims…')}</p> : claims.length === 0 ? <p className="text-xs text-slate-500">{t('No claims submitted yet.')}</p> : <ul className="space-y-2">{claims.map(claim => <li key={claim.id} className="rounded-xl border border-slate-200 p-3 dark:border-white/10"><details><summary className="flex cursor-pointer list-none flex-wrap items-center justify-between gap-2"><span><span className="block text-sm font-semibold">{claim.planName}</span><span className="text-xs text-slate-500">{format.date(claim.expenseDate)} · {claim.invoiceReference}</span></span><span className="text-end"><strong className="block text-sm">{format.money(claim.amount, claim.currency)}</strong><span className="text-xs text-slate-500">{t(STATUS_LABELS[claim.settlementStatus] ?? claim.settlementStatus)}</span></span></summary><div className="mt-3"><ClaimDetails claim={claim} selfService={employeeId == null} coveredCosts={benefits.find(item => item.id === claim.enrollmentId)?.paymentPolicy?.coveredCosts} />{canWrite && claim.canWithdraw && <button type="button" className={`${SECONDARY} mt-3`} disabled={withdrawing !== null} onClick={() => void withdraw(claim.id)}>{withdrawing === claim.id ? t('Withdrawing…') : t('Withdraw claim')}</button>}</div></details></li>)}</ul>}
    {error && <button type="button" className={SECONDARY} onClick={() => void load()}>{t('Retry')}</button>}
    {claiming && <BenefitClaimForm benefit={claiming} employeeId={employeeId} onClose={() => setClaiming(null)} onSubmitted={() => { toast.success(t('Claim submitted for approval.')); setClaiming(null); setSuccess(true); void load(); onChanged(); }} />}
  </section>;
}

export function BenefitClaimApprovalCard({ request }: ApprovalDetailRendererProps) {
  const { t } = useLocale(); const format = useFormat(); const [claim, setClaim] = useState<BenefitClaim | null>(null); const [error, setError] = useState<string | null>(null);
  useEffect(() => { let live = true; setError(null); setClaim(null); benefitsApi.claim(request.entityId || request.id).then(value => { if (live) setClaim(value); }).catch(err => { if (live) setError(benefitsErrorMessage(err, t('Could not load the benefit claim.'))); }); return () => { live = false; }; }, [request.entityId, request.id, t]);
  if (error) return <FormError message={error} />;
  if (!claim) return <p role="status" className="text-sm">{t('Loading claim…')}</p>;
  return <section className={`${CARD} space-y-3 p-4`}><div className="flex flex-wrap justify-between gap-2"><div><h3 className="font-semibold">{claim.planName}</h3><p className="text-xs text-slate-500">{claim.employeeName}</p></div><div className="text-end"><strong>{format.money(claim.amount, claim.currency)}</strong><p className="text-xs text-slate-500">{t(STATUS_LABELS[claim.settlementStatus])}</p></div></div><ClaimDetails claim={claim} /></section>;
}
