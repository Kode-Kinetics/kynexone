'use client';

import { useCallback, useEffect, useState } from 'react';
import { CheckCircle2, Circle, Clock } from 'lucide-react';
import { Modal } from '../Modal';
import { Field, Textarea } from '../ui/Field';
import { Badge } from '../ui/Badge';
import { Skeleton } from '../ui/Skeleton';
import { ResultBanner } from '../ui/ResultBanner';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { reviewsApi } from '../../api/performance';
import type { AppraisalAppeal, AppraisalReview, CompetencyRating, EmployeeGoal, ScoreBreakdown } from '../../api/performance';
import { formatCalendarDate } from '../../lib/calendarDate';
import { appraisalRefusal } from '../../lib/appraisalsRefusal';
import {
  REVIEW_PHASES, canAcknowledge, canAppeal, employeeMaySeeResult, ratingLabel, reviewProgress, reviewStatusLabel, scoreComponentLabel,
} from '../../lib/appraisalsUi';

type Detail = Awaited<ReturnType<typeof reviewsApi.get>>;

/**
 * The employee's own review: where it is, and once HR has published it, the result, how it was reached, and the two things
 * the employee may do about it, acknowledge it or ask HR to look at it again.
 *
 * It shows only what the person is meant to see. Before publication there is no score, rating or breakdown on screen,
 * whatever the API returns. The manager's and HR's written notes are withheld by the API; the screen says so rather than
 * showing a blank, and the calibration record and audit trail are never shown here.
 */
export function ReviewDetailDrawer({ reviewId, onClose, onChanged }: { reviewId: string; onClose: () => void; onChanged: () => void }) {
  const t = useT();
  const { locale } = useLocale();
  const [detail, setDetail] = useState<Detail | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [mode, setMode] = useState<'view' | 'appeal'>('view');
  const [reason, setReason] = useState('');
  const [justification, setJustification] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [done, setDone] = useState<{ changed: string; pending?: string } | null>(null);

  const load = useCallback(() => {
    setFailure(null);
    return reviewsApi.get(reviewId).then(setDetail).catch(e => setFailure(e ?? new Error('failed')));
  }, [reviewId]);
  useEffect(() => { void load(); }, [load]);

  const review: AppraisalReview | undefined = detail?.review;
  const appeal = (detail?.appeal ?? null) as AppraisalAppeal | null;

  const acknowledge = async () => {
    if (!review) return;
    setBusy(true); setError('');
    try {
      await reviewsApi.acknowledge(review.id);
      setDone({ changed: t('apr.detail.ackDone') });
      onChanged(); await load();
    } catch (e) { setError(appraisalRefusal(e, t, t('Failed to acknowledge.'), locale)); }
    finally { setBusy(false); }
  };

  const sendAppeal = async () => {
    if (!review) return;
    if (!reason.trim()) { setError(t('apr.appeal.reasonRequired')); return; }
    setBusy(true); setError('');
    try {
      await reviewsApi.appeal(review.id, reason.trim(), justification.trim() || undefined);
      setDone({ changed: t('apr.appeal.sent'), pending: t('apr.appeal.pending') });
      setMode('view'); setReason(''); setJustification('');
      onChanged(); await load();
    } catch (e) { setError(appraisalRefusal(e, t, t('apr.appeal.failed'), locale)); }
    finally { setBusy(false); }
  };

  const title = review ? t('apr.detail.title', { cycle: review.cycleName }) : t('apr.detail.titleLoading');
  const status = review?.status ?? '';
  const footer = review && mode === 'view' && (canAcknowledge(status) || canAppeal(status)) ? (
    <div className="flex flex-wrap items-center justify-end gap-2">
      {canAppeal(status) && <button type="button" className="btn-secondary" onClick={() => { setMode('appeal'); setError(''); setDone(null); }}>{t('apr.appeal.open')}</button>}
      {canAcknowledge(status) && <button type="button" className="btn-primary" disabled={busy} aria-busy={busy || undefined} onClick={acknowledge}>{busy ? t('apr.detail.acknowledging') : t('apr.detail.acknowledge')}</button>}
    </div>
  ) : review && mode === 'appeal' ? (
    <div className="flex flex-wrap items-center justify-end gap-2">
      <button type="button" className="btn-secondary" onClick={() => { setMode('view'); setError(''); }}>{t('Cancel')}</button>
      <button type="button" className="btn-primary" disabled={busy} aria-busy={busy || undefined} onClick={sendAppeal}>{busy ? t('apr.appeal.sending') : t('apr.appeal.send')}</button>
    </div>
  ) : undefined;

  return (
    <Modal isOpen title={title} onClose={onClose} closeLabel={t('Close')} size="lg" placement="end" footer={footer}>
      <div className="space-y-5" data-testid="review-detail">
        <p className="text-body text-fg-muted" data-purpose>{t('apr.detail.purpose')}</p>

        {failure != null && <LoadFailedNotice error={failure} onRetry={() => void load()} />}
        {failure == null && !review && (
          <div role="status" aria-busy="true" className="space-y-3">
            <span className="sr-only">{t('apr.detail.loading')}</span>
            <Skeleton className="h-10 w-full" /><Skeleton className="h-24 w-full" /><Skeleton className="h-24 w-full" />
          </div>
        )}

        {done && <ResultBanner changed={done.changed} where={t('apr.detail.where', { cycle: review?.cycleName ?? '' })} pending={done.pending} onDismiss={() => setDone(null)} />}

        {review && (
          <>
            <ProgressStrip status={status} />
            {error && <p role="alert" className="text-body text-danger-fg" data-testid="review-detail-error">{error}</p>}

            {mode === 'appeal' ? (
              <section className="space-y-4" aria-labelledby="appeal-heading">
                <h3 id="appeal-heading" className="text-h4 text-fg">{t('apr.appeal.heading')}</h3>
                <p className="text-body text-fg-muted">{t('apr.appeal.intro')}</p>
                <Field label={t('apr.appeal.reasonLabel')} required info={t('apr.appeal.reasonInfo')}>
                  <Textarea rows={3} value={reason} onChange={e => setReason(e.target.value)} placeholder={t('apr.appeal.reasonPlaceholder')} />
                </Field>
                <Field label={t('apr.appeal.supportLabel')} noInfo="free text, optional">
                  <Textarea rows={3} value={justification} onChange={e => setJustification(e.target.value)} />
                </Field>
              </section>
            ) : employeeMaySeeResult(status) ? (
              <Result review={review} breakdown={detail?.breakdown ?? []} competencies={detail?.competencies ?? []} goals={detail?.goals ?? []} appeal={appeal} locale={locale} />
            ) : (
              <WaitingNote review={review} />
            )}
          </>
        )}
      </div>
    </Modal>
  );
}

