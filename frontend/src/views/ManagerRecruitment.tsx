'use client';

import { useCallback, useEffect, useState } from 'react';
import { Calendar, ClipboardList, Plus, Send } from 'lucide-react';
import { interviewsApi, requisitionsApi, type ManpowerRequisition, type MyInterview } from '../api/recruitment';
import { apiErrorReason, notifyApiError } from '../api/client';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import { useAuth } from '../contexts/AuthContext';
import { enumLabel } from '../i18n/enumLabel';
import { recruitmentAccess } from '../lib/recruitmentAccess';
import { interviewFeedbackRows, type FeedbackRow } from '../lib/recruitmentFixesC';

/**
 * The Recruitment screen of a line manager, who holds only two narrow keys. Two small jobs and nothing else: raise a requisition for their own
 * department (and submit it), and give feedback on the interviews they are named on. Candidates, offers and hiring stay with HR, and the API
 * refuses them to this audience too (see LegacyRolePermissionResolver.NarrowRecruitmentActions).
 */
export function ManagerRecruitment() {
  const { hasPermission } = useAuth();
  const access = recruitmentAccess(hasPermission);
  const t = useT();
  return (
    <div className="space-y-6 p-4 sm:p-6">
      <div>
        <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Hiring for my team')}</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          {t('Ask for a new position in your department, and give your feedback on the candidates you interview.')}
        </p>
      </div>
      {access.canRaiseRequisition && <MyRequisitions />}
      {access.canGiveFeedback && <MyInterviews />}
    </div>
  );
}

