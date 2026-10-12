'use client';

import { useCallback, useEffect, useState } from 'react';
import { learningApi, type AssignmentView, type AttemptSummary, type Credential, type LearningPath } from '../../api/learning';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  assignmentChip, assignmentStatusLabel, completionSourceLabel, credentialStatusLabel, credentialTone, enrollmentStatusLabel, enrollmentTone,
  learningError, sourceLabel, todayIso,
} from '../../lib/learning';
import type { RecordsFilter } from '../../views/LearningPage';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { CertificateView } from './CertificateView';
import { EmptyBlock, ErrorBlock, Field, LoadingRows } from './LearningParts';

/**
 * Records (LRN-07/08/15): assignments and their courses with every attempt, HR's corrections — cancel an assignment,
 * reset a locked course, revoke or correct a certificate — each with a mandatory reason that goes to the audit trail,
 * and certificate verification by number + code. Rows are limited by the server to the people the user may see.
 */
export function RecordsPanel({ filter, canCorrect, canAssign }: { filter: RecordsFilter; canCorrect: boolean; canAssign: boolean }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [paths, setPaths] = useState<LearningPath[]>([]);
  const [pathId, setPathId] = useState(filter.pathId ?? '');
  const [status, setStatus] = useState(filter.status ?? '');
  const [rows, setRows] = useState<AssignmentView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<AssignmentView | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setRows(await learningApi.assignments({ pathId: pathId || undefined, status: status || undefined })); }
    catch (e) { setError(learningError(e, t)); }
  }, [pathId, status, t]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { void learningApi.paths().then(setPaths).catch(() => setPaths([])); }, []);
  useEffect(() => {
    if (!filter.assignmentId) return;
    void learningApi.assignment(filter.assignmentId).then(setOpen).catch((e) => toast.error(learningError(e, t)));
  }, [filter.assignmentId, t, toast]);

  return (
    <div className="space-y-5" data-testid="records-panel">
      <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="records-assignments">
        <div className="flex flex-wrap items-end justify-between gap-2">
          <h2 id="records-assignments" className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Assignments')}</h2>
          <div className="flex flex-wrap gap-2">
            <Field id="rec-path" label={t('Learning path')}>
              <select id="rec-path" className="input w-52 py-1 text-xs" value={pathId} onChange={(e) => setPathId(e.target.value)}>
                <option value="">{t('All paths')}</option>
                {paths.map((p) => <option key={p.id} value={p.id}>{locale === 'ar' && p.nameAr ? p.nameAr : p.name}</option>)}
              </select>
            </Field>
            <Field id="rec-status" label={t('Status')}>
              <select id="rec-status" className="input w-40 py-1 text-xs" value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="">{t('Any status')}</option>
                {(['Assigned', 'InProgress', 'Completed', 'Cancelled'] as const).map((s) => <option key={s} value={s}>{t(assignmentStatusLabel[s])}</option>)}
              </select>
            </Field>
          </div>
        </div>
        {error ? <ErrorBlock message={error} onRetry={() => void load()} />
          : !rows ? <LoadingRows label={t('Loading assignments')} />
            : rows.length === 0 ? <EmptyBlock title={t('No assignments match')} body={t('Change the filters, or assign a learning path.')} />
              : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[720px] text-xs" data-testid="records-table">
                    <thead className="text-slate-500 dark:text-slate-400">
                      <tr>
                        <th scope="col" className="py-2 text-start font-semibold">{t('Employee')}</th>
                        <th scope="col" className="py-2 text-start font-semibold">{t('Learning path')}</th>
                        <th scope="col" className="py-2 text-start font-semibold">{t('Status')}</th>
                        <th scope="col" className="py-2 text-start font-semibold">{t('Due')}</th>
                        <th scope="col" className="py-2 text-end font-semibold">{t('Progress')}</th>
                        <th scope="col" className="py-2"><span className="sr-only">{t('Actions')}</span></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                      {rows.map((a) => (
                        <tr key={a.id}>
                          <td className="py-2 pe-3"><span className="font-semibold">{a.employeeName}</span> <span className="text-slate-500" dir="ltr">{a.employeeCode}</span></td>
                          <td className="py-2 pe-3">{locale === 'ar' && a.pathNameAr ? a.pathNameAr : a.pathName}{a.cycle > 1 ? ` · ${t('Cycle {n}', { n: a.cycle })}` : ''}</td>
                          <td className="py-2 pe-3">{(() => { const chip = assignmentChip(a.status, a.isOverdue, a.awaitingVerification); return <StatusChip label={t(chip.label)} tone={chip.tone} />; })()}</td>
                          <td className="py-2 pe-3 tabular-nums">{a.dueDate ? fx.date(a.dueDate) : t('No due date')}</td>
                          <td className="py-2 text-end tabular-nums">{t('{done} of {total}', { done: a.requiredCompleted, total: a.requiredCount })}</td>
                          <td className="py-2 text-end"><button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setOpen(a)}>{t('Open')}</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
      </section>

      <CredentialsSection canCorrect={canCorrect} />
      <VerifySection />

      {open && (
        <AssignmentDetail assignment={open} canCorrect={canCorrect} canAssign={canAssign}
          onClose={() => setOpen(null)} onChanged={async (next) => { setOpen(next); await load(); }} />
      )}
    </div>
  );
}

/** A required-reason confirmation: the reason is stored in the audit trail with the action. */
function ReasonDialog({ title, body, confirmLabel, onCancel, onConfirm, extra }: {
  title: string; body: string; confirmLabel: string; onCancel: () => void; onConfirm: (reason: string) => Promise<void>; extra?: React.ReactNode;
}) {
  const { t } = useLocale();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const confirm = async () => {
    if (reason.trim().length < 3) { setError(t('Give a reason (it is kept in the audit trail).')); return; }
    setBusy(true);
    try { await onConfirm(reason.trim()); } finally { setBusy(false); }
  };
  return (
    <Modal isOpen size="sm" title={title} onClose={onCancel}
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onCancel} disabled={busy}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void confirm()} disabled={busy} data-testid="reason-confirm">{busy ? t('Saving...') : confirmLabel}</button>
        </div>
      )}>
      <div className="space-y-3">
        <p className="text-sm text-slate-600 dark:text-slate-300">{body}</p>
        {extra}
        <Field id="reason-text" label={t('Reason')} error={error}>
          <textarea id="reason-text" className="input w-full" rows={3} value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} aria-describedby={error ? 'reason-text-error' : undefined} />
        </Field>
      </div>
    </Modal>
  );
}

