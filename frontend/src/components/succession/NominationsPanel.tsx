'use client';

import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { successionApi, type MyNomination, type NominationTarget, type PersonRef, type Readiness } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { READINESS, longEnough, maskedStageLabel, readinessLabel, successionError } from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, FormError, LoadingRows, PersonName, PersonPicker } from './SuccessionParts';

/**
 * Nominate someone (succession.nominate) to a critical position or a talent pool, with evidence. A manager's picker
 * only offers their own reporting line (the server's data scope); HR's offers their companies.
 * With `target` fixed (the position page), the target picker is not shown.
 */
export function NominateDialog({ target, onClose, onDone }: {
  target?: { kind: 'position' | 'pool'; id: string; title: string } | null; onClose: () => void; onDone: () => void;
}) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [targets, setTargets] = useState<NominationTarget[] | null>(target ? [] : null);
  const [targetKey, setTargetKey] = useState(target ? `${target.kind}:${target.id}` : '');
  const [person, setPerson] = useState<PersonRef | null>(null);
  const [readiness, setReadiness] = useState<Readiness | ''>('');
  const [evidence, setEvidence] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (target) return;
    successionApi.targets().then(setTargets).catch((e) => setError(successionError(e, t)));
  }, [target, t]);

  const [kind, id] = targetKey.split(':');
  const problems = {
    target: targetKey ? null : t('suc.nominate.chooseTarget'),
    person: person ? null : t('suc.check.choosePerson'),
    evidence: longEnough(evidence, 10) ? null : t('suc.check.min10'),
  };

  const submit = async () => {
    setTouched(true);
    if (problems.target || problems.person || problems.evidence) return;
    setBusy(true);
    setError(null);
    try {
      await successionApi.nominate({
        employeeId: person!.employeeId,
        criticalPositionId: kind === 'position' ? id : null,
        talentPoolId: kind === 'pool' ? id : null,
        readiness: readiness || null,
        evidence: evidence.trim(),
      });
      toast.success(t('suc.nominate.done'));
      onDone();
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={target ? t('suc.nominate.titleFor', { target: target.title }) : t('suc.nominate.title')} onClose={onClose} size="lg" footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-nominate-submit" disabled={busy} onClick={() => void submit()}>
          {busy ? t('suc.saving') : t('suc.nominate.submit')}
        </button>
      </>
    )}>
      <div className="space-y-3" data-testid="suc-nominate-form">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.nominate.intro')}</p>
        {!target && (
          <Field id="suc-nominate-target" label={t('suc.nominate.target')} error={touched ? problems.target : null}>
            <select id="suc-nominate-target" data-testid="suc-nominate-target" className="select w-full" value={targetKey} onChange={(e) => setTargetKey(e.target.value)} disabled={!targets}>
              <option value="">{targets ? t('suc.nominate.pickTarget') : t('suc.loading')}</option>
              {targets?.map((x) => (
                <option key={`${x.kind}:${x.id}`} value={`${x.kind}:${x.id}`}>
                  {`${x.kind === 'pool' ? t('suc.nominate.kindPool') : t('suc.nominate.kindPosition')}: ${(locale === 'ar' && x.titleAr) || x.title}`}
                </option>
              ))}
            </select>
          </Field>
        )}
        <PersonPicker id="suc-nominate-employee" testId="suc-nominate-employee" value={person} onChange={setPerson}
          label={t('suc.nominate.employee')} hint={t('suc.nominate.employeeHint')} error={touched ? problems.person : null} />
        <Field id="suc-nominate-readiness" label={t('suc.nominate.readiness')} hint={t('suc.nominate.readinessHint')} optional>
          <select id="suc-nominate-readiness" data-testid="suc-nominate-readiness" className="select w-full" value={readiness} onChange={(e) => setReadiness(e.target.value as Readiness | '')}>
            <option value="">{t('suc.nominate.readinessNone')}</option>
            {READINESS.map((r) => <option key={r} value={r}>{t(readinessLabel[r])}</option>)}
          </select>
        </Field>
        <Field id="suc-nominate-evidence" label={t('suc.nominate.evidence')} hint={t('suc.nominate.evidenceHint')} error={touched ? problems.evidence : null}>
          <textarea id="suc-nominate-evidence" data-testid="suc-nominate-evidence" className="input min-h-[96px] w-full" value={evidence} onChange={(e) => setEvidence(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}

/**
 * My nominations: what the caller nominated, with the stage masked — "Proposed", "Being considered" or "Closed".
 * A nominator never learns whether their person is on a slate.
 */
export function MyNominationsPanel() {
  const { t } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [rows, setRows] = useState<MyNomination[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nominating, setNominating] = useState(false);
  const [withdrawing, setWithdrawing] = useState<MyNomination | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try { setRows(await successionApi.myNominations()); } catch (e) { setError(successionError(e, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <div className="space-y-3" data-testid="suc-my-nominations">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.mine.intro')}</p>
        <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-nominate-open" onClick={() => setNominating(true)}>
          <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.nominate.open')}
        </button>
      </div>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !rows && <LoadingRows label={t('suc.loading')} />}
      {rows?.length === 0 && <EmptyBlock title={t('suc.mine.emptyTitle')} body={t('suc.mine.emptyBody')} />}
      {rows && rows.length > 0 && (
        <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
          {rows.map((n) => (
            <li key={n.id} className="flex flex-wrap items-center justify-between gap-2 p-3 text-sm" data-testid="suc-my-nomination" data-nomination-id={n.id}>
              <span className="min-w-0">
                <PersonName person={n.candidate} />
                <span className="block text-xs text-slate-500">
                  {t('suc.mine.for', { target: n.targetTitle, date: fx.date(n.nominatedAtUtc) })}
                </span>
                {n.closedNote && <span className="block text-xs text-slate-500">{t('suc.mine.closedNote', { note: n.closedNote })}</span>}
              </span>
              <span className="flex items-center gap-2">
                <StatusChip label={t(maskedStageLabel[n.stage])} tone={n.stage === 'Closed' ? 'slate' : n.stage === 'InReview' ? 'blue' : 'amber'} />
                {n.canWithdraw && (
                  <button type="button" className="btn-secondary text-xs" onClick={() => { setWithdrawing(n); setReason(''); }}>{t('suc.mine.withdraw')}</button>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
      {nominating && <NominateDialog onClose={() => setNominating(false)} onDone={() => { setNominating(false); void load(); }} />}
      {withdrawing && (
        <Modal isOpen title={t('suc.mine.withdrawTitle')} onClose={() => setWithdrawing(null)} footer={(
          <>
            <button type="button" className="btn-secondary text-xs" onClick={() => setWithdrawing(null)}>{t('suc.cancel')}</button>
            <button type="button" className="btn-primary text-xs" disabled={busy || !longEnough(reason, 3)} onClick={async () => {
              setBusy(true);
              try { await successionApi.withdraw(withdrawing.id, reason.trim()); setWithdrawing(null); void load(); } catch (e) { toast.error(successionError(e, t)); } finally { setBusy(false); }
            }}>{t('suc.mine.withdraw')}</button>
          </>
        )}>
          <Field id="suc-withdraw-reason" label={t('suc.reason')}>
            <textarea id="suc-withdraw-reason" className="input min-h-[64px] w-full" value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
        </Modal>
      )}
    </div>
  );
}