function MyRequisitions() {
  const t = useT();
  const [items, setItems] = useState<ManpowerRequisition[]>([]);
  const [departments, setDepartments] = useState<{ id: string; name: string; nameAr?: string }[]>([]);
  const [loadError, setLoadError] = useState('');
  const [open, setOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [acting, setActing] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [form, setForm] = useState({ departmentId: '', designationTitle: '', headCount: 1, priority: 'Medium', justification: '' });
  const { user } = useAuth();

  const load = useCallback(async () => {
    try {
      const [list, mine] = await Promise.all([requisitionsApi.list({ pageSize: 50 }), requisitionsApi.myDepartments()]);
      setItems(list.items); setDepartments(mine.items); setLoadError('');
    } catch (e) { setLoadError(apiErrorReason(e, t('Could not load your requisitions.'))); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  const create = async () => {
    const department = departments.find(d => d.id === form.departmentId);
    if (!department || !form.designationTitle.trim() || !form.justification.trim()) {
      setError(t('Department, position and the reason for the request are required.')); return;
    }
    setSaving(true); setError('');
    try {
      await requisitionsApi.create({
        departmentId: department.id, departmentName: department.name, designationTitle: form.designationTitle.trim(),
        headCount: Number(form.headCount) || 1, employmentType: 'Full-Time', priority: form.priority,
        justification: form.justification.trim(), requiredSkills: '', requestedByName: user?.fullName ?? '',
      });
      setOpen(false); setForm({ departmentId: '', designationTitle: '', headCount: 1, priority: 'Medium', justification: '' });
      await load();
    } catch (e) { setError(apiErrorReason(e, t('Failed to create requisition.'))); }
    finally { setSaving(false); }
  };

  const submit = async (id: string) => {
    setActing(id);
    try { await requisitionsApi.submit(id); await load(); } catch (e) { notifyApiError(e); }
    finally { setActing(null); }
  };

  return (
    <section aria-labelledby="my-requisitions-heading" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
      {/* On a phone the primary sits full width under the title (one line), beside it from 640px up. */}
      <div className="mb-3 flex flex-wrap items-center gap-x-2 gap-y-3">
        <ClipboardList className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
        <h2 id="my-requisitions-heading" className="min-w-0 flex-1 text-base font-semibold text-slate-900 dark:text-white">{t('My requisitions')}</h2>
        <button type="button" className="btn-primary flex w-full items-center justify-center gap-1.5 whitespace-nowrap text-sm sm:ms-auto sm:w-auto" onClick={() => setOpen(o => !o)} disabled={departments.length === 0}>
          <Plus className="h-3.5 w-3.5" />{t('New Requisition')}
        </button>
      </div>
      {loadError && <p role="alert" className="mb-2 text-sm text-rose-600">{loadError}</p>}
      {departments.length === 0 && !loadError && (
        <p className="mb-2 text-sm text-slate-500 dark:text-slate-400">{t('Your profile has no department yet, so a requisition cannot be raised. Ask HR to complete it.')}</p>
      )}
      {open && (
        <div className="mb-4 grid gap-3 rounded-lg bg-slate-50 p-3 sm:grid-cols-2 dark:bg-white/[0.03]">
          <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Department *')}
            <select className="select mt-1 w-full" value={form.departmentId} onChange={e => setForm(f => ({ ...f, departmentId: e.target.value }))}>
              <option value="">{t('Choose a department')}</option>
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </label>
          <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Position *')}
            <input className="input mt-1 w-full" value={form.designationTitle} onChange={e => setForm(f => ({ ...f, designationTitle: e.target.value }))} />
          </label>
          <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Head Count')}
            <input type="number" min={1} className="input mt-1 w-full" value={form.headCount} onChange={e => setForm(f => ({ ...f, headCount: Number(e.target.value) }))} />
          </label>
          <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Priority')}
            <select className="select mt-1 w-full" value={form.priority} onChange={e => setForm(f => ({ ...f, priority: e.target.value }))}>
              {['Low', 'Medium', 'High'].map(p => <option key={p} value={p}>{enumLabel(t, 'Status', p)}</option>)}
            </select>
          </label>
          <label className="text-xs font-medium text-slate-600 dark:text-slate-400 sm:col-span-2">{t('Why is this position needed? *')}
            <textarea className="input mt-1 w-full" rows={2} value={form.justification} onChange={e => setForm(f => ({ ...f, justification: e.target.value }))} />
          </label>
          {error && <p role="alert" className="text-xs text-rose-600 sm:col-span-2">{error}</p>}
          <div className="flex justify-end gap-2 sm:col-span-2">
            <button type="button" className="btn-secondary text-sm" onClick={() => setOpen(false)}>{t('Cancel')}</button>
            <button type="button" className="btn-primary text-sm" onClick={create} disabled={saving}>{saving ? t('Saving…') : t('Save as draft')}</button>
          </div>
        </div>
      )}
      <ul className="divide-y divide-slate-100 dark:divide-white/10">
        {items.length === 0 && <li className="py-3 text-sm text-slate-500 dark:text-slate-400">{t('You have not raised any requisitions yet.')}</li>}
        {items.map(r => (
          <li key={r.id} className="flex flex-wrap items-center gap-3 py-2.5 text-sm">
            <span className="font-medium text-slate-800 dark:text-slate-200">{r.requisitionNumber}</span>
            <span className="text-slate-600 dark:text-slate-300">{r.designationTitle} × {r.headCount}</span>
            <span className="text-xs text-slate-500 dark:text-slate-400">{r.departmentName}</span>
            <span className="badge ms-auto">{enumLabel(t, 'Status', r.status)}</span>
            {r.status === 'Draft' && (
              <button type="button" className="btn-secondary flex items-center gap-1 text-xs" onClick={() => submit(r.id)} disabled={acting === r.id}>
                <Send className="h-3 w-3" />{t('Submit for approval')}
              </button>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function MyInterviews() {
  const t = useT();
  const f = useFormat();
  const [items, setItems] = useState<MyInterview[]>([]);
  const [loadError, setLoadError] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);
  const [form, setForm] = useState({ overallRating: 4, recommendation: 'Proceed', feedbackNotes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  // What this person has already submitted (the API returns a panellist only their own scorecard) and the one-line confirmation after saving.
  const [mine, setMine] = useState<Record<string, FeedbackRow[]>>({});
  const [confirmation, setConfirmation] = useState<{ id: string; text: string } | null>(null);

  const load = useCallback(async () => {
    try { setItems((await interviewsApi.mine()).items); setLoadError(''); }
    catch (e) { setLoadError(apiErrorReason(e, t('Could not load your interviews.'))); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    let active = true;
    const missing = items.filter(i => !(i.id in mine)).slice(0, 30);
    if (missing.length === 0) return;
    void Promise.allSettled(missing.map(i => interviewsApi.get(i.id))).then(results => {
      if (!active) return;
      setMine(m => { const next = { ...m }; results.forEach((r, n) => { if (r.status === 'fulfilled') next[missing[n].id] = interviewFeedbackRows(r.value.feedbacks ?? []); }); return next; });
    });
    return () => { active = false; };
  }, [items]); // eslint-disable-line react-hooks/exhaustive-deps

  const send = async (id: string) => {
    if (!form.feedbackNotes.trim()) { setError(t('Write a short note on the candidate.')); return; }
    setSaving(true); setError('');
    try {
      const saved = await interviewsApi.recordFeedback(id, form) as { id?: string; interviewerName?: string; submittedAtUtc?: string } | undefined;
      const row: FeedbackRow = { id: saved?.id ?? `saved-${Date.now()}`, interviewer: saved?.interviewerName ?? '', rating: form.overallRating, recommendation: form.recommendation, notes: form.feedbackNotes.trim(), submittedAtUtc: saved?.submittedAtUtc ?? new Date().toISOString() };
      setMine(m => ({ ...m, [id]: [row, ...(m[id] ?? [])] }));
      setConfirmation({ id, text: t('Your feedback on {name} was saved. HR can now see it on the interview.', { name: items.find(i => i.id === id)?.candidateName ?? '' }) });
      setOpenId(null); setForm({ overallRating: 4, recommendation: 'Proceed', feedbackNotes: '' }); await load();
    }
    catch (e) { setError(apiErrorReason(e, t('Could not save your feedback.'))); }
    finally { setSaving(false); }
  };

  return (
    <section aria-labelledby="my-interviews-heading" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
      <div className="mb-3 flex items-center gap-2">
        <Calendar className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h2 id="my-interviews-heading" className="text-base font-semibold text-slate-900 dark:text-white">{t('My interviews')}</h2>
      </div>
      {loadError && <p role="alert" className="mb-2 text-sm text-rose-600">{loadError}</p>}
      <ul className="divide-y divide-slate-100 dark:divide-white/10">
        {items.length === 0 && !loadError && <li className="py-3 text-sm text-slate-500 dark:text-slate-400">{t('You are not on any interview panel yet.')}</li>}
        {items.map(i => (
          <li key={i.id} className="py-2.5 text-sm">
            <div className="flex flex-wrap items-center gap-3">
              <span className="font-medium text-slate-800 dark:text-slate-200">{i.candidateName}</span>
              <span className="text-slate-600 dark:text-slate-300">{i.jobTitle}</span>
              <span className="text-xs text-slate-500 dark:text-slate-400">{f.dateTime(i.scheduledAt)}</span>
              <span className="badge ms-auto">{enumLabel(t, 'Status', i.status)}</span>
              <button type="button" className="btn-secondary text-xs" onClick={() => { setOpenId(openId === i.id ? null : i.id); setError(''); }}>
                {(mine[i.id]?.length ?? 0) > 0 ? t('Add more feedback') : t('Give feedback')}
              </button>
            </div>
            {confirmation?.id === i.id && <p role="status" className="mt-2 rounded-lg bg-emerald-50 px-3 py-2 text-xs font-medium text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">{confirmation.text}</p>}
            {(mine[i.id]?.length ?? 0) > 0 && (
              <div className="mt-2 space-y-1.5 rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10" role="region" aria-label={t('Your feedback')}>
                <p className="font-semibold text-slate-700 dark:text-slate-200">{t('Your feedback')}</p>
                {mine[i.id].map(r => (
                  <div key={r.id} className="text-slate-600 dark:text-slate-300">
                    <p>{t('Rating')}: {r.rating}/5 · {t('Recommendation')}: {enumLabel(t, 'InterviewRecommendation', r.recommendation)} · {f.dateTime(r.submittedAtUtc)}</p>
                    {r.notes && <p className="mt-0.5 break-words"><bdi>{r.notes}</bdi></p>}
                  </div>
                ))}
              </div>
            )}
            {openId === i.id && (
              <div className="mt-3 grid gap-3 rounded-lg bg-slate-50 p-3 sm:grid-cols-2 dark:bg-white/[0.03]">
                <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Overall rating (1 to 5)')}
                  <select className="select mt-1 w-full" value={form.overallRating} onChange={e => setForm(v => ({ ...v, overallRating: Number(e.target.value) }))}>
                    {[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{n}</option>)}
                  </select>
                </label>
                <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Recommendation')}
                  <select className="select mt-1 w-full" value={form.recommendation} onChange={e => setForm(v => ({ ...v, recommendation: e.target.value }))}>
                    {['Proceed', 'Hold', 'Reject'].map(r => <option key={r} value={r}>{enumLabel(t, 'InterviewRecommendation', r)}</option>)}
                  </select>
                </label>
                <label className="text-xs font-medium text-slate-600 dark:text-slate-400 sm:col-span-2">{t('Your notes')}
                  <textarea className="input mt-1 w-full" rows={3} value={form.feedbackNotes} onChange={e => setForm(v => ({ ...v, feedbackNotes: e.target.value }))} />
                </label>
                {error && <p role="alert" className="text-xs text-rose-600 sm:col-span-2">{error}</p>}
                <div className="flex justify-end sm:col-span-2">
                  <button type="button" className="btn-primary text-sm" onClick={() => send(i.id)} disabled={saving}>{saving ? t('Saving…') : t('Send feedback')}</button>
                </div>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
