'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { successionApi, type HistoryEntry, type NineBox, type Nomination, type PositionDetail, type Readiness } from '../api/succession';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import { AssessDialog } from '../components/succession/CalibrationPanel';
import { NineBoxGrid } from '../components/succession/NineBoxGrid';
import { NominateDialog } from '../components/succession/NominationsPanel';
import { SlatePanel } from '../components/succession/SlatePanel';
import {
  EmptyBlock, ErrorBlock, Field, FormError, ForbiddenBlock, GapChips, LoadingRows, PersonName, RawCode, ReasonDialog, SectionCard, SuccessionGate,
  useSuccessionAccess,
} from '../components/succession/SuccessionParts';
import { useAppToast } from '../components/ui/AppToast';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import {
  READINESS, bandLabel, currentPeriod, historyActionLabel, impactLabel, longEnough, nineBoxCellLabel, nominationStatusLabel, nominationTone,
  personName, potentialLabel, readinessLabel, readinessTone, riskLabel, successionError, successionStatus,
} from '../lib/succession';

/**
 * One critical position (SUC-01..09): who holds it, how exposed it is, the candidates and their stage, the 9-box of
 * those candidates with every input, and the slate versions. The accountable executive reaches it read-only.
 */
export function SuccessionPositionPage({ id }: { id: string }) {
  return (
    <SuccessionGate>
      <PositionBody id={id} />
    </SuccessionGate>
  );
}

function PositionBody({ id }: { id: string }) {
  const { t } = useLocale();
  const access = useSuccessionAccess();
  const toast = useAppToast();
  const [detail, setDetail] = useState<PositionDetail | null>(null);
  const [error, setError] = useState<{ message: string; status?: number } | null>(null);
  const [nominating, setNominating] = useState(false);
  const [retiring, setRetiring] = useState(false);
  const [version, setVersion] = useState(0);

  const load = useCallback(async () => {
    setError(null);
    try { setDetail(await successionApi.position(id)); } catch (e) { setError({ message: successionError(e, t), status: successionStatus(e) }); }
  }, [id, t]);
  useEffect(() => { void load(); }, [load, version]);
  const refresh = () => setVersion((v) => v + 1);

  if (error?.status === 403) return <ForbiddenBlock />;
  if (error) return <ErrorBlock message={error.message} onRetry={() => void load()} />;
  if (!detail) return <LoadingRows label={t('suc.loading')} />;
  const p = detail.position;
  const via = detail.viaRelationship;
  const active = p.status === 'Active';

  return (
    <div className="space-y-4" data-testid="suc-position-detail" data-position-id={p.id} data-read-only={via ? 'true' : 'false'}>
      <nav aria-label={t('suc.breadcrumb')} className="text-xs">
        <Link href="/performance/succession" className="text-sapphire hover:underline dark:text-cyanAccent">{t('suc.title')}</Link>
      </nav>
      <header className="wg-card space-y-3 rounded-2xl p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{p.positionTitle}</h1>
            <p className="text-xs text-slate-500">{[p.positionCode, p.companyName, p.departmentName].filter(Boolean).join(' · ')}</p>
          </div>
          <div className="flex flex-wrap gap-2">
            {!via && access.nominate && active && (
              <button type="button" className="btn-primary text-xs" data-testid="suc-nominate-open" onClick={() => setNominating(true)}>{t('suc.nominate.open')}</button>
            )}
            {!via && access.positionsManage && active && (
              <button type="button" className="btn-secondary text-xs" data-testid="suc-position-retire" onClick={() => setRetiring(true)}>{t('suc.position.retire')}</button>
            )}
          </div>
        </div>
        {via && <p className="rounded-lg bg-sapphire/10 px-3 py-2 text-xs text-slate-700 dark:text-slate-200" data-testid="suc-read-only">{t('suc.position.viaRelationship')}</p>}
        {!active && <p className="rounded-lg bg-slate-100 px-3 py-2 text-xs text-slate-700 dark:bg-white/10 dark:text-slate-200">{t('suc.position.retiredBecause', { reason: p.retiredReason ?? '—' })}</p>}
        <dl className="grid gap-3 text-xs sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <dt className="text-slate-500">{t('suc.position.incumbent')}</dt>
            <dd className="mt-0.5">{p.vacant ? <StatusChip label={t('suc.position.vacant')} tone="rose" /> : <PersonName person={p.incumbent} />}</dd>
          </div>
          <div>
            <dt className="text-slate-500">{t('suc.position.accountable')}</dt>
            <dd className="mt-0.5"><PersonName person={p.accountable} fallback={t('suc.position.noAccountable')} /></dd>
          </div>
          <div>
            <dt className="text-slate-500">{t('suc.position.riskLabel')}</dt>
            <dd className="mt-0.5 font-semibold text-slate-800 dark:text-slate-100">{t('suc.position.riskImpact', { risk: t(riskLabel[p.riskOfLoss]), impact: t(impactLabel[p.impactOfVacancy]) })}</dd>
          </div>
          <div>
            <dt className="text-slate-500">{t('suc.position.gaps')}</dt>
            <dd className="mt-0.5"><GapChips gaps={p.gaps} emptyLabel={t('suc.gap.none')} /></dd>
          </div>
        </dl>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.position.rationale', { text: p.rationale })}</p>
        {p.readyNow !== null && <p className="text-xs text-slate-500">{t('suc.position.bench', { ready: p.readyNow, soon: p.oneToTwoYears ?? 0, later: p.threePlusYears ?? 0 })}</p>}
      </header>

      {access.slatesRead && !via && <CandidatesSection positionId={p.id} canManage={access.slatesManage && active} canAssess={access.assess} key={`c${version}`} onChanged={refresh} />}
      {access.slatesRead && !via && <NineBoxSection positionId={p.id} key={`n${version}`} />}
      {(access.slatesRead || via) && <SlatePanel position={p} canManage={access.slatesManage && active && !via} key={`s${version}`} />}
      {!access.slatesRead && !via && <p className="text-xs text-slate-500">{t('suc.position.namesNeedAccess')}</p>}

      {nominating && (
        <NominateDialog target={{ kind: 'position', id: p.id, title: p.positionTitle }} onClose={() => setNominating(false)}
          onDone={() => { setNominating(false); refresh(); }} />
      )}
      {retiring && (
        <ReasonDialog title={t('suc.position.retireTitle')} intro={t('suc.position.retireIntro')} confirmLabel={t('suc.position.retire')} minLength={3}
          testId="suc-position-retire" onClose={() => setRetiring(false)}
          onConfirm={async (reason) => { await successionApi.retirePosition(p.id, reason); setRetiring(false); toast.success(t('suc.position.retired')); refresh(); }} />
      )}
    </div>
  );
}

