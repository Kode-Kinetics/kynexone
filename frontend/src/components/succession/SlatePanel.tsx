'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Plus, ShieldAlert } from 'lucide-react';
import { successionApi, type CriticalPosition, type Nomination, type SlateCandidateInput, type SlateSnapshot, type SlateVersion } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  bandLabel, impactLabel, nineBoxCellLabel, potentialLabel, readinessLabel, readinessTone, riskLabel, shaPrefix, slateDraftProblem, slateStatusLabel,
  slateTone, successionError,
} from '../../lib/succession';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, FormError, LoadingRows, RawCode, ReasonDialog, SectionCard } from './SuccessionParts';

/** The read-only content of one slate version: who, in which manual order, why, and every 9-box input. */
export function SnapshotView({ snapshot }: { snapshot: SlateSnapshot }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const name = (c: { nameEn: string; nameAr: string }) => (locale === 'ar' ? c.nameAr || c.nameEn : c.nameEn || c.nameAr);
  return (
    <div className="space-y-3" data-testid="suc-snapshot">
      <p className="text-xs text-slate-600 dark:text-slate-300">
        {t('suc.snapshot.header', {
          position: snapshot.positionTitle, risk: t(riskLabel[snapshot.riskOfLoss]), impact: t(impactLabel[snapshot.impactOfVacancy]),
          incumbent: snapshot.incumbent ? name(snapshot.incumbent) : t('suc.position.vacant'), built: fx.dateTime(snapshot.builtAtUtc),
        })}
      </p>
      <p className="text-caption text-slate-500">{t('suc.snapshot.manualOrder')}</p>
      {snapshot.candidates.length === 0 && <p className="text-xs text-slate-500">{t('suc.snapshot.noCandidates')}</p>}
      <ol className="space-y-2">
        {[...snapshot.candidates].sort((a, b) => a.order - b.order).map((c) => (
          <li key={c.nominationId} className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-snapshot-candidate" data-nomination-id={c.nominationId}>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm">
                <span className="me-2 inline-grid h-6 w-6 place-items-center rounded-full bg-sapphire/10 text-xs font-semibold text-sapphire tabular-nums">{c.order}</span>
                <span className="font-semibold text-slate-900 dark:text-white">{name(c)}</span>
                <span className="ms-1 text-xs text-slate-400" dir="ltr">{c.employeeCode}</span>
              </p>
              <StatusChip label={t(readinessLabel[c.readiness])} tone={readinessTone(c.readiness)} />
            </div>
            <p className="text-xs text-slate-700 dark:text-slate-200">{t('suc.snapshot.rationale', { text: c.rationale })}</p>
            <dl className="grid gap-x-4 gap-y-1 text-caption sm:grid-cols-3">
              <Pair k={t('suc.inputs.cell')} v={c.nineBoxCell ? t(nineBoxCellLabel[c.nineBoxCell] ?? c.nineBoxCell) : t('suc.ninebox.insufficient')} />
              <Pair k={t('suc.inputs.period')} v={c.reviewPeriod ?? '—'} />
              <Pair k={t('suc.inputs.score')} v={c.performanceScore == null ? '—' : fx.number(c.performanceScore)} />
              <Pair k={t('suc.inputs.band')} v={t(bandLabel[c.performanceBand])} />
              <Pair k={t('suc.inputs.bandRule')} v={c.bandRuleVersion ?? '—'} />
              <Pair k={t('suc.inputs.review')} v={c.appraisalReviewId ?? t('suc.inputs.noReview')} />
              <Pair k={t('suc.inputs.proposed')} v={c.proposedPotential == null ? '—' : t(potentialLabel[c.proposedPotential])} />
              <Pair k={t('suc.inputs.calibrated')} v={c.calibratedPotential == null ? t('suc.inputs.notCalibrated') : t(potentialLabel[c.calibratedPotential])} />
              <Pair k={t('suc.inputs.reason')} v={c.calibrationReason ?? '—'} />
            </dl>
          </li>
        ))}
      </ol>
      <p className="text-caption text-slate-400" dir="ltr">{`${snapshot.rules.nineBox} · ${snapshot.rules.readinessScale} · ${snapshot.rules.ordering}`}</p>
    </div>
  );
}

function Pair({ k, v }: { k: string; v: string }) {
  return (
    <div className="flex justify-between gap-2 border-b border-slate-100 py-0.5 dark:border-white/[0.06]">
      <dt className="text-slate-500">{k}</dt>
      <dd className="break-all text-end font-medium text-slate-800 dark:text-slate-100">{v}</dd>
    </div>
  );
}