function AssignmentDetail({ assignment: a, canCorrect, canAssign, onClose, onChanged }: {
  assignment: AssignmentView; canCorrect: boolean; canAssign: boolean; onClose: () => void; onChanged: (next: AssignmentView) => Promise<void>;
}) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [attempts, setAttempts] = useState<Record<string, AttemptSummary[]>>({});
  const [resetFor, setResetFor] = useState<string | null>(null);
  const [rejectFor, setRejectFor] = useState<string | null>(null);
  const [verifying, setVerifying] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const live = a.status === 'Assigned' || a.status === 'InProgress';

  useEffect(() => {
    for (const item of a.items.filter((i) => i.completionRule === 'Assessment' && i.attemptsUsed > 0))
      void learningApi.attempts(item.id).then((list) => setAttempts((m) => ({ ...m, [item.id]: list }))).catch(() => undefined);
  }, [a]);

  return (
    <>
      <Modal isOpen size="lg" title={t('{name} — {path}', { name: a.employeeName, path: locale === 'ar' && a.pathNameAr ? a.pathNameAr : a.pathName })} onClose={onClose}
        footer={canAssign && live ? (
          <div className="flex justify-end"><button type="button" className="btn-secondary" onClick={() => setCancelling(true)}>{t('Cancel this assignment')}</button></div>
        ) : undefined}>
        <div className="space-y-3 text-sm" data-testid="assignment-detail">
          <p className="text-xs text-slate-500">
            {t(sourceLabel[a.source] ?? a.source)}{' · '}{t('Assigned {date}', { date: fx.date(a.assignedAtUtc) })}
            {a.dueDate ? ` · ${t('Due {date}', { date: fx.date(a.dueDate) })}` : ''}
            {a.isMandatory ? ` · ${t('Mandatory')}` : ''}
            {a.cancelReason ? ` · ${t('Cancelled: {reason}', { reason: a.cancelReason })}` : ''}
          </p>
          <ol className="space-y-2">
            {a.items.map((item) => (
              <li key={item.id} className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-semibold">{t('{n}. {course}', { n: item.sequence, course: locale === 'ar' && item.titleAr ? item.titleAr : item.title })}</span>
                  <StatusChip label={t(enrollmentStatusLabel[item.status])} tone={enrollmentTone(item.status)} />
                </div>
                <p className="mt-1 text-xs text-slate-500">
                  {item.completedAtUtc && item.status === 'PendingVerification'
                    ? t('Reported complete {date} · {how}', { date: fx.date(item.completedAtUtc), how: t(completionSourceLabel[item.completionSource!]) })
                    : item.completedAtUtc
                    ? t('Completed {date} · {how}', { date: fx.date(item.completedAtUtc), how: t(completionSourceLabel[item.completionSource!]) })
                    : item.firstLaunchedAtUtc ? t('Opened {date}', { date: fx.date(item.firstLaunchedAtUtc) }) : t('Not opened yet')}
                  {item.completionRule === 'Assessment' ? ` · ${t('{used} of {allowed} attempts used', { used: item.attemptsUsed, allowed: item.attemptAllowance })}` : ''}
                </p>
                {(attempts[item.id] ?? []).length > 0 && (
                  <ul className="mt-1 space-y-0.5 text-xs text-slate-600 dark:text-slate-300">
                    {attempts[item.id].map((x) => (
                      <li key={x.id}>{x.status === 'Passed'
                        ? t('Attempt {n}: {score}, passed', { n: x.attemptNo, score: fx.percent(x.scorePercent ?? 0) })
                        : x.status === 'Failed'
                          ? t('Attempt {n}: {score}, not passed', { n: x.attemptNo, score: fx.percent(x.scorePercent ?? 0) })
                          : t('Attempt {n}: in progress', { n: x.attemptNo })}</li>
                    ))}
                  </ul>
                )}
                {canCorrect && item.status === 'Locked' && (
                  <button type="button" className="btn-primary mt-2 text-xs" onClick={() => setResetFor(item.id)} data-testid="reset-attempts">{t('Reset attempts')}</button>
                )}
                {item.status === 'PendingVerification' && (
                  <div className="mt-2 space-y-2 rounded-lg bg-amber-50 p-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-100" data-testid="pending-verification">
                    <p>{t('The course content reported this as complete. Check it (for example the attendance or the unit’s own report) before the certificate is issued. You cannot verify your own record.')}</p>
                    {canCorrect && (
                      <div className="flex flex-wrap gap-2">
                        <button type="button" className="btn-primary text-xs" disabled={verifying !== null} data-testid="verify-completion"
                          onClick={async () => {
                            setVerifying(item.id);
                            try { const next = await learningApi.verifyCompletion(item.id); toast.success(t('Completion verified. The certificate is issued.')); await onChanged(next); }
                            catch (e) { toast.error(learningError(e, t)); }
                            finally { setVerifying(null); }
                          }}>
                          {verifying === item.id ? t('Saving...') : t('Verify completion')}
                        </button>
                        <button type="button" className="btn-secondary text-xs" onClick={() => setRejectFor(item.id)} data-testid="reject-completion">{t('Reject')}</button>
                      </div>
                    )}
                  </div>
                )}
              </li>
            ))}
          </ol>
        </div>
      </Modal>
      {resetFor && (
        <ReasonDialog title={t('Reset attempts')} confirmLabel={t('Reset attempts')}
          body={t('The employee gets another full set of attempts. Their earlier attempts stay on record.')}
          onCancel={() => setResetFor(null)}
          onConfirm={async (reason) => {
            try { const next = await learningApi.resetAttempts(resetFor, reason); toast.success(t('Attempts reset.')); setResetFor(null); await onChanged(next); }
            catch (e) { toast.error(learningError(e, t)); }
          }} />
      )}
      {rejectFor && (
        <ReasonDialog title={t('Reject the reported completion')} confirmLabel={t('Reject')}
          body={t('The course goes back to in progress and no certificate is issued. What the unit reported stays on record.')}
          onCancel={() => setRejectFor(null)}
          onConfirm={async (reason) => {
            try { const next = await learningApi.rejectCompletion(rejectFor, reason); toast.success(t('Completion rejected.')); setRejectFor(null); await onChanged(next); }
            catch (e) { toast.error(learningError(e, t)); }
          }} />
      )}
      {cancelling && (
        <ReasonDialog title={t('Cancel this assignment')} confirmLabel={t('Cancel assignment')}
          body={t('The employee no longer needs to complete this path. Completed courses and certificates are kept.')}
          onCancel={() => setCancelling(false)}
          onConfirm={async (reason) => {
            try { const next = await learningApi.cancelAssignment(a.id, reason); toast.success(t('Assignment cancelled.')); setCancelling(false); await onChanged(next); }
            catch (e) { toast.error(learningError(e, t)); }
          }} />
      )}
    </>
  );
}

