'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import {
  successionApi, type AvailablePosition, type CriticalPosition, type ImpactLevel, type PersonRef, type RiskLevel,
} from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { IMPACTS, RISKS, impactLabel, longEnough, riskLabel, sortGapsFirst, successionError } from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, FormError, GapChips, LoadingRows, PersonName, PersonPicker } from './SuccessionParts';

/**
 * Critical positions, gaps first (SUC-01, SUC-05). Each row says what is missing in words (no approved slate, no
 * Ready-now successor, vacant …) and opens the position. HR with positions.manage designates new ones here.
 */
export function PositionsPanel({ canManage, onChanged }: { canManage: boolean; onChanged?: () => void }) {
  const { t } = useLocale();
  const [rows, setRows] = useState<CriticalPosition[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [designating, setDesignating] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try { setRows(sortGapsFirst(await successionApi.positions('Active'))); } catch (e) { setError(successionError(e, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <div className="space-y-3" data-testid="suc-positions">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.positions.intro')}</p>
        {canManage && (
          <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-designate-open" onClick={() => setDesignating(true)}>
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.positions.designate')}
          </button>
        )}
      </div>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !rows && <LoadingRows label={t('suc.positions.loading')} />}
      {rows && rows.length === 0 && (
        <EmptyBlock title={t('suc.positions.emptyTitle')} body={canManage ? t('suc.positions.emptyBodyManage') : t('suc.positions.emptyBody')} />
      )}
      {rows && rows.length > 0 && <PositionList rows={rows} />}
      {designating && <DesignateDialog onClose={() => setDesignating(false)} onDone={() => { setDesignating(false); void load(); onChanged?.(); }} />}
    </div>
  );
}

/** A list of critical positions as links — also used for "positions you are accountable for". */
export function PositionList({ rows, rowTestId = 'suc-position-row' }: { rows: CriticalPosition[]; rowTestId?: string }) {
  const { t } = useLocale();
  return (
    <ul className="space-y-2" aria-label={t('suc.positions.listLabel')}>
      {rows.map((p) => (
        <li key={p.id}>
          <Link href={`/performance/succession/positions/${p.id}`} data-testid={rowTestId} data-position-id={p.id}
            className="grid gap-2 rounded-xl border border-slate-200 bg-white p-3 transition hover:border-sapphire/60 dark:border-white/10 dark:bg-white/[0.03] md:grid-cols-[minmax(0,2fr)_minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1.6fr)] md:items-center">
            <span className="min-w-0">
              <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white">{p.positionTitle}</span>
              <span className="block truncate text-xs text-slate-500">{[p.positionCode, p.companyName, p.departmentName].filter(Boolean).join(' · ')}</span>
            </span>
            <span className="text-xs">
              {p.vacant
                ? <StatusChip label={t('suc.position.vacant')} tone="rose" />
                : <><span className="text-slate-500">{t('suc.position.incumbent')} </span><PersonName person={p.incumbent} /></>}
            </span>
            <span className="text-xs text-slate-600 dark:text-slate-300">
              {t('suc.position.riskImpact', { risk: t(riskLabel[p.riskOfLoss]), impact: t(impactLabel[p.impactOfVacancy]) })}
              {p.readyNow !== null && <span className="block text-slate-500">{t('suc.position.bench', { ready: p.readyNow, soon: p.oneToTwoYears ?? 0, later: p.threePlusYears ?? 0 })}</span>}
            </span>
            <GapChips gaps={p.gaps} emptyLabel={t('suc.gap.none')} />
          </Link>
        </li>
      ))}
    </ul>
  );
}

function DesignateDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [options, setOptions] = useState<AvailablePosition[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [positionId, setPositionId] = useState('');
  const [risk, setRisk] = useState<RiskLevel>('Medium');
  const [impact, setImpact] = useState<ImpactLevel>('High');
  const [rationale, setRationale] = useState('');
  const [accountable, setAccountable] = useState<PersonRef | null>(null);
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try { setOptions(await successionApi.available()); } catch (e) { setLoadError(successionError(e, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  const positionProblem = !positionId ? t('suc.designate.choosePosition') : null;
  const rationaleProblem = longEnough(rationale, 10) ? null : t('suc.check.min10');
  const chosen = options?.find((o) => o.id === positionId);

  const submit = async () => {
    setTouched(true);
    if (positionProblem || rationaleProblem) return;
    setBusy(true);
    setError(null);
    try {
      await successionApi.designate({ positionId, riskOfLoss: risk, impactOfVacancy: impact, rationale: rationale.trim(), accountableEmployeeId: accountable?.employeeId ?? null });
      toast.success(t('suc.designate.done'));
      onDone();
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={t('suc.designate.title')} onClose={onClose} size="lg" footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-designate-submit" disabled={busy || !options} onClick={() => void submit()}>
          {busy ? t('suc.saving') : t('suc.designate.submit')}
        </button>
      </>
    )}>
      <div className="space-y-3" data-testid="suc-designate-form">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.designate.intro')}</p>
        {loadError && <ErrorBlock message={loadError} onRetry={() => void load()} />}
        <Field id="suc-designate-position" label={t('suc.designate.position')} error={touched ? positionProblem : null}
          hint={options && options.length === 0 ? t('suc.designate.noneAvailable') : undefined}>
          <select id="suc-designate-position" data-testid="suc-designate-position" className="select w-full" value={positionId}
            onChange={(e) => setPositionId(e.target.value)} disabled={!options}>
            <option value="">{options ? t('suc.designate.pick') : t('suc.loading')}</option>
            {options?.map((o) => <option key={o.id} value={o.id}>{`${o.title} (${o.code})${o.companyName ? ` · ${o.companyName}` : ''}`}</option>)}
          </select>
        </Field>
        {chosen && (
          <p className="text-xs text-slate-600 dark:text-slate-300">
            {chosen.incumbent ? <>{t('suc.position.incumbent')} <PersonName person={chosen.incumbent} /></> : t('suc.designate.vacantNow')}
          </p>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field id="suc-designate-risk" label={t('suc.designate.risk')} hint={t('suc.designate.riskHint')}>
            <select id="suc-designate-risk" data-testid="suc-designate-risk" className="select w-full" value={risk} onChange={(e) => setRisk(e.target.value as RiskLevel)}>
              {RISKS.map((r) => <option key={r} value={r}>{t(riskLabel[r])}</option>)}
            </select>
          </Field>
          <Field id="suc-designate-impact" label={t('suc.designate.impact')} hint={t('suc.designate.impactHint')}>
            <select id="suc-designate-impact" data-testid="suc-designate-impact" className="select w-full" value={impact} onChange={(e) => setImpact(e.target.value as ImpactLevel)}>
              {IMPACTS.map((r) => <option key={r} value={r}>{t(impactLabel[r])}</option>)}
            </select>
          </Field>
        </div>
        <Field id="suc-designate-rationale" label={t('suc.designate.rationale')} hint={t('suc.designate.rationaleHint')} error={touched ? rationaleProblem : null}>
          <textarea id="suc-designate-rationale" data-testid="suc-designate-rationale" className="input min-h-[80px] w-full" value={rationale}
            onChange={(e) => setRationale(e.target.value)} />
        </Field>
        <PersonPicker id="suc-designate-accountable" testId="suc-designate-accountable" value={accountable} onChange={setAccountable}
          label={t('suc.designate.accountable')} hint={t('suc.designate.accountableHint')} optional />
        <FormError message={error} />
      </div>
    </Modal>
  );
}
