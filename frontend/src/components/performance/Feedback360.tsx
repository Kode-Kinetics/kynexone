'use client';

import { useCallback, useEffect, useState } from 'react';
import { Modal } from '../Modal';
import { Field, Select, Textarea } from '../ui/Field';
import { Badge } from '../ui/Badge';
import { Skeleton } from '../ui/Skeleton';
import { ResultBanner } from '../ui/ResultBanner';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { cyclesApi, feedbackApi } from '../../api/performance';
import type { AppraisalReview } from '../../api/performance';
import { appraisalRefusal } from '../../lib/appraisalsRefusal';

/** One entry as the API projects it: named entries for everyone in scope, anonymous ones only for the HR tier, without an author. */
interface Entry {
  id: string; reviewerName: string; reviewerRole: string; isAnonymous: boolean; score: number;
  strengths: string; improvements: string; comments: string;
}

export interface Cycle360 { enabled: boolean; open: boolean }

/** Which cycles collect 360 feedback, and which are still open for it, read once for the whole list. */
export function useCycles360(): Map<string, Cycle360> {
  const [map, setMap] = useState<Map<string, Cycle360>>(new Map());
  useEffect(() => {
    let cancelled = false;
    cyclesApi.listAll().then(all => {
      if (cancelled) return;
      setMap(new Map(all.map(c => [c.id, { enabled: c.enable360Feedback, open: c.status === 'Active' || c.status === 'InReview' }])));
    }).catch(() => undefined);
    return () => { cancelled = true; };
  }, []);
  return map;
}

const OPEN_REVIEW = ['Pending', 'SelfAssessmentDue', 'SelfAssessmentSubmitted', 'ManagerReview'];
export const feedbackOpenFor = (review: AppraisalReview, cycle: Cycle360 | undefined) => !!cycle?.open && OPEN_REVIEW.includes(review.status);

/**
 * The 360 feedback on one review: what colleagues have said, and the form to give your own. Giving is open only while the
 * cycle collects 360 and the review is still being written; reading stays open afterwards. Feedback is always given as
 * yourself, so a login that is not linked to an employee gets the API's own explanation.
 */