function CredentialsSection({ canCorrect }: { canCorrect: boolean }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [status, setStatus] = useState('');
  const [rows, setRows] = useState<Credential[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [viewing, setViewing] = useState<Credential | null>(null);
  const [revoking, setRevoking] = useState<Credential | null>(null);
  const [correcting, setCorrecting] = useState<Credential | null>(null);
  const [completedOn, setCompletedOn] = useState('');
  const [score, setScore] = useState('');
  const [redoBy, setRedoBy] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try { setRows(await learningApi.credentials({ status: status || undefined })); }
    catch (e) { setError(learningError(e, t)); }
  }, [status, t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="records-certs" data-testid="records-credentials">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <h2 id="records-certs" className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Certificates')}</h2>
        <Field id="cert-status" label={t('Status')}>
          <select id="cert-status" className="input w-40 py-1 text-xs" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">{t('Any status')}</option>
            {(['Active', 'Expired', 'Revoked', 'Superseded'] as const).map((s) => <option key={s} value={s}>{t(credentialStatusLabel[s])}</option>)}
          </select>
        </Field>
      </div>
      {error ? <ErrorBlock message={error} onRetry={() => void load()} />
        : !rows ? <LoadingRows label={t('Loading certificates')} rows={2} />
          : rows.length === 0 ? <p className="text-xs text-slate-500">{t('No certificates match.')}</p>
            : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[720px] text-xs">
                  <thead className="text-slate-500 dark:text-slate-400">
                    <tr>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Employee')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Course')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Certificate number')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Valid until')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Status')}</th>
                      <th scope="col" className="py-2"><span className="sr-only">{t('Actions')}</span></th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                    {rows.map((c) => (
                      <tr key={c.id}>
                        <td className="py-2 pe-3 font-semibold">{c.employeeName}</td>
                        <td className="py-2 pe-3">{locale === 'ar' && c.courseTitleAr ? c.courseTitleAr : c.courseTitle}</td>
                        <td className="py-2 pe-3 font-mono" dir="ltr">{c.certificateNumber}</td>
                        <td className="py-2 pe-3 tabular-nums">{c.expiresOn ? fx.date(c.expiresOn) : t('Does not expire')}</td>
                        <td className="py-2 pe-3"><StatusChip label={t(credentialStatusLabel[c.status])} tone={credentialTone(c.status)} /></td>
                        <td className="py-2">
                          <div className="flex justify-end gap-1.5">
                            <button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setViewing(c)}>{t('View')}</button>
                            {canCorrect && (c.status === 'Active' || c.status === 'Expired') && (
                              <>
                                <button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => { setCompletedOn(c.completedOn); setScore(c.scorePercent?.toString() ?? ''); setCorrecting(c); }}>{t('Correct')}</button>
                                <button type="button" className="text-rose-600 hover:underline" onClick={() => { setRedoBy(addDays(todayIso(), 14)); setRevoking(c); }}>{t('Revoke')}</button>
                              </>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
      {viewing && <Modal isOpen size="lg" title={t('Certificate')} onClose={() => setViewing(null)}><CertificateView credential={viewing} /></Modal>}
      {revoking && (
        <ReasonDialog title={t('Revoke certificate {number}', { number: revoking.certificateNumber })} confirmLabel={t('Revoke')}
          body={t('The certificate stays on record, marked revoked. This cannot be undone. The course is reopened for the employee, to be completed again by the date below; that completion issues a new certificate.')}
          extra={(
            <Field id="revoke-due" label={t('Complete again by')}>
              <input id="revoke-due" type="date" className="input w-full" value={redoBy} min={todayIso()} onChange={(e) => setRedoBy(e.target.value)} data-testid="revoke-due" />
            </Field>
          )}
          onCancel={() => setRevoking(null)}
          onConfirm={async (reason) => {
            if (!redoBy) { toast.error(t('Choose the date by which the course must be completed again.')); return; }
            try { await learningApi.revoke(revoking.id, reason, redoBy); toast.success(t('Certificate revoked. The course is reopened.')); setRevoking(null); await load(); }
            catch (e) { toast.error(learningError(e, t)); }
          }} />
      )}
      {correcting && (
        <ReasonDialog title={t('Correct certificate {number}', { number: correcting.certificateNumber })} confirmLabel={t('Issue corrected certificate')}
          body={t('A corrected certificate gets a new number and replaces this one, which stays on record as replaced.')}
          extra={(
            <div className="grid grid-cols-2 gap-2">
              <Field id="corr-date" label={t('Completed on')}><input id="corr-date" type="date" className="input w-full" value={completedOn} max={new Date().toISOString().slice(0, 10)} onChange={(e) => setCompletedOn(e.target.value)} /></Field>
              <Field id="corr-score" label={t('Score (%)')} optional><input id="corr-score" type="number" min={0} max={100} className="input w-full" value={score} onChange={(e) => setScore(e.target.value)} /></Field>
            </div>
          )}
          onCancel={() => setCorrecting(null)}
          onConfirm={async (reason) => {
            try {
              await learningApi.correct(correcting.id, { reason, completedOn: completedOn || null, scorePercent: score === '' ? null : Number(score) });
              toast.success(t('Corrected certificate issued.'));
              setCorrecting(null);
              await load();
            } catch (e) { toast.error(learningError(e, t)); }
          }} />
      )}
    </section>
  );
}

function VerifySection() {
  const { t } = useLocale();
  const [number, setNumber] = useState('');
  const [code, setCode] = useState('');
  const [result, setResult] = useState<Credential | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const verify = async () => {
    setBusy(true);
    setError(null);
    setResult(null);
    try { setResult(await learningApi.verify(number.trim(), code.trim())); }
    catch { setError(t('No certificate matches that number and code.')); }
    finally { setBusy(false); }
  };
  return (
    <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="records-verify">
      <h2 id="records-verify" className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Verify a certificate')}</h2>
      <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => { e.preventDefault(); void verify(); }}>
        <Field id="ver-number" label={t('Certificate number')}><input id="ver-number" className="input w-48" dir="ltr" value={number} onChange={(e) => setNumber(e.target.value)} /></Field>
        <Field id="ver-code" label={t('Verification code')}><input id="ver-code" className="input w-40" dir="ltr" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
        <button type="submit" className="btn-secondary" disabled={busy || !number.trim() || !code.trim()}>{busy ? t('Checking…') : t('Verify')}</button>
      </form>
      {error && <p role="alert" className="text-xs text-rose-600">{error}</p>}
      {result && (
        <p role="status" className="text-sm">
          {t('{number} belongs to {name} for "{course}" and is {status}.', {
            number: result.certificateNumber, name: result.employeeName, course: result.courseTitle, status: t(credentialStatusLabel[result.status]),
          })}
        </p>
      )}
    </section>
  );
}

function addDays(isoDate: string, days: number): string {
  const d = new Date(`${isoDate}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