function CandidatesSection({ positionId, canManage, canAssess, onChanged }: { positionId: string; canManage: boolean; canAssess: boolean; onChanged: () => void }) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [rows, setRows] = useState<Nomination[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dialog, setDialog] = useState<{ kind: 'readiness' | 'decline' | 'history' | 'assess' | 'clear'; n: Nomination } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setRows(await successionApi.nominations({ positionId })); } catch (e) { setError(successionError(e, t)); }
  }, [positionId, t]);
  useEffect(() => { void load(); }, [load]);

  const live = rows?.filter((n) => n.status !== 'Declined' && n.status !== 'Withdrawn') ?? [];
  const closed = rows?.filter((n) => n.status === 'Declined' || n.status === 'Withdrawn') ?? [];
  const done = () => { setDialog(null); void load(); onChanged(); };

  const startReview = async (n: Nomination) => {
    setBusy(n.id);
    try { await successionApi.startReview(n.id); done(); } catch (e) { toast.error(successionError(e, t)); } finally { setBusy(null); }
  };

  const row = (n: Nomination, isClosed = false) => {
    const a = n.currentAssessment;
    return (
      <li key={n.id} className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-candidate-row" data-nomination-id={n.id}>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <PersonName person={n.candidate} />
            <p className="text-caption text-slate-500">{n.nominatedBy ? t('suc.candidate.nominatedBy', { name: personName(n.nominatedBy, locale) }) : t('suc.candidate.nominatedBySystem')}</p>
          </div>
          <span className="flex flex-wrap items-center gap-1">
            <StatusChip label={t(nominationStatusLabel[n.status])} tone={nominationTone(n.status)} />
            <RawCode testId="suc-candidate-stage" value={n.status} />
            {n.readiness
              ? <StatusChip label={t(readinessLabel[n.readiness])} tone={readinessTone(n.readiness)} />
              : <StatusChip label={t('suc.readiness.notSet')} tone="slate" />}
            {n.needsReview && <span data-testid="suc-candidate-needs-review"><StatusChip label={t('suc.candidate.needsReview')} tone="amber" /></span>}
            {n.overCommitted && <span data-testid="suc-candidate-overcommitted"><StatusChip label={t('suc.candidate.overCommitted', { count: n.readyNowOnApprovedSlates })} tone="amber" /></span>}
          </span>
        </div>
        {n.needsReview && n.reviewReason && <p className="text-xs text-amber-800 dark:text-amber-300">{t('suc.candidate.reviewReason', { reason: n.reviewReason })}</p>}
        {n.readinessReason && <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.candidate.readinessChanged', { from: n.readinessPrevious ? t(readinessLabel[n.readinessPrevious]) : '—', reason: n.readinessReason })}</p>}
        {isClosed && n.exitNote && <p className="text-xs text-slate-500">{t('suc.candidate.closedBecause', { reason: n.exitNote })}</p>}
        <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="suc-candidate-assessment">
          {a
            ? t('suc.candidate.assessment', {
              period: a.reviewPeriod,
              potential: t(potentialLabel[a.calibratedPotential ?? a.proposedPotential]),
              state: a.calibratedPotential != null ? t('suc.candidate.calibratedWord') : t('suc.candidate.proposedWord'),
              band: t(bandLabel[a.performanceBand]),
              cell: a.nineBoxCell ? t(nineBoxCellLabel[a.nineBoxCell] ?? a.nineBoxCell) : t('suc.ninebox.insufficient'),
            })
            : t('suc.candidate.noAssessment')}
        </p>
        <details className="text-xs">
          <summary className="cursor-pointer font-semibold text-slate-600 dark:text-slate-300">{t('suc.candidate.evidence')}</summary>
          <p className="mt-1 whitespace-pre-wrap text-slate-700 dark:text-slate-200">{n.evidence}</p>
        </details>
        <div className="flex flex-wrap gap-2">
          {canManage && n.status === 'Proposed' && (
            <button type="button" className="btn-primary text-xs" data-testid="suc-candidate-review" disabled={busy === n.id} onClick={() => void startReview(n)}>{t('suc.candidate.startReview')}</button>
          )}
          {canAssess && !isClosed && (
            <button type="button" className="btn-secondary text-xs" data-testid="suc-candidate-assess" onClick={() => setDialog({ kind: 'assess', n })}>{t('suc.candidate.assess')}</button>
          )}
          {canManage && (n.status === 'InReview' || n.status === 'Calibrated' || n.status === 'OnSlate') && (
            <button type="button" className="btn-secondary text-xs" data-testid="suc-candidate-readiness" onClick={() => setDialog({ kind: 'readiness', n })}>{t('suc.candidate.changeReadiness')}</button>
          )}
          {canManage && n.needsReview && (
            <button type="button" className="btn-secondary text-xs" data-testid="suc-candidate-clear-review" onClick={() => setDialog({ kind: 'clear', n })}>{t('suc.candidate.clearReview')}</button>
          )}
          {canManage && !isClosed && (
            <button type="button" className="btn-secondary text-xs" data-testid="suc-candidate-decline" onClick={() => setDialog({ kind: 'decline', n })}>{t('suc.candidate.decline')}</button>
          )}
          <button type="button" className="btn-secondary text-xs" data-testid="suc-candidate-history" onClick={() => setDialog({ kind: 'history', n })}>{t('suc.candidate.history')}</button>
        </div>
      </li>
    );
  };

  return (
    <SectionCard title={t('suc.candidate.title')} description={t('suc.candidate.intro')} testId="suc-candidates">
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !rows && <LoadingRows label={t('suc.loading')} rows={2} />}
      {rows && live.length === 0 && <EmptyBlock title={t('suc.candidate.emptyTitle')} body={t('suc.candidate.emptyBody')} />}
      {live.length > 0 && <ul className="space-y-2">{live.map((n) => row(n))}</ul>}
      {closed.length > 0 && (
        <details className="text-xs">
          <summary className="cursor-pointer font-semibold text-slate-600 dark:text-slate-300">{t('suc.candidate.closed', { count: closed.length })}</summary>
          <ul className="mt-2 space-y-2">{closed.map((n) => row(n, true))}</ul>
        </details>
      )}
      {dialog?.kind === 'readiness' && <ReadinessDialog n={dialog.n} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === 'decline' && (
        <ReasonDialog title={t('suc.candidate.declineTitle')} intro={t('suc.candidate.declineIntro')} confirmLabel={t('suc.candidate.decline')} minLength={5}
          testId="suc-decline" onClose={() => setDialog(null)} onConfirm={async (reason) => { await successionApi.decline(dialog.n.id, reason, dialog.n.version); done(); }} />
      )}
      {dialog?.kind === 'clear' && (
        <ReasonDialog title={t('suc.candidate.clearTitle')} intro={dialog.n.reviewReason ?? undefined} confirmLabel={t('suc.candidate.clearReview')} minLength={5}
          testId="suc-clear-review" onClose={() => setDialog(null)} onConfirm={async (reason) => { await successionApi.clearReview(dialog.n.id, reason); done(); }} />
      )}
      {dialog?.kind === 'assess' && <AssessDialog defaultPeriod={currentPeriod()} employee={dialog.n.candidate} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === 'history' && <HistoryDrawer n={dialog.n} onClose={() => setDialog(null)} />}
    </SectionCard>
  );
}

