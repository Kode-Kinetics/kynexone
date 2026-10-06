'use client';

import { useCallback, useEffect, useState } from 'react';
import { notifyApiError } from '../../api/client';
import { renewalsApi, type ContractChain, type NationalityClass } from '../../api/renewals';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { EnumLabel } from '../EnumLabel';
import { actionKeys, blockText, fill, formatDay, gapReasonKeys, linkKindKeys } from '../../lib/renewalRadar';
import { Modal } from '../Modal';

/** One meter row: used of max, filled in proportion, red once the limit is reached. */
function Meter({ label, used, max, text }: { label: string; used: number | null; max: number; text: string }) {
  const pct = used == null ? 0 : Math.min(100, Math.round((used / max) * 100));
  const reached = used != null && used >= max;
  return (
    <div>
      <div className="flex justify-between text-xs text-slate-600 dark:text-slate-300">
        <span>{label}</span>
        <span className="font-semibold">{used == null ? '—' : text}</span>
      </div>
      <div className="mt-1 h-2 rounded-full bg-slate-100 dark:bg-white/10" role="meter" aria-label={label}
        aria-valuemin={0} aria-valuemax={max} aria-valuenow={used ?? 0}>
        <div className={`h-2 rounded-full ${reached ? 'bg-rose-500' : 'bg-sapphire'}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

/**
 * The contract chain drawer: every term on file with how it links to the one before, the Art. 55 meter, what can be
 * done at renewal, and — for HR with contracts.renewal.manage — the form that records the history when it could not
 * be linked from the records (T2). The renewal is anchored on the term's START date; the signing date is shown beside it.
 */
export function ChainDrawer({ contractId, canManage, onClose, onChanged }: {
  contractId: string | null; canManage: boolean; onClose: () => void; onChanged: () => void;
}) {
  const { t, locale } = useLocale();
  const f = useFormat();
  const [chain, setChain] = useState<ContractChain | null>(null);
  const [failed, setFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState({ renewedFrom: '', chainStartedOn: '', nationality: '' as '' | NationalityClass, renewalNumber: '0', noticeDays: '', autoRenew: true });

  const load = useCallback(async () => {
    if (!contractId) return;
    setFailed(false);
    try {
      const c = await renewalsApi.chain(contractId);
      setChain(c);
      const current = c.terms.find((x) => x.isCurrent);
      setForm({
        renewedFrom: '',
        chainStartedOn: c.chainStartedOn ?? current?.startDate ?? '',
        nationality: c.nationalityClass ?? '',
        renewalNumber: String(c.renewalNumber ?? 0),
        noticeDays: c.nonRenewalNoticeDays == null ? '' : String(c.nonRenewalNoticeDays),
        autoRenew: c.autoRenew,
      });
    } catch {
      setFailed(true);
    }
  }, [contractId]);

  useEffect(() => { setChain(null); void load(); }, [load]);

  const current = chain?.terms.find((x) => x.isCurrent);
  const earlier = chain?.terms.filter((x) => current && x.startDate < current.startDate) ?? [];
  const mayConfirm = canManage && chain != null && (!chain.confirmed || chain.nationalityClass == null || chain.caseState === 'NeedsConfirmation');

  const save = async () => {
    if (!contractId || !form.nationality || !form.chainStartedOn) return;
    setSaving(true);
    try {
      const updated = await renewalsApi.confirmChain(contractId, {
        renewedFromContractId: form.renewedFrom || null,
        chainStartedOn: form.chainStartedOn,
        workerNationalityClass: form.nationality,
        autoRenew: form.autoRenew,
        nonRenewalNoticeDays: form.noticeDays === '' ? null : Number(form.noticeDays),
        renewalNumber: Number(form.renewalNumber),
      });
      setChain(updated);
      onChanged();
    } catch (err) {
      notifyApiError(err, t('The contract history could not be saved.'));
    } finally {
      setSaving(false);
    }
  };

  const input = 'w-full rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-sm dark:border-white/10 dark:bg-white/5';
  return (
    <Modal isOpen={contractId != null} onClose={onClose} title={t('Contract history')} size="lg">
      {failed ? (
        <div className="space-y-3 text-sm text-slate-600 dark:text-slate-300">
          <p>{t('The contract history could not be loaded.')}</p>
          <button type="button" onClick={() => void load()} className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white">{t('Try again')}</button>
        </div>
      ) : !chain ? (
        <div className="h-32 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : (
        <div className="space-y-5 text-sm">
          <section className="grid gap-3 rounded-xl bg-slate-50 p-3 dark:bg-white/[0.03] md:grid-cols-2">
            {chain.nationalityClass === 'Saudi' ? (
              <>
                <Meter label={t('Renewals used (Art. 55)')} used={chain.art55.renewalsUsed} max={chain.art55.maxRenewals}
                  text={fill(t('{used} of {max} renewals'), { used: chain.art55.renewalsUsed ?? 0, max: chain.art55.maxRenewals })} />
                <Meter label={t('Years in the chain (Art. 55)')} used={chain.art55.yearsServed} max={chain.art55.maxYears}
                  text={fill(t('{used} of {max} years'), { used: chain.art55.yearsServed ?? 0, max: chain.art55.maxYears })} />
                {chain.art55.thresholdReached && (
                  <p className="md:col-span-2 text-xs font-medium text-rose-700 dark:text-rose-300">
                    {t('At the limit: at renewal this contract can only become indefinite or not be renewed.')}
                  </p>
                )}
              </>
            ) : chain.nationalityClass === 'NonSaudi' ? (
              <p className="md:col-span-2 text-xs text-slate-600 dark:text-slate-300">{t('Non-Saudi: fixed-term only (Art. 37)')}</p>
            ) : (
              <p className="md:col-span-2 text-xs text-amber-700 dark:text-amber-300">{t('Saudi or non-Saudi is not confirmed')}</p>
            )}
            <div className="md:col-span-2 text-xs text-slate-600 dark:text-slate-300">
              <span className="font-semibold">{t('At renewal')}: </span>
              {chain.nextAllowedActions.length > 0
                ? chain.nextAllowedActions.map((a) => t(actionKeys[a] ?? a)).join(' · ')
                : t('No option until the history is confirmed')}
              {chain.opensOn && (
                <span className="block text-slate-500">{fill(t('The renewal review opens on {date}.'), { date: formatDay(chain.opensOn, f) })}</span>
              )}
            </div>
            {chain.blockReasons.map((r) => {
              const text = blockText(r, locale);
              return (
                <div key={r.code} className="md:col-span-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs dark:border-amber-400/20 dark:bg-amber-400/10">
                  <span className="font-semibold">{text.title}</span>
                  <span className="block">{text.why}</span>
                  <span className="block text-slate-600 dark:text-slate-300">{text.fix}</span>
                </div>
              );
            })}
          </section>

          <section aria-label={t('Contracts on file')}>
            <table className="w-full text-xs">
              <thead>
                <tr className="border-b border-slate-200 text-slate-500 dark:border-white/10 dark:text-slate-400">
                  <th className="p-2 text-start">{t('Contract')}</th>
                  <th className="p-2 text-start">{t('Starts (renewal anchor)')}</th>
                  <th className="p-2 text-start">{t('Ends')}</th>
                  <th className="p-2 text-start">{t('Signed')}</th>
                  <th className="p-2 text-start">{t('Link')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {chain.terms.map((term) => (
                  <tr key={term.contractId} className={term.isCurrent ? 'bg-sapphire/5' : ''}>
                    <td className="p-2 font-mono">{term.contractNumber}<span className="block font-sans text-slate-500"><EnumLabel enum="Status" value={term.status} /></span></td>
                    <td className="p-2">{formatDay(term.startDate, f, '0000')}</td>
                    <td className="p-2">{term.endDate ? formatDay(term.endDate, f, '0000') : t('Indefinite')}</td>
                    <td className="p-2">{formatDay(term.signedOn, f, '0000')}</td>
                    <td className="p-2">
                      {t(linkKindKeys[term.linkKind] ?? term.linkKind)}
                      {term.renewalNumber != null && term.linkKind !== 'Unconfirmed' && <span className="text-slate-500"> · #{term.renewalNumber}</span>}
                      {term.gapReason && <span className="block text-amber-700 dark:text-amber-300">{t(gapReasonKeys[term.gapReason] ?? term.gapReason)}</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          {mayConfirm && (
            <section aria-labelledby="confirm-history" className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10">
              <h3 id="confirm-history" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Confirm the contract history')}</h3>
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Record what the signed contracts show. Nothing is counted until you confirm it.')}</p>
              <div className="grid gap-3 md:grid-cols-2">
                <label className="text-xs text-slate-600 dark:text-slate-300">{t('Saudi or non-Saudi')}
                  <select className={input} value={form.nationality} onChange={(e) => setForm({ ...form, nationality: e.target.value as NationalityClass })}>
                    <option value="">{t('Choose')}</option>
                    <option value="Saudi">{t('Saudi')}</option>
                    <option value="NonSaudi">{t('Non-Saudi')}</option>
                  </select>
                </label>
                <label className="text-xs text-slate-600 dark:text-slate-300">{t('First contract started on')}
                  <input type="date" className={input} value={form.chainStartedOn} max={current?.startDate}
                    onChange={(e) => setForm({ ...form, chainStartedOn: e.target.value })} />
                </label>
                <label className="text-xs text-slate-600 dark:text-slate-300">{t('Renewals before this contract')}
                  <input type="number" min={0} className={input} value={form.renewalNumber}
                    onChange={(e) => setForm({ ...form, renewalNumber: e.target.value })} />
                </label>
                <label className="text-xs text-slate-600 dark:text-slate-300">{t('Renews this earlier contract (if on file)')}
                  <select className={input} value={form.renewedFrom} onChange={(e) => setForm({ ...form, renewedFrom: e.target.value })}>
                    <option value="">{t('None on file')}</option>
                    {earlier.map((e) => <option key={e.contractId} value={e.contractId}>{e.contractNumber}</option>)}
                  </select>
                </label>
                <label className="text-xs text-slate-600 dark:text-slate-300">{t('Non-renewal notice in the contract (days)')}
                  <input type="number" min={1} max={365} className={input} value={form.noticeDays} placeholder={t('Statutory default')}
                    onChange={(e) => setForm({ ...form, noticeDays: e.target.value })} />
                </label>
                <label className="flex items-center gap-2 self-end text-xs text-slate-600 dark:text-slate-300">
                  <input type="checkbox" checked={form.autoRenew} onChange={(e) => setForm({ ...form, autoRenew: e.target.checked })} />
                  {t('Renews automatically unless notice is served')}
                </label>
              </div>
              <button type="button" disabled={saving || !form.nationality || !form.chainStartedOn} onClick={() => void save()}
                className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50">
                {saving ? t('Saving…') : t('Confirm history')}
              </button>
            </section>
          )}
        </div>
      )}
    </Modal>
  );
}