export function Feedback360Modal({ review, canGive, onClose }: { review: AppraisalReview; canGive: boolean; onClose: () => void }) {
  const t = useT();
  const { locale } = useLocale();
  const [entries, setEntries] = useState<Entry[] | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [form, setForm] = useState({ score: '', role: 'Peer', strengths: '', improvements: '', comments: '', anonymous: false });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [sent, setSent] = useState(false);
  const set = (k: keyof typeof form, v: string | boolean) => setForm(f => ({ ...f, [k]: v }));

  const load = useCallback(() => {
    setFailure(null);
    return feedbackApi.list360(review.id).then((r: Entry[]) => setEntries(r)).catch(e => setFailure(e ?? new Error('failed')));
  }, [review.id]);
  useEffect(() => { void load(); }, [load]);

  const submit = async () => {
    if (!form.score) { setError(t('apr.f360.scoreRequired')); return; }
    if (!form.strengths.trim() && !form.improvements.trim() && !form.comments.trim()) { setError(t('apr.f360.textRequired')); return; }
    setSaving(true); setError('');
    try {
      await feedbackApi.submit360({
        reviewId: review.id, reviewerRole: form.role, isAnonymous: form.anonymous, score: Number(form.score),
        strengths: form.strengths.trim(), improvements: form.improvements.trim(), comments: form.comments.trim(),
      });
      setSent(true); await load();
    } catch (e) { setError(appraisalRefusal(e, t, t('apr.f360.failed'), locale)); }
    finally { setSaving(false); }
  };

  const average = entries && entries.length > 0 ? Math.round((entries.reduce((a, e) => a + Number(e.score), 0) / entries.length) * 10) / 10 : null;
  const showForm = canGive && !sent;

  return (
    <Modal isOpen title={t('apr.f360.title', { name: review.employeeName })} onClose={onClose} closeLabel={t('Close')} size="lg" placement="end"
      footer={showForm ? (
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" disabled={saving} aria-busy={saving || undefined} onClick={submit}>{saving ? t('Submitting…') : t('apr.f360.submit')}</button>
        </div>
      ) : undefined}>
      <div className="space-y-5" data-testid="feedback-360">
        <p className="text-body text-fg-muted" data-purpose>{canGive ? t('apr.f360.purposeGive') : t('apr.f360.purposeRead')}</p>
        {sent && <ResultBanner changed={t('apr.f360.sent')} where={t('apr.f360.where', { name: review.employeeName })} onDismiss={() => setSent(false)} />}

        <section aria-labelledby="f360-entries">
          <div className="mb-2 flex flex-wrap items-center gap-2">
            <h3 id="f360-entries" className="text-h4 text-fg">{t('apr.f360.received')}</h3>
            {average !== null && <Badge tone="info">{t('apr.f360.average', { score: average })}</Badge>}
          </div>
          {failure != null ? <LoadFailedNotice error={failure} onRetry={() => void load()} />
            : entries === null ? <div role="status" aria-busy="true"><span className="sr-only">{t('Loading…')}</span><Skeleton className="h-16 w-full" /></div>
            : entries.length === 0 ? <p className="text-body text-fg-muted" data-testid="f360-none">{t('apr.f360.none')}</p>
            : (
              <ul className="space-y-2">
                {entries.map(e => (
                  <li key={e.id} className="space-y-1 rounded-lg border border-line p-3">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-body font-medium text-fg">{e.isAnonymous ? t('Anonymous') : e.reviewerName}</span>
                      <Badge tone="neutral">{t(`apr.f360.role.${e.reviewerRole}`)}</Badge>
                      <span className="text-meta tabular-nums text-fg-muted" dir="ltr">{e.score} / 5</span>
                    </div>
                    {e.strengths && <p className="text-body text-fg"><span className="font-medium">{t('apr.f360.strengths')}</span> {e.strengths}</p>}
                    {e.improvements && <p className="text-body text-fg"><span className="font-medium">{t('apr.f360.improvements')}</span> {e.improvements}</p>}
                    {e.comments && <p className="text-body text-fg-muted">{e.comments}</p>}
                  </li>
                ))}
              </ul>
            )}
        </section>

        {showForm && (
          <section className="space-y-4" aria-labelledby="f360-give">
            <h3 id="f360-give" className="text-h4 text-fg">{t('apr.f360.give')}</h3>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t('apr.f360.score')} required info={t('apr.f360.scoreInfo')}>
                <Select value={form.score} onChange={e => set('score', e.target.value)}>
                  <option value="">{t('apr.f360.scoreChoose')}</option>
                  {[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{t(`apr.f360.score.${n}`)}</option>)}
                </Select>
              </Field>
              <Field label={t('apr.f360.relationship')} info={t('apr.f360.relationshipInfo')}>
                <Select value={form.role} onChange={e => set('role', e.target.value)}>
                  {['Peer', 'CrossFunc', 'Customer'].map(r => <option key={r} value={r}>{t(`apr.f360.role.${r}`)}</option>)}
                </Select>
              </Field>
            </div>
            <Field label={t('apr.f360.strengths')} noInfo="free text"><Textarea rows={2} value={form.strengths} onChange={e => set('strengths', e.target.value)} /></Field>
            <Field label={t('apr.f360.improvements')} noInfo="free text"><Textarea rows={2} value={form.improvements} onChange={e => set('improvements', e.target.value)} /></Field>
            <Field label={t('apr.f360.comments')} noInfo="free text, optional"><Textarea rows={2} value={form.comments} onChange={e => set('comments', e.target.value)} /></Field>
            <label className="flex items-start gap-2 text-body text-fg">
              <input type="checkbox" className="mt-1" checked={form.anonymous} onChange={e => set('anonymous', e.target.checked)} />
              <span>{t('apr.f360.anonymous')}<span className="block text-meta text-fg-muted">{t('apr.f360.anonymousHint')}</span></span>
            </label>
            {error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
          </section>
        )}
        {!showForm && error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
      </div>
    </Modal>
  );
}
