'use client';

import { useState } from 'react';
import { Field } from '../ui/Field';
import { Modal } from '../Modal';
import { useAppToast } from '../ui/AppToast';
import { useLocale } from '../../contexts/LocaleContext';
import { apiErrorReason } from '../../api/client';
import { reviewsApi } from '../../api/performance';
import type { AppraisalReview } from '../../api/performance';

/**
 * HR's steps on one review (the rating-approver tier, performance.approve), each with a written reason the server
 * records in the review's audit trail:
 *   - Waive self-assessment: a self-assessment nobody will submit (long absence, no access) opens the manager review.
 *   - Exclude from cycle: a leaver or a wrong enrolment stops holding the cycle back.
 *   - Correct score: HR's override. After publication it withdraws the result, which is then published again and
 *     acknowledged again by the employee.
 * The server decides which step a review's status allows; the buttons mirror that so none is offered in vain.
 */
type Action = 'waive' | 'exclude' | 'correct';

const WAIVABLE = ['SelfAssessmentDue'];
const EXCLUDABLE = ['Pending', 'SelfAssessmentDue', 'SelfAssessmentSubmitted', 'ManagerReview', 'ManagerReviewComplete', 'Calibration'];
const CORRECTABLE = ['ManagerReviewComplete', 'Calibration', 'FinalApproval', 'Published', 'Acknowledged'];
const PUBLISHED = ['Published', 'Acknowledged'];
const SCORES = ['kpiScore', 'competencyScore', 'attendanceScore', 'productivityScore', 'feedbackScore', 'disciplineScore'] as const;
type ScoreKey = typeof SCORES[number];

const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const ghost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const primary = 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50';
const cancel = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';

export function ReviewHrActions({ review, onDone }: { review: AppraisalReview; onDone: () => void }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [action, setAction] = useState<Action | null>(null);
  const [reason, setReason] = useState('');
  const [scores, setScores] = useState<Record<ScoreKey, string>>({ kpiScore: '', competencyScore: '', attendanceScore: '', productivityScore: '', feedbackScore: '', disciplineScore: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const open = (next: Action) => {
    setAction(next); setReason(''); setError('');
    setScores({ kpiScore: '', competencyScore: '', attendanceScore: '', productivityScore: '', feedbackScore: '', disciplineScore: '' });
  };

  const scoreLabel: Record<ScoreKey, string> = {
    kpiScore: t('KPI Score (0–100)'), competencyScore: t('Competency Score (0–100)'), attendanceScore: t('Attendance Score (0–100)'),
    productivityScore: t('Productivity Score (0–100)'), feedbackScore: t('Feedback Score (0–100)'), disciplineScore: t('Discipline Score (0–100)'),
  };

  const submit = async () => {
    if (!action) return;
    if (!reason.trim()) { setError(t('Write the reason.')); return; }
    const body: Record<string, number> = {};
    if (action === 'correct') {
      for (const k of SCORES) {
        if (scores[k].trim() === '') continue;
        const n = Number(scores[k]);
        if (!Number.isFinite(n) || n < 0 || n > 100) { setError(t('Every score must be between 0 and 100.')); return; }
        body[k] = n;
      }
    }
    setSaving(true); setError('');
    try {
      if (action === 'waive') {
        await reviewsApi.waiveSelfAssessment(review.id, reason.trim());
        toast.success(t('Self-assessment waived. The manager review is open.'));
      } else if (action === 'exclude') {
        await reviewsApi.exclude(review.id, reason.trim());
        toast.success(t('{name} is excluded from this cycle.', { name: review.employeeName }));
      } else {
        await reviewsApi.overrideScore(review.id, { ...body, reason: reason.trim() });
        toast.success(PUBLISHED.includes(review.status)
          ? t('Score corrected and the published result withdrawn. Publish it again to re-issue it.')
          : t('Score corrected.'));
      }
      setAction(null); onDone();
    } catch (e) {
      setError(apiErrorReason(e, t('The action could not be recorded.')));
    } finally { setSaving(false); }
  };

  const title = action === 'waive' ? t('Waive self-assessment — {name}', { name: review.employeeName })
    : action === 'exclude' ? t('Exclude from cycle — {name}', { name: review.employeeName })
    : t('Correct score — {name}', { name: review.employeeName });
  const explanation = action === 'waive'
    ? t('The manager review opens without the employee’s self-assessment. Use this when the employee cannot or will not submit it (long absence, no system access).')
    : action === 'exclude'
      ? t('The employee is not appraised in this cycle and no longer holds it back. Use this for a leaver or someone who should not have been enrolled.')
      : PUBLISHED.includes(review.status)
        ? t('This result has been published. Correcting it withdraws it: publish it again and the employee acknowledges the corrected result.')
        : t('Leave a score blank to keep it. Scores are 0 to 100.');

  return (
    <>
      {WAIVABLE.includes(review.status) && (
        <button type="button" className={ghost} onClick={() => open('waive')}>{t('Waive self-assessment')}</button>
      )}
      {CORRECTABLE.includes(review.status) && (
        <button type="button" className={ghost} onClick={() => open('correct')}>{t('Correct score')}</button>
      )}
      {EXCLUDABLE.includes(review.status) && (
        <button type="button" className={ghost} onClick={() => open('exclude')}>{t('Exclude from cycle')}</button>
      )}
      {action && (
        <Modal isOpen title={title} onClose={() => setAction(null)} closeLabel={t('Close')} size={action === 'correct' ? 'lg' : 'md'}>
          <div className="space-y-4">
            <p className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">{explanation}</p>
            {action === 'correct' && (
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                {SCORES.map(k => (
                  <Field key={k} label={scoreLabel[k]} noInfo="a 0-100 score; the scale is in the label">
                    <input type="number" min={0} max={100} className={inp} value={scores[k]}
                      onChange={e => setScores(s => ({ ...s, [k]: e.target.value }))} />
                  </Field>
                ))}
              </div>
            )}
            <Field label={t('Reason (recorded in the audit trail) *')} noInfo="free-text reason; the explanation above says what it is for">
              <textarea className={inp} rows={3} value={reason} onChange={e => setReason(e.target.value)} />
            </Field>
            {error && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={cancel} onClick={() => setAction(null)}>{t('Cancel')}</button>
              <button type="button" className={primary} onClick={submit} disabled={saving}>{saving ? t('Recording…') : t('Record')}</button>
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}
