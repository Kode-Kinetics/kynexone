'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { successionApi, type Nomination, type PersonRef, type Readiness, type TalentAssessment } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  POTENTIALS, READINESS, assessmentStatusLabel, bandLabel, calibrationReasonProblem, currentPeriod, longEnough, nominationStatusLabel,
  nominationTone, potentialLabel, readinessLabel, successionError, successionErrorCode, validPeriod,
} from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, FormError, LoadingRows, PersonName, PersonPicker, SectionCard, type SuccessionAccess } from './SuccessionParts';

/**
 * Talent review (SUC-03): the period's submitted assessments — proposed potential, its evidence and the performance
 * band it was placed with — and the calibration of each; then the nominations in review whose readiness a calibrator
 * confirms. Every refusal (a reviewer conflict, a stale version) is shown as the server's sentence.
 */
export function CalibrationPanel({ access }: { access: SuccessionAccess }) {
  const { t } = useLocale();
  const [periods, setPeriods] = useState<string[] | null>(null);
  const [period, setPeriod] = useState('');
  const [rows, setRows] = useState<TalentAssessment[] | null>(null);
  const [inReview, setInReview] = useState<Nomination[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [assessing, setAssessing] = useState(false);
  const [calibrating, setCalibrating] = useState<TalentAssessment | null>(null);
  const [confirming, setConfirming] = useState<Nomination | null>(null);

  const loadPeriods = useCallback(async () => {
    setError(null);
    try {
      const list = await successionApi.periods();
      setPeriods(list);
      setPeriod((p) => p || list[0] || '');
    } catch (e) { setError(successionError(e, t)); }
  }, [t]);

  const loadRows = useCallback(async () => {
    if (!period) { setRows([]); return; }
    setRows(null);
    try {
      setRows((await successionApi.assessments({ period })).filter((a) => a.status !== 'Superseded'));
    } catch (e) { setError(successionError(e, t)); }
  }, [period, t]);

  const loadReview = useCallback(async () => {
    try { setInReview(await successionApi.nominations({ status: 'InReview' })); } catch (e) { setError(successionError(e, t)); }
  }, [t]);

  useEffect(() => { void loadPeriods(); void loadReview(); }, [loadPeriods, loadReview]);
  useEffect(() => { if (periods) void loadRows(); }, [periods, loadRows]);

  const afterChange = () => { void loadPeriods(); void loadRows(); void loadReview(); };
  const submitted = rows?.filter((r) => r.status === 'Submitted' || r.status === 'Draft') ?? [];
  const calibrated = rows?.filter((r) => r.status === 'Calibrated') ?? [];

  if (error) return <ErrorBlock message={error} onRetry={() => { setError(null); afterChange(); }} />;

  return (
    <div className="space-y-4" data-testid="suc-calibration">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <Field id="suc-period" label={t('suc.calibration.period')}>
          <select id="suc-period" data-testid="suc-calibration-period" className="select" value={period} onChange={(e) => setPeriod(e.target.value)} disabled={!periods || periods.length === 0}>
            {!periods && <option value="">{t('suc.loading')}</option>}
            {periods?.length === 0 && <option value="">{t('suc.calibration.noPeriods')}</option>}
            {periods?.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </Field>
        {access.assess && (
          <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-assess-open" onClick={() => setAssessing(true)}>
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.assess.open')}
          </button>
        )}
      </div>

      <SectionCard title={t('suc.calibration.toCalibrate')} description={t('suc.calibration.toCalibrateHint')}>
        {!rows && <LoadingRows label={t('suc.loading')} rows={2} />}
        {rows && submitted.length === 0 && <EmptyBlock title={t('suc.calibration.noneWaiting')} body={access.assess ? t('suc.calibration.noneWaitingAssess') : undefined} />}
        {submitted.length > 0 && (
          <ul className="space-y-2">
            {submitted.map((a) => (
              <AssessmentRow key={a.id} a={a} action={
                a.status === 'Draft' && access.assess
                  ? <SubmitDraftButton id={a.id} onDone={afterChange} />
                  : access.calibrate && a.status === 'Submitted'
                    ? <button type="button" className="btn-primary text-xs" data-testid="suc-calibrate-open" onClick={() => setCalibrating(a)}>{t('suc.calibrate.open')}</button>
                    : null
              } />
            ))}
          </ul>
        )}
      </SectionCard>

      {calibrated.length > 0 && (
        <SectionCard title={t('suc.calibration.calibrated')} description={t('suc.calibration.calibratedHint')}>
          <ul className="space-y-2">{calibrated.map((a) => <AssessmentRow key={a.id} a={a} />)}</ul>
        </SectionCard>
      )}

      <SectionCard title={t('suc.calibration.readinessTitle')} description={t('suc.calibration.readinessHint')} testId="suc-calibration-nominations">
        {!inReview && <LoadingRows label={t('suc.loading')} rows={1} />}
        {inReview?.length === 0 && <p className="text-xs text-slate-500">{t('suc.calibration.noneInReview')}</p>}
        {inReview && inReview.length > 0 && (
          <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {inReview.map((n) => (
              <li key={n.id} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm" data-testid="suc-review-nomination" data-nomination-id={n.id}>
                <span className="min-w-0">
                  <PersonName person={n.candidate} />
                  <span className="block text-xs text-slate-500">
                    {n.target.kind === 'position'
                      ? <Link href={`/performance/succession/positions/${n.target.id}`} className="text-sapphire hover:underline dark:text-cyanAccent">{n.target.title}</Link>
                      : n.target.title}
                    {' · '}{n.readiness ? t(readinessLabel[n.readiness]) : t('suc.readiness.notSet')}
                    {' · '}{n.currentAssessment?.status === 'Calibrated' ? t('suc.calibration.assessmentCalibrated') : t('suc.calibration.assessmentPending')}
                  </span>
                </span>
                <span className="flex items-center gap-2">
                  <StatusChip label={t(nominationStatusLabel[n.status])} tone={nominationTone(n.status)} />
                  {access.calibrate && (
                    <button type="button" className="btn-secondary text-xs" data-testid="suc-confirm-readiness-open" data-nomination-id={n.id} onClick={() => setConfirming(n)}>
                      {t('suc.confirm.open')}
                    </button>
                  )}
                </span>
              </li>
            ))}
          </ul>
        )}
      </SectionCard>

      {assessing && <AssessDialog defaultPeriod={period || currentPeriod()} onClose={() => setAssessing(false)} onDone={(p) => { setAssessing(false); setPeriod(p); afterChange(); }} />}
      {calibrating && <CalibrateDialog assessment={calibrating} onClose={() => setCalibrating(null)} onDone={() => { setCalibrating(null); afterChange(); }} />}
      {confirming && <ConfirmReadinessDialog nomination={confirming} onClose={() => setConfirming(null)} onDone={() => { setConfirming(null); afterChange(); }} />}
    </div>
  );
}

function AssessmentRow({ a, action }: { a: TalentAssessment; action?: React.ReactNode }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  return (
    <li className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-calibration-row" data-assessment-id={a.id}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <PersonName person={a.employee} />
          <p className="text-xs text-slate-500">
            {t('suc.assessment.by', { name: a.assessor ? (locale === 'ar' ? a.assessor.nameAr || a.assessor.nameEn : a.assessor.nameEn) : t('suc.unknown') })}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <StatusChip label={t(assessmentStatusLabel[a.status])} tone={a.status === 'Calibrated' ? 'emerald' : a.status === 'Draft' ? 'slate' : 'amber'} />
          {action}
        </div>
      </div>
      <dl className="grid gap-2 text-xs sm:grid-cols-3">
        <div>
          <dt className="text-slate-500">{t('suc.assessment.proposed')}</dt>
          <dd className="font-semibold text-slate-800 dark:text-slate-100">{t(potentialLabel[a.proposedPotential])}</dd>
          {a.calibratedPotential !== null && <dd className="text-slate-600 dark:text-slate-300">{t('suc.assessment.calibratedTo', { potential: t(potentialLabel[a.calibratedPotential]) })}</dd>}
        </div>
        <div>
          <dt className="text-slate-500">{t('suc.assessment.performance')}</dt>
          <dd className="font-semibold text-slate-800 dark:text-slate-100">{t(bandLabel[a.performanceBand])}</dd>
          <dd className="text-slate-600 dark:text-slate-300">
            {a.performanceScore !== null
              ? t('suc.assessment.score', { score: fx.number(a.performanceScore), cycle: a.appraisalCycleName ?? '—' })
              : t('suc.assessment.noAppraisal')}
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">{t('suc.assessment.cell')}</dt>
          <dd className="font-semibold text-slate-800 dark:text-slate-100">
            {a.nineBoxCell ? (locale === 'ar' ? a.cellLabelAr : a.cellLabelEn) ?? a.nineBoxCell : t('suc.ninebox.insufficient')}
          </dd>
        </div>
      </dl>
      <details className="text-xs">
        <summary className="cursor-pointer font-semibold text-slate-600 dark:text-slate-300">{t('suc.assessment.evidence')}</summary>
        <p className="mt-1 whitespace-pre-wrap text-slate-700 dark:text-slate-200">{a.potentialEvidence}</p>
        {a.calibrationReason && <p className="mt-1 text-slate-600 dark:text-slate-300">{t('suc.assessment.reason', { reason: a.calibrationReason })}</p>}
      </details>
    </li>
  );
}

function SubmitDraftButton({ id, onDone }: { id: string; onDone: () => void }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [busy, setBusy] = useState(false);
  return (
    <button type="button" className="btn-secondary text-xs" disabled={busy} onClick={async () => {
      setBusy(true);
      try { await successionApi.submitAssessment(id); onDone(); } catch (e) { toast.error(successionError(e, t)); } finally { setBusy(false); }
    }}>{t('suc.assessment.submitDraft')}</button>
  );
}

/** Assess (succession.assess): potential 1–3 with at least 20 characters of evidence. Performance comes from the latest appraisal. */
export function AssessDialog({ defaultPeriod, employee, onClose, onDone }: {
  defaultPeriod: string; employee?: PersonRef | null; onClose: () => void; onDone: (period: string) => void;
}) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [person, setPerson] = useState<PersonRef | null>(employee ?? null);
  const [period, setPeriod] = useState(defaultPeriod);
  const [potential, setPotential] = useState(2);
  const [evidence, setEvidence] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [offerSupersede, setOfferSupersede] = useState(false);

  const problems = {
    person: person ? null : t('suc.check.choosePerson'),
    period: validPeriod(period) ? null : t('suc.check.period'),
    evidence: longEnough(evidence, 20) ? null : t('suc.check.min20'),
  };

  const submit = async (supersede = false) => {
    setTouched(true);
    if (problems.person || problems.period || problems.evidence) return;
    setBusy(true);
    setError(null);
    try {
      const p = period.trim().toUpperCase();
      await successionApi.assess({ employeeId: person!.employeeId, reviewPeriod: p, proposedPotential: potential, potentialEvidence: evidence.trim(), supersede });
      toast.success(t('suc.assess.done'));
      onDone(p);
    } catch (e) {
      setOfferSupersede(successionErrorCode(e) === 'assessment_calibrated');
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={t('suc.assess.title')} onClose={onClose} size="lg" footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        {offerSupersede && <button type="button" className="btn-secondary text-xs" disabled={busy} onClick={() => void submit(true)}>{t('suc.assess.supersede')}</button>}
        <button type="button" className="btn-primary text-xs" data-testid="suc-assess-submit" disabled={busy} onClick={() => void submit(false)}>
          {busy ? t('suc.saving') : t('suc.assess.submit')}
        </button>
      </>
    )}>
      <div className="space-y-3">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.assess.intro')}</p>
        {employee
          ? <p className="text-sm"><PersonName person={employee} /></p>
          : <PersonPicker id="suc-assess-employee" testId="suc-assess-employee" value={person} onChange={setPerson} label={t('suc.assess.employee')} error={touched ? problems.person : null} />}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field id="suc-assess-period" label={t('suc.assess.period')} hint={t('suc.assess.periodHint')} error={touched ? problems.period : null}>
            <input id="suc-assess-period" data-testid="suc-assess-period" className="input w-full" dir="ltr" value={period} onChange={(e) => setPeriod(e.target.value)} />
          </Field>
          <Field id="suc-assess-potential" label={t('suc.assess.potential')}>
            <select id="suc-assess-potential" data-testid="suc-assess-potential" className="select w-full" value={potential} onChange={(e) => setPotential(Number(e.target.value))}>
              {POTENTIALS.map((p) => <option key={p} value={p}>{t(potentialLabel[p])}</option>)}
            </select>
          </Field>
        </div>
        <Field id="suc-assess-evidence" label={t('suc.assess.evidence')} hint={t('suc.assess.evidenceHint')} error={touched ? problems.evidence : null}>
          <textarea id="suc-assess-evidence" data-testid="suc-assess-evidence" className="input min-h-[96px] w-full" value={evidence} onChange={(e) => setEvidence(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}

function CalibrateDialog({ assessment, onClose, onDone }: { assessment: TalentAssessment; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [potential, setPotential] = useState(assessment.proposedPotential);
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problemKey = calibrationReasonProblem(assessment.proposedPotential, potential, reason);
  const moved = potential !== assessment.proposedPotential;

  const submit = async () => {
    setTouched(true);
    if (problemKey) return;
    setBusy(true);
    setError(null);
    try {
      await successionApi.calibrateAssessment(assessment.id, potential, reason.trim() || null, assessment.version);
      toast.success(t('suc.calibrate.done'));
      onDone();
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={t('suc.calibrate.title')} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-calibrate-submit" disabled={busy} onClick={() => void submit()}>
          {busy ? t('suc.saving') : moved ? t('suc.calibrate.submitMove') : t('suc.calibrate.submitConfirm')}
        </button>
      </>
    )}>
      <div className="space-y-3">
        <p className="text-sm"><PersonName person={assessment.employee} /></p>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.calibrate.proposedWas', { potential: t(potentialLabel[assessment.proposedPotential]) })}</p>
        <Field id="suc-calibrate-potential" label={t('suc.calibrate.potential')}>
          <select id="suc-calibrate-potential" data-testid="suc-calibrate-potential" className="select w-full" value={potential} onChange={(e) => setPotential(Number(e.target.value))}>
            {POTENTIALS.map((p) => <option key={p} value={p}>{t(potentialLabel[p])}</option>)}
          </select>
        </Field>
        <Field id="suc-calibrate-reason" label={t('suc.calibrate.reason')} optional={!moved}
          hint={moved ? t('suc.calibrate.reasonHintMoved') : t('suc.calibrate.reasonHintSame')} error={touched && problemKey ? t(problemKey) : null}>
          <textarea id="suc-calibrate-reason" data-testid="suc-calibrate-reason" className="input min-h-[80px] w-full" value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}

function ConfirmReadinessDialog({ nomination, onClose, onDone }: { nomination: Nomination; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [readiness, setReadiness] = useState<Readiness>(nomination.readiness ?? 'OneToTwoYears');
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const changes = nomination.readiness !== null && nomination.readiness !== readiness;
  const problem = changes && !longEnough(reason, 10) ? t('suc.check.min10') : null;

  const submit = async () => {
    setTouched(true);
    if (problem) return;
    setBusy(true);
    setError(null);
    try {
      await successionApi.calibrateNomination(nomination.id, readiness, reason.trim() || null, nomination.version);
      toast.success(t('suc.confirm.done'));
      onDone();
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={t('suc.confirm.title')} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-confirm-readiness-submit" disabled={busy} onClick={() => void submit()}>
          {busy ? t('suc.saving') : t('suc.confirm.submit')}
        </button>
      </>
    )}>
      <div className="space-y-3">
        <p className="text-sm"><PersonName person={nomination.candidate} /></p>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.confirm.intro', { target: nomination.target.title })}</p>
        <Field id="suc-confirm-readiness-select" label={t('suc.readiness.label')}>
          <select id="suc-confirm-readiness-select" data-testid="suc-confirm-readiness-select" className="select w-full" value={readiness} onChange={(e) => setReadiness(e.target.value as Readiness)}>
            {READINESS.map((r) => <option key={r} value={r}>{t(readinessLabel[r])}</option>)}
          </select>
        </Field>
        <Field id="suc-confirm-readiness-reason" label={t('suc.confirm.reason')} optional={!changes}
          hint={changes ? t('suc.confirm.reasonHintChanged') : t('suc.confirm.reasonHintSame')} error={touched ? problem : null}>
          <textarea id="suc-confirm-readiness-reason" data-testid="suc-confirm-readiness-reason" className="input min-h-[72px] w-full" value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}