/** "Hash verified" — the stored snapshot still hashes to what was recorded at submit. */
export function HashBadge({ slate }: { slate: Pick<SlateVersion, 'hashVerified' | 'snapshotSha256' | 'integrity'> }) {
  const { t } = useLocale();
  if (!slate.snapshotSha256) return null;
  return (
    <span className="inline-flex items-center gap-1 text-caption" title={slate.snapshotSha256} data-testid="suc-slate-hash" data-integrity={slate.integrity ?? (slate.hashVerified ? 'Verified' : 'Tampered')}>
      {slate.hashVerified && slate.integrity === 'Anonymised'
        ? <><CheckCircle2 className="h-3.5 w-3.5 text-slate-500" aria-hidden="true" /><span className="font-semibold text-slate-600 dark:text-slate-300">{t('suc.slate.hashAnonymised')}</span></>
        : slate.hashVerified
        ? <><CheckCircle2 className="h-3.5 w-3.5 text-emerald-600" aria-hidden="true" /><span className="font-semibold text-emerald-700 dark:text-emerald-300">{t('suc.slate.hashVerified')}</span></>
        : <><ShieldAlert className="h-3.5 w-3.5 text-rose-600" aria-hidden="true" /><span className="font-semibold text-rose-700 dark:text-rose-300">{t('suc.slate.hashMismatch')}</span></>}
      <span className="font-mono text-slate-500" dir="ltr">{shaPrefix(slate.snapshotSha256)}</span>
    </span>
  );
}

interface DraftEntry { pick: boolean; order: string; rationale: string }

/**
 * Slates for one critical position (SUC-06): the versions (status, dates, hash, return reason, a read-only snapshot),
 * and — for slates.manage — the draft editor: choose calibrated candidates, put them in YOUR order 1..n with a reason
 * for each place, save, submit for approval, revise a returned or approved version, or discard a draft.
 */
export function SlatePanel({ position, canManage }: { position: CriticalPosition; canManage: boolean }) {
  const { t } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [versions, setVersions] = useState<SlateVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);
  const [editingNew, setEditingNew] = useState(false);
  const [discarding, setDiscarding] = useState<SlateVersion | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try { setVersions([...(await successionApi.slates(position.id))].sort((a, b) => b.versionNo - a.versionNo)); } catch (e) { setError(successionError(e, t)); }
  }, [position.id, t]);
  useEffect(() => { void load(); }, [load]);

  const draft = versions?.find((v) => v.status === 'Draft') ?? null;
  const open = versions?.find((v) => v.status === 'Draft' || v.status === 'Submitted') ?? null;
  const latest = versions?.[0] ?? null;
  const revisable = !open && latest && (latest.status === 'ReturnedForRevision' || latest.status === 'Approved') ? latest
    : !open ? versions?.find((v) => v.status === 'Approved') ?? null : null;

  const revise = async (v: SlateVersion) => {
    setBusy(true);
    try { await successionApi.reviseSlate(v.id); toast.success(t('suc.slate.revised')); await load(); } catch (e) { toast.error(successionError(e, t)); } finally { setBusy(false); }
  };

  return (
    <SectionCard title={t('suc.slate.title')} description={t('suc.slate.intro')} testId="suc-slates" actions={canManage && versions ? (
      <div className="flex flex-wrap gap-2">
        {!open && !editingNew && (
          <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-slate-new" onClick={() => setEditingNew(true)}>
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.slate.new')}
          </button>
        )}
        {revisable && !editingNew && (
          <button type="button" className="btn-secondary text-xs" data-testid="suc-slate-revise" disabled={busy} onClick={() => void revise(revisable)}>
            {t('suc.slate.revise', { version: revisable.versionNo })}
          </button>
        )}
      </div>
    ) : undefined}>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !versions && <LoadingRows label={t('suc.loading')} rows={2} />}

      {canManage && (editingNew || draft) && versions && (
        <SlateEditor key={draft?.id ?? 'new'} position={position} draft={draft}
          onSaved={() => { setEditingNew(false); void load(); }} onCancelNew={() => setEditingNew(false)} onDiscard={draft ? () => setDiscarding(draft) : undefined} />
      )}

      {versions && versions.length === 0 && !editingNew && (
        <EmptyBlock title={t('suc.slate.noneTitle')} body={canManage ? t('suc.slate.noneBodyManage') : t('suc.slate.noneBody')} />
      )}
      {versions && versions.length > 0 && (
        <ul className="space-y-2" aria-label={t('suc.slate.versions')}>
          {versions.map((v) => (
            <li key={v.id} className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-slate-version" data-slate-id={v.id} data-status={v.status}>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <p className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-semibold text-slate-900 dark:text-white">{t('suc.slate.version', { version: v.versionNo })}</span>
                  <StatusChip label={t(slateStatusLabel[v.status])} tone={slateTone(v.status)} />
                  <RawCode testId="suc-slate-status" value={v.status} />
                  <HashBadge slate={v} />
                </p>
                <span className="flex flex-wrap gap-2">
                  {v.snapshot && (
                    <button type="button" className="btn-secondary text-xs" aria-expanded={viewing === v.id} onClick={() => setViewing(viewing === v.id ? null : v.id)}>
                      {viewing === v.id ? t('suc.slate.hide') : t('suc.slate.view')}
                    </button>
                  )}
                  {v.status !== 'Draft' && (
                    <Link href={`/performance/succession/slates/${v.id}`} className="btn-secondary text-xs" data-testid="suc-slate-open">{t('suc.slate.openPage')}</Link>
                  )}
                </span>
              </div>
              <p className="text-caption text-slate-500">
                {[
                  v.submittedAtUtc ? t('suc.slate.submittedOn', { date: fx.dateTime(v.submittedAtUtc) }) : null,
                  v.approvedAtUtc ? t('suc.slate.approvedOn', { date: fx.dateTime(v.approvedAtUtc) }) : null,
                  v.effectiveFrom ? t('suc.slate.effective', { date: fx.date(v.effectiveFrom) }) : null,
                  v.supersededAtUtc ? t('suc.slate.supersededOn', { date: fx.dateTime(v.supersededAtUtc) }) : null,
                  v.cancelledAtUtc ? t('suc.slate.cancelledOn', { date: fx.dateTime(v.cancelledAtUtc) }) : null,
                ].filter(Boolean).join(' · ') || t('suc.slate.notSubmitted')}
              </p>
              {v.returnReason && <p className="text-xs text-rose-700 dark:text-rose-300" data-testid="suc-slate-return-reason-text">{t('suc.slate.returnedBecause', { reason: v.returnReason })}</p>}
              {v.cancelReason && <p className="text-xs text-slate-500">{t('suc.slate.cancelledBecause', { reason: v.cancelReason })}</p>}
              {v.closedSinceApproval.length > 0 && <p className="text-xs text-amber-700 dark:text-amber-300">{t('suc.slate.stale', { count: v.closedSinceApproval.length })}</p>}
              {viewing === v.id && v.snapshot && <SnapshotView snapshot={v.snapshot} />}
            </li>
          ))}
        </ul>
      )}
      {discarding && (
        <ReasonDialog title={t('suc.slate.discardTitle')} intro={t('suc.slate.discardIntro')} confirmLabel={t('suc.slate.discard')} minLength={3} testId="suc-slate-discard"
          onClose={() => setDiscarding(null)} onConfirm={async (reason) => { await successionApi.cancelSlate(discarding.id, reason); setDiscarding(null); await load(); }} />
      )}
    </SectionCard>
  );
}