function ReadinessDialog({ n, onClose, onDone }: { n: Nomination; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const [readiness, setReadiness] = useState<Readiness>(n.readiness ?? 'OneToTwoYears');
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problem = readiness === n.readiness ? t('suc.candidate.sameReadiness') : longEnough(reason, 10) ? null : t('suc.check.min10');
  return (
    <Modal isOpen title={t('suc.candidate.readinessTitle')} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-readiness-submit" disabled={busy} onClick={async () => {
          setTouched(true);
          if (problem) return;
          setBusy(true);
          setError(null);
          try { await successionApi.changeReadiness(n.id, readiness, reason.trim(), n.version); onDone(); } catch (e) { setError(successionError(e, t)); } finally { setBusy(false); }
        }}>{busy ? t('suc.saving') : t('suc.candidate.saveReadiness')}</button>
      </>
    )}>
      <div className="space-y-3">
        <p className="text-sm"><PersonName person={n.candidate} /></p>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.candidate.readinessIntro', { current: n.readiness ? t(readinessLabel[n.readiness]) : t('suc.readiness.notSet') })}</p>
        <Field id="suc-readiness-select" label={t('suc.readiness.label')}>
          <select id="suc-readiness-select" data-testid="suc-readiness-select" className="select w-full" value={readiness} onChange={(e) => setReadiness(e.target.value as Readiness)}>
            {READINESS.map((r) => <option key={r} value={r}>{t(readinessLabel[r])}</option>)}
          </select>
        </Field>
        <Field id="suc-readiness-reason" label={t('suc.reason')} hint={t('suc.candidate.readinessReasonHint')} error={touched ? problem : null}>
          <textarea id="suc-readiness-reason" data-testid="suc-readiness-reason" className="input min-h-[72px] w-full" value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}