function ProgressStrip({ status }: { status: string }) {
  const t = useT();
  const current = reviewProgress(status);
  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <h3 className="text-h4 text-fg">{t('apr.detail.progress')}</h3>
        <Badge tone={status === 'Appealed' ? 'warning' : employeeMaySeeResult(status) ? 'success' : 'info'}>{reviewStatusLabel(t, status)}</Badge>
      </div>
      <ol className="grid grid-cols-5 gap-1" data-step-strip aria-label={t('apr.detail.progress')}>
        {REVIEW_PHASES.map((phase, i) => {
          const state = i < current ? 'done' : i === current ? 'current' : 'todo';
          const Icon = state === 'done' ? CheckCircle2 : state === 'current' ? Clock : Circle;
          return (
            <li key={phase} aria-current={state === 'current' ? 'step' : undefined} data-phase-state={state}
              className={`flex flex-col items-center gap-1 text-center text-caption ${state === 'todo' ? 'text-fg-subtle' : 'text-fg'}`}>
              <Icon className={`h-4 w-4 ${state === 'done' ? 'text-success-fg' : state === 'current' ? 'text-primary' : ''}`} aria-hidden />
              <span>{t(`apr.phase.${phase}`)}</span>
              <span className="sr-only">{t(`apr.phaseState.${state}`)}</span>
            </li>
          );
        })}
      </ol>
    </div>
  );
}

function WaitingNote({ review }: { review: AppraisalReview }) {
  const t = useT();
  const next = review.status === 'SelfAssessmentDue' || review.status === 'Pending' ? t('apr.waiting.self')
    : review.status === 'SelfAssessmentSubmitted' || review.status === 'ManagerReview' ? t('apr.waiting.manager')
    : t('apr.waiting.hr');
  return (
    <section className="space-y-3 rounded-lg border border-line bg-surface-sunken p-4" data-testid="result-withheld">
      <h3 className="text-h4 text-fg">{t('apr.waiting.title')}</h3>
      <p className="text-body text-fg-muted">{next}</p>
      <p className="text-body text-fg-muted">{t('apr.waiting.nothingYet')}</p>
      {review.selfAssessmentNotes && (
        <div>
          <p className="text-meta font-medium text-fg-muted">{t('apr.result.yourNotes')}</p>
          <p className="mt-1 whitespace-pre-wrap text-body text-fg">{review.selfAssessmentNotes}</p>
        </div>
      )}
    </section>
  );
}