function SlateEditor({ position, draft, onSaved, onCancelNew, onDiscard }: {
  position: CriticalPosition; draft: SlateVersion | null; onSaved: () => void; onCancelNew: () => void; onDiscard?: () => void;
}) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [eligible, setEligible] = useState<Nomination[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [entries, setEntries] = useState<Record<string, DraftEntry>>(() => {
    const init: Record<string, DraftEntry> = {};
    for (const c of draft?.snapshot?.candidates ?? []) init[c.nominationId] = { pick: true, order: String(c.order), rationale: c.rationale };
    return init;
  });
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(!draft);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const list = await successionApi.nominations({ positionId: position.id });
      setEligible(list.filter((n) => n.status === 'Calibrated' || n.status === 'OnSlate'));
    } catch (e) { setLoadError(successionError(e, t)); }
  }, [position.id, t]);
  useEffect(() => { void load(); }, [load]);

  const picked = useMemo(() => Object.entries(entries).filter(([, e]) => e.pick), [entries]);
  const payload: SlateCandidateInput[] = picked.map(([id, e]) => ({ nominationId: id, order: Number(e.order), rationale: e.rationale.trim() }));
  const problemKey = slateDraftProblem(payload.map((p) => ({ order: Number.isFinite(p.order) ? p.order : 0, rationale: p.rationale })));

  const set = (id: string, patch: Partial<DraftEntry>) => {
    setDirty(true);
    setEntries((all) => {
      const current = all[id] ?? { pick: false, order: '', rationale: '' };
      const next = { ...current, ...patch };
      // Ticking a candidate gives them the next free place; the planner changes it as they see fit.
      if (patch.pick === true && !current.order) next.order = String(Object.values(all).filter((e) => e.pick).length + 1);
      return { ...all, [id]: next };
    });
  };

  const save = async (): Promise<SlateVersion | null> => {
    setTouched(true);
    if (problemKey) return null;
    setBusy(true);
    setError(null);
    try {
      const saved = draft ? await successionApi.updateSlate(draft.id, payload, draft.version) : await successionApi.createSlate(position.id, payload);
      setDirty(false);
      return saved;
    } catch (e) {
      setError(successionError(e, t));
      return null;
    } finally {
      setBusy(false);
    }
  };

  const submit = async () => {
    const saved = dirty || !draft ? await save() : draft;
    if (!saved) return;
    setBusy(true);
    try {
      await successionApi.submitSlate(saved.id, saved.version);
      toast.success(t('suc.slate.submitted'));
      onSaved();
    } catch (e) {
      setError(successionError(e, t));
      onSaved();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-3 rounded-xl border border-sapphire/30 bg-sapphire/[0.03] p-3" data-testid="suc-slate-editor">
      <div>
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{draft ? t('suc.slate.editDraft', { version: draft.versionNo }) : t('suc.slate.newDraft')}</p>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.slate.manualNote')}</p>
      </div>
      {loadError && <ErrorBlock message={loadError} onRetry={() => void load()} />}
      {!loadError && !eligible && <LoadingRows label={t('suc.loading')} rows={2} />}
      {eligible?.length === 0 && <p className="text-xs text-slate-500">{t('suc.slate.noEligible')}</p>}
      {eligible && eligible.length > 0 && (
        <ul className="space-y-2">
          {eligible.map((n) => {
            const e = entries[n.id] ?? { pick: false, order: '', rationale: '' };
            const name = locale === 'ar' ? n.candidate.nameAr || n.candidate.nameEn : n.candidate.nameEn;
            return (
              <li key={n.id} className="grid gap-2 rounded-lg border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-white/[0.03] sm:grid-cols-[minmax(0,1.3fr)_5.5rem_minmax(0,2fr)] sm:items-start">
                <label className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-1" data-testid={`suc-slate-pick-${n.id}`} checked={e.pick} onChange={(ev) => set(n.id, { pick: ev.target.checked })} />
                  <span>
                    <span className="font-semibold text-slate-900 dark:text-white">{name}</span>
                    <span className="block text-caption text-slate-500">
                      {n.readiness ? t(readinessLabel[n.readiness]) : t('suc.readiness.notSet')}
                      {n.currentAssessment?.nineBoxCell ? ` · ${t(nineBoxCellLabel[n.currentAssessment.nineBoxCell] ?? n.currentAssessment.nineBoxCell)}` : ` · ${t('suc.ninebox.insufficient')}`}
                    </span>
                  </span>
                </label>
                <div>
                  <label htmlFor={`suc-slate-order-${n.id}`} className="block text-caption font-semibold text-slate-600 dark:text-slate-300">{t('suc.slate.place')}</label>
                  <input id={`suc-slate-order-${n.id}`} data-testid={`suc-slate-order-${n.id}`} type="number" min={1} inputMode="numeric" className="input w-full" disabled={!e.pick}
                    value={e.order} onChange={(ev) => set(n.id, { order: ev.target.value })} />
                </div>
                <div>
                  <label htmlFor={`suc-slate-rationale-${n.id}`} className="block text-caption font-semibold text-slate-600 dark:text-slate-300">{t('suc.slate.rationale')}</label>
                  <textarea id={`suc-slate-rationale-${n.id}`} data-testid={`suc-slate-rationale-${n.id}`} className="input min-h-[52px] w-full" disabled={!e.pick}
                    value={e.rationale} onChange={(ev) => set(n.id, { rationale: ev.target.value })} />
                </div>
              </li>
            );
          })}
        </ul>
      )}
      {touched && problemKey && <p role="alert" className="text-xs font-medium text-rose-600 dark:text-rose-400">{t(problemKey)}</p>}
      <FormError message={error} />
      <div className="flex flex-wrap justify-end gap-2">
        {!draft && <button type="button" className="btn-secondary text-xs" onClick={onCancelNew}>{t('suc.cancel')}</button>}
        {onDiscard && <button type="button" className="btn-secondary text-xs" data-testid="suc-slate-discard" onClick={onDiscard}>{t('suc.slate.discard')}</button>}
        <button type="button" className="btn-secondary text-xs" data-testid="suc-slate-save" disabled={busy}
          onClick={async () => { if (await save()) { toast.success(t('suc.slate.saved')); onSaved(); } }}>
          {busy ? t('suc.saving') : t('suc.slate.save')}
        </button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-slate-submit" disabled={busy} onClick={() => void submit()}>{t('suc.slate.submit')}</button>
      </div>
    </div>
  );
}