function HistoryDrawer({ n, onClose }: { n: Nomination; onClose: () => void }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const [rows, setRows] = useState<HistoryEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    setError(null);
    try { setRows(await successionApi.history(n.id)); } catch (e) { setError(successionError(e, t)); }
  }, [n.id, t]);
  useEffect(() => { void load(); }, [load]);
  return (
    <Modal isOpen title={t('suc.candidate.historyTitle', { name: personName(n.candidate, locale) })} onClose={onClose} size="lg">
      <div data-testid="suc-history">
        {error && <ErrorBlock message={error} onRetry={() => void load()} />}
        {!error && !rows && <LoadingRows label={t('suc.loading')} rows={2} />}
        {rows?.length === 0 && <p className="text-xs text-slate-500">{t('suc.candidate.noHistory')}</p>}
        {rows && rows.length > 0 && (
          <ol className="space-y-2">
            {rows.map((h, i) => (
              <li key={`${h.atUtc}-${i}`} className="rounded-lg border border-slate-200 p-2 text-xs dark:border-white/10" data-testid="suc-history-entry">
                <p className="font-semibold text-slate-800 dark:text-slate-100">{historyActionLabel[h.action] ? t(historyActionLabel[h.action]) : h.action}</p>
                <p className="text-slate-500">{fx.dateTime(h.atUtc)}</p>
                {h.metadata && <p className="mt-1 break-all font-mono text-caption text-slate-500" dir="ltr">{h.metadata}</p>}
              </li>
            ))}
          </ol>
        )}
      </div>
    </Modal>
  );
}

function NineBoxSection({ positionId }: { positionId: string }) {
  const { t } = useLocale();
  const [data, setData] = useState<NineBox | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    setError(null);
    try { setData(await successionApi.nineBox({ positionId })); } catch (e) { setError(successionError(e, t)); }
  }, [positionId, t]);
  useEffect(() => { void load(); }, [load]);
  return (
    <SectionCard title={t('suc.ninebox.title')} description={t('suc.ninebox.intro')} testId="suc-ninebox-section">
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !data && <LoadingRows label={t('suc.loading')} rows={2} />}
      {data && !data.reviewPeriod && <EmptyBlock title={t('suc.ninebox.emptyTitle')} body={t('suc.ninebox.emptyBody')} />}
      {data && data.reviewPeriod && <NineBoxGrid data={data} />}
    </SectionCard>
  );
}
