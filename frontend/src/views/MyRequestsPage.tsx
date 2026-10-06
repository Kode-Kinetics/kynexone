'use client';

import { useCallback, useEffect, useState } from 'react';
import { essActionsApi, essApi, type EssHrRequest, type EssHrRequestCategory, type EssHrRequestDetail } from '@/src/api/ess';
import { StatusChip } from '@/src/components/StatusChip';
import { EssCard, EssEmpty, EssField, EssLoadError, EssNotice, EssPageHeader, essInput, essPrimaryButton, useEssDate, useCanWriteEss, EssReadOnly } from '@/src/components/ess/EssParts';
import { useLocale } from '@/src/contexts/LocaleContext';
import { hrRequestStatus } from '@/src/lib/essSelfService';
import { fillTemplate } from '@/src/lib/gradeLoanLimits';
import { requestFailureReason } from '@/src/lib/requestFailure';

const GENERAL = 'General HR';
const blankForm = { categoryId: '', subject: '', description: '' };

/**
 * Employee self-service: raise a request to HR, follow my requests, and read and add to the
 * conversation on each. Gated on ess.read; every call is limited to the caller's own requests.
 */
export function MyRequestsPage() {
  const { t } = useLocale();
  const fmtDate = useEssDate();
  const canWrite = useCanWriteEss();
  const [categories, setCategories] = useState<EssHrRequestCategory[]>([]);
  const [requests, setRequests] = useState<EssHrRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [form, setForm] = useState(blankForm);
  const [submitting, setSubmitting] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [thread, setThread] = useState<EssHrRequestDetail | null>(null);
  const [threadError, setThreadError] = useState<string | null>(null);
  const [reply, setReply] = useState('');
  const [replying, setReplying] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const [c, r] = await Promise.all([essActionsApi.hrRequestCategories(), essApi.hrRequests()]);
      setCategories(c);
      setRequests(r);
    } catch (e) {
      setLoadError(e);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const set = <K extends keyof typeof blankForm>(k: K, v: (typeof blankForm)[K]) => setForm((f) => ({ ...f, [k]: v }));

  const openThread = async (id: string) => {
    setThreadError(null);
    setReply('');
    try {
      setThread(await essApi.hrRequestDetail(id));
    } catch (err) {
      setThread(null);
      setThreadError(t(requestFailureReason(err)));
    }
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setNotice(null);
    if (!form.subject.trim() || !form.description.trim()) {
      setNotice({ tone: 'error', text: t('Give your request a subject and describe what you need.') });
      return;
    }
    const category = categories.find((c) => c.id === form.categoryId);
    setSubmitting(true);
    try {
      const created = await essApi.createHrRequest({
        categoryId: category?.id,
        categoryName: category?.name ?? GENERAL,
        subject: form.subject.trim(),
        description: form.description.trim(),
        priority: 'Normal',
      }) as { id?: string };
      setForm(blankForm);
      setNotice({ tone: 'ok', text: t('Your request was sent to HR.') });
      await load();
      if (created?.id) await openThread(created.id);
    } catch (err) {
      setNotice({ tone: 'error', text: t(requestFailureReason(err)) });
    } finally {
      setSubmitting(false);
    }
  };

  const sendReply = async () => {
    if (!thread || !reply.trim()) return;
    setReplying(true);
    setThreadError(null);
    try {
      await essApi.addHrRequestComment(thread.request.id, reply.trim());
      setReply('');
      setThread(await essApi.hrRequestDetail(thread.request.id));
    } catch (err) {
      setThreadError(t(requestFailureReason(err)));
    } finally {
      setReplying(false);
    }
  };

  return (
    <div className="space-y-4">
      <EssPageHeader title={t('My HR Requests')} subtitle={t('Ask HR for something, follow your requests, and reply to HR on each one.')} />

      {loading ? (
        <div className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading your requests')} />
      ) : loadError ? (
        <EssLoadError error={loadError} onRetry={() => void load()} />
      ) : (
        <>
          {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}
          <div className="grid gap-4 lg:grid-cols-[22rem_1fr]">
            <EssCard title={t('Raise a request')} testId="my-requests-new">
              {!canWrite ? <EssReadOnly /> : (
              <form className="space-y-3" onSubmit={(e) => void submit(e)}>
                <EssField label={t('What is it about?')}>
                  <select className={essInput} value={form.categoryId} onChange={(e) => set('categoryId', e.target.value)}>
                    <option value="">{t('General HR')}</option>
                    {categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                  </select>
                </EssField>
                <EssField label={t('Subject')}>
                  <input className={essInput} value={form.subject} maxLength={200} onChange={(e) => set('subject', e.target.value)} />
                </EssField>
                <EssField label={t('What do you need?')}>
                  <textarea className={essInput} rows={4} value={form.description} onChange={(e) => set('description', e.target.value)} />
                </EssField>
                <button type="submit" disabled={submitting} className={`${essPrimaryButton} w-full`}>
                  {submitting ? t('Sending…') : t('Send to HR')}
                </button>
              </form>
              )}
            </EssCard>

            <div className="space-y-4">
              <EssCard title={t('My requests')} testId="my-requests-list">
                {requests.length === 0 ? (
                  <EssEmpty text={t('You have not raised any requests yet.')} />
                ) : (
                  <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                    {requests.map((r) => {
                      const s = hrRequestStatus(r.responseStatus);
                      const open = thread?.request.id === r.id;
                      return (
                        <li key={r.id}>
                          <button type="button" onClick={() => void openThread(r.id)} aria-expanded={open}
                            className={`flex w-full flex-wrap items-center justify-between gap-2 rounded-lg px-2 py-2.5 text-start hover:bg-slate-50 dark:hover:bg-white/[0.04] ${open ? 'bg-slate-50 dark:bg-white/[0.04]' : ''}`}>
                            <span className="min-w-0">
                              <span className="block text-sm font-semibold text-slate-800 dark:text-slate-100">{r.subject}</span>
                              <span className="block text-xs text-slate-500 dark:text-slate-400">
                                {fillTemplate(t('{category}, raised on {date}'), { category: r.categoryName, date: fmtDate(r.createdAtUtc) })}
                              </span>
                            </span>
                            <StatusChip label={t(s.label)} tone={s.tone} dot />
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </EssCard>

              {threadError && <EssNotice tone="error">{threadError}</EssNotice>}
              {thread && (
                <EssCard title={thread.request.subject} testId="my-requests-thread">
                  <p className="whitespace-pre-wrap text-sm text-slate-700 dark:text-slate-300">{thread.request.description}</p>
                  <h3 className="mt-4 text-xs font-bold text-slate-600 dark:text-slate-300">{t('Conversation with HR')}</h3>
                  {thread.comments.length === 0 ? (
                    <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('No replies yet. HR will reply here.')}</p>
                  ) : (
                    <ul className="mt-2 space-y-2">
                      {thread.comments.map((c) => (
                        <li key={c.id} className={`rounded-xl p-3 text-sm ${c.authorType === 'HR'
                          ? 'bg-sapphire/[0.06] dark:bg-cyanAccent/[0.08]'
                          : 'bg-slate-50 dark:bg-white/[0.03]'}`}>
                          <p className="text-[11px] font-semibold text-slate-500 dark:text-slate-400">
                            {c.authorType === 'HR'
                              ? fillTemplate(t('HR ({name}), {date}'), { name: c.authorName, date: fmtDate(c.createdAtUtc) })
                              : fillTemplate(t('You, {date}'), { date: fmtDate(c.createdAtUtc) })}
                          </p>
                          <p className="whitespace-pre-wrap text-slate-800 dark:text-slate-100">{c.comment}</p>
                        </li>
                      ))}
                    </ul>
                  )}
                  {canWrite && (
                  <div className="mt-3 space-y-2">
                    <EssField label={t('Add a comment')}>
                      <textarea className={essInput} rows={2} value={reply} onChange={(e) => setReply(e.target.value)} />
                    </EssField>
                    <button type="button" disabled={replying || !reply.trim()} onClick={() => void sendReply()} className={essPrimaryButton}>
                      {replying ? t('Sending…') : t('Send comment')}
                    </button>
                  </div>
                  )}
                </EssCard>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
