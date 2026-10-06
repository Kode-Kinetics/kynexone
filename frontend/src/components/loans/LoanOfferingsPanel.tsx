'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { loanOfferingsApi, type LoanTypeOffering } from '../../api/loanGovernance';
import { useLocale } from '../../contexts/LocaleContext';
import { loanErrorMessage } from '../../lib/loanWorkflow';
import { fillTemplate, localName } from '../../lib/gradeLoanLimits';

const sourceKeys: Record<LoanTypeOffering['source'], string> = {
  CompanyPolicy: "Offered under this company's own policy.",
  GroupPolicy: 'Offered under the group-wide policy.',
  LoanTypeBaseline: 'Offered on the loan type limits (no policy published yet).',
  CompanyNotOffered: 'Switched off for this company.',
  NoPolicy: 'Not offered: limited by grade and no policy is published for this company yet.',
};

/**
 * The company's explicit "we offer / don't offer this loan type" switch, one row per loan type. Switching
 * publishes a new policy version that copies the terms in force, so no other rule changes; employees of the
 * company then see the type as not offered.
 */
export function LoanOfferingsPanel({ companyId, companyName }: { companyId: string; companyName: string }) {
  const { t, locale } = useLocale();
  const [rows, setRows] = useState<LoanTypeOffering[]>([]);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const seq = useRef(0);

  const load = useCallback(async () => {
    const mine = ++seq.current;
    if (!companyId) { setRows([]); return; }
    try {
      const list = await loanOfferingsApi.list(companyId);
      // Anything but a list is a failed load, never "no loan types": an empty list would read as a fact.
      if (!Array.isArray(list)) throw new Error('Unexpected offerings response');
      if (mine === seq.current) { setRows(list); setError(''); }
    }
    catch (e) { if (mine === seq.current) setError(loanErrorMessage(e, t('Unable to load which loan types this company offers.'))); }
  }, [companyId, t]);
  useEffect(() => { setError(''); void load(); }, [load]);

  const toggle = async (row: LoanTypeOffering, offered: boolean) => {
    setBusy(row.loanTypeId); setError('');
    setNotice('');
    try {
      const result = await loanOfferingsApi.set({ companyId, loanTypeId: row.loanTypeId, offered });
      if (result?.detachedFromGroupPolicy) setNotice(t('This company now has its own policy; group changes no longer apply.'));
      await load();
    }
    catch (e) { setError(loanErrorMessage(e, t('Unable to change whether this loan type is offered.'))); }
    finally { setBusy(''); }
  };

  if (!companyId) return null;
  return <section className="surface space-y-3 p-4" aria-labelledby="loan-offerings-heading">
    <div>
      <h2 id="loan-offerings-heading" className="font-semibold">{t('Loan types offered')}</h2>
      <p className="text-sm text-slate-500">{fillTemplate(t('Choose which loan types employees of {company} can apply for. Switching one off keeps every other policy rule as it is.'), { company: companyName })}</p>
    </div>
    <p className="text-xs text-slate-500">{t('Requests already submitted can still be approved after you switch a type off.')}</p>
    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {notice && <p role="status" className="text-sm text-amber-700 dark:text-amber-300">{notice}</p>}
    {error && rows.length === 0 ? null : rows.length === 0 ? <p className="text-sm text-slate-500">{t('No loan types yet')}</p>
      : <ul className="divide-y divide-slate-100 dark:divide-white/10">{rows.map(row => <li key={row.loanTypeId} className="flex flex-wrap items-center justify-between gap-2 py-2">
        <div>
          <p className="text-sm font-medium">{localName(locale, row.nameEn, row.nameAr)}</p>
          <p className="text-xs text-slate-500">{t(sourceKeys[row.source] ?? '')}</p>
          {row.detachedFromGroupPolicy && <p className="text-xs text-amber-700 dark:text-amber-300">{t('This company now has its own policy; group changes no longer apply.')}</p>}
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={row.offered} disabled={busy === row.loanTypeId || (!row.offered && row.source === 'NoPolicy')}
            onChange={e => void toggle(row, e.target.checked)} />
          {t(row.offered ? 'Offered' : 'Not offered')}
        </label>
      </li>)}</ul>}
  </section>;
}