function Result({ review, breakdown, competencies, goals, appeal, locale }: {
  review: AppraisalReview; breakdown: ScoreBreakdown[]; competencies: CompetencyRating[]; goals: EmployeeGoal[];
  appeal: AppraisalAppeal | null; locale: string;
}) {
  const t = useT();
  const rows = breakdown.filter(b => b.weight > 0);
  return (
    <div className="space-y-5">
      <section className="flex items-center gap-4 rounded-lg border border-line bg-surface-sunken p-4" data-testid="result-score">
        <p className="text-h1 font-semibold tabular-nums text-fg" dir="ltr">{review.finalScore}</p>
        <div className="min-w-0">
          <p className="text-h4 text-fg">{ratingLabel(t, review.finalRating)}</p>
          <p className="text-meta text-fg-muted">{t('apr.result.outOf')}{review.publishedAt ? ` · ${t('apr.result.published', { date: formatCalendarDate(review.publishedAt, locale) })}` : ''}</p>
        </div>
      </section>

      {appeal && (
        <section className="space-y-1 rounded-lg border border-warning/30 bg-warning-soft p-4" data-testid="appeal-status">
          <p className="text-body font-semibold text-fg">{t('apr.appeal.status', { status: ['Submitted', 'UnderReview', 'Upheld', 'Rejected'].includes(appeal.status) ? t(`apr.appeal.state.${appeal.status}`) : appeal.status })}</p>
          <p className="text-body text-fg-muted">{appeal.appealReason}</p>
          {appeal.hrResponse && <p className="text-body text-fg"><span className="font-medium">{t('apr.appeal.hrReply')}</span> {appeal.hrResponse}</p>}
        </section>
      )}

      {rows.length > 0 && (
        <section aria-labelledby="breakdown-heading">
          <h3 id="breakdown-heading" className="mb-2 text-h4 text-fg">{t('apr.result.breakdown')}</h3>
          <div className="overflow-x-auto"><table className="w-full text-body">
            <thead><tr className="text-start text-meta text-fg-muted">
              <th scope="col" className="py-1.5 text-start font-medium">{t('apr.result.component')}</th>
              <th scope="col" className="py-1.5 text-end font-medium">{t('Score')}</th>
              <th scope="col" className="py-1.5 text-end font-medium">{t('apr.result.weight')}</th>
              <th scope="col" className="py-1.5 text-end font-medium">{t('apr.result.counts')}</th>
            </tr></thead>
            <tbody className="divide-y divide-line">
              {rows.map(b => (
                <tr key={b.component}>
                  <th scope="row" className="py-2 text-start font-normal text-fg">{scoreComponentLabel(t, b.component)}</th>
                  <td className="py-2 text-end tabular-nums" dir="ltr">{b.rawScore}</td>
                  <td className="py-2 text-end tabular-nums" dir="ltr">{b.weight}%</td>
                  <td className="py-2 text-end tabular-nums font-medium" dir="ltr">{b.weightedScore}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        </section>
      )}

      {competencies.length > 0 && (
        <section aria-labelledby="competency-heading">
          <h3 id="competency-heading" className="mb-2 text-h4 text-fg">{t('apr.result.competencies')}</h3>
          <ul className="divide-y divide-line">
            {competencies.map(c => (
              <li key={c.competencyId} className="flex items-center justify-between gap-3 py-2 text-body">
                <span className="min-w-0 text-fg">{c.competencyName}</span>
                <span className="shrink-0 tabular-nums text-fg-muted" dir="ltr">{c.selfRating} / {c.managerRating}</span>
              </li>
            ))}
          </ul>
          <p className="mt-1 text-meta text-fg-muted">{t('apr.result.competencyKey')}</p>
        </section>
      )}

      {goals.length > 0 && (
        <section aria-labelledby="goals-heading">
          <h3 id="goals-heading" className="mb-2 text-h4 text-fg">{t('Goals')}</h3>
          <ul className="divide-y divide-line">
            {goals.map(g => (
              <li key={g.id} className="flex items-center justify-between gap-3 py-2 text-body">
                <span className="min-w-0 text-fg">{g.title}</span>
                <span className="shrink-0 tabular-nums text-fg-muted" dir="ltr">{Math.round(g.achievementPct)}%</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {review.selfAssessmentNotes && (
        <section>
          <h3 className="mb-1 text-h4 text-fg">{t('apr.result.yourNotes')}</h3>
          <p className="whitespace-pre-wrap text-body text-fg-muted">{review.selfAssessmentNotes}</p>
        </section>
      )}

      <p className="text-meta text-fg-muted" data-testid="notes-withheld">{t('apr.result.notesWithheld')}</p>
    </div>
  );
}
