'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, Loader2, RefreshCw, Search, ShieldAlert, UserPlus } from 'lucide-react';
import {
  employeeDraftsApi,
  type EmployeeDraftListItem,
  type EmployeeDraftListResponse,
  type EmployeeDraftReview,
} from '../api/employeeDrafts';
import { notActivatableFromError } from '../api/employees';
import { establishmentBlockFromError, type EstablishmentBlockedPayload } from '../api/establishment';
import { DraftPlacementFix } from '../components/DraftPlacementFix';
import { EstablishmentBlockedModal } from '../components/EstablishmentBlockedModal';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import {
  DRAFT_FILTERS,
  approveDisabledReason,
  canDecideDraft,
  draftRequestFailureReason,
  draftStatusChip,
  isClosedDraftError,
  isOpenDraft,
  nextDraftAction,
  rejectionReasonError,
  type DraftFilterKey,
} from '../lib/newHireReview';

const PAGE_SIZE = 25;

const fmtDate = (s?: string | null) =>
  s ? new Date(s).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : 'Not set';
const fmtDateTime = (s?: string | null) =>
  s ? new Date(s).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '';

function placement(row: Pick<EmployeeDraftListItem, 'department' | 'designation' | 'branch'>): string {
  return [row.designation, row.department, row.branch].filter((x) => x && x.trim()).join(' · ') || 'No placement yet';
}

type Notice = { text: string; employeeId?: number | null };

/**
 * New hires: every accepted offer and prepared hire on its way to becoming an employee. The default
 * view is the exception list (what waits on a checker); each row offers one next action, and the
 * review shows, before anyone presses Approve, every reason activation would be refused.
 */
export function NewHiresPage() {
  const [filter, setFilter] = useState<DraftFilterKey>('awaiting');
  const [search, setSearch] = useState('');
  const [appliedSearch, setAppliedSearch] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<EmployeeDraftListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [notice, setNotice] = useState<Notice | null>(null);
  const [rowBusy, setRowBusy] = useState<string | null>(null);
  const requestSeq = useRef(0);

  const [selected, setSelected] = useState<EmployeeDraftListItem | null>(null);
  const [review, setReview] = useState<EmployeeDraftReview | null>(null);
  const [reviewLoading, setReviewLoading] = useState(false);
  const [reviewError, setReviewError] = useState('');
  const [reason, setReason] = useState('');
  const [decisionError, setDecisionError] = useState('');
  const [deciding, setDeciding] = useState<'approve' | 'reject' | 'withdraw' | null>(null);
  const [establishmentBlock, setEstablishmentBlock] = useState<{ block: EstablishmentBlockedPayload; name: string } | null>(null);

  const load = useCallback(async () => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setLoadError('');
    try {
      const res = await employeeDraftsApi.list({ status: filter, search: appliedSearch || undefined, page, pageSize: PAGE_SIZE });
      if (seq !== requestSeq.current) return;
      setData(res);
    } catch (err) {
      if (seq !== requestSeq.current) return;
      // Never show an outage as "no new hires": drop the rows and say why.
      setData(null);
      setLoadError(draftRequestFailureReason(err));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [filter, appliedSearch, page]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { setPage(1); }, [filter, appliedSearch]);

  const loadReview = useCallback(async (id: string) => {
    setReviewLoading(true);
    setReviewError('');
    try {
      setReview(await employeeDraftsApi.get(id));
    } catch (err) {
      setReview(null);
      setReviewError(draftRequestFailureReason(err));
    } finally {
      setReviewLoading(false);
    }
  }, []);

  const openReview = (row: EmployeeDraftListItem) => {
    setSelected(row);
    setReview(null);
    setReason('');
    setDecisionError('');
    void loadReview(row.id);
  };

  const closeReview = () => {
    if (deciding) return;
    setSelected(null);
    setReview(null);
    setReviewError('');
    setDecisionError('');
  };

  const submitDraft = async (row: EmployeeDraftListItem) => {
    setRowBusy(row.id);
    setNotice(null);
    try {
      await employeeDraftsApi.submit(row.id);
      setNotice({ text: `${row.name} was sent for HR approval.` });
    } catch (err) {
      setNotice({ text: `${row.name} could not be sent: ${draftRequestFailureReason(err)}` });
    } finally {
      setRowBusy(null);
      void load();
    }
  };

  const approve = async () => {
    if (!selected) return;
    setDeciding('approve');
    setDecisionError('');
    try {
      const employee = await employeeDraftsApi.approve(selected.id);
      setNotice({ text: `${selected.name} is now employee ${employee.employeeCode}.`, employeeId: employee.id });
      setSelected(null);
      setReview(null);
      void load();
    } catch (err) {
      const blocked = notActivatableFromError(err);
      const budget = establishmentBlockFromError(err);
      if (blocked) {
        const items = blocked.blocking.map((i) => i.label).join(', ');
        setDecisionError(`Activation was refused. Still missing: ${items || blocked.message}.`);
        void loadReview(selected.id);
      } else if (budget) {
        setEstablishmentBlock({ block: budget, name: selected.name });
      } else {
        setDecisionError(draftRequestFailureReason(err));
        if (isClosedDraftError(err)) { void load(); void loadReview(selected.id); }
      }
    } finally {
      setDeciding(null);
    }
  };

  const reject = async () => {
    if (!selected) return;
    const invalid = rejectionReasonError(reason);
    if (invalid) { setDecisionError(invalid); return; }
    setDeciding('reject');
    setDecisionError('');
    try {
      await employeeDraftsApi.reject(selected.id, reason.trim());
      setNotice({ text: `${selected.name} was rejected. The reason is kept on the record.` });
      setSelected(null);
      setReview(null);
      void load();
    } catch (err) {
      setDecisionError(draftRequestFailureReason(err));
      if (isClosedDraftError(err)) { void load(); void loadReview(selected.id); }
    } finally {
      setDeciding(null);
    }
  };

  const withdraw = async () => {
    if (!selected) return;
    setDeciding('withdraw');
    setDecisionError('');
    try {
      await employeeDraftsApi.cancel(selected.id, reason.trim() || undefined);
      setNotice({ text: `${selected.name}'s draft was withdrawn.` });
      setSelected(null);
      setReview(null);
      void load();
    } catch (err) {
      setDecisionError(draftRequestFailureReason(err));
      if (isClosedDraftError(err)) { void load(); void loadReview(selected.id); }
    } finally {
      setDeciding(null);
    }
  };

  const counts = data?.counts;
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
  const summary = review?.summary ?? selected;
  const check = review?.activationCheck ?? null;
  const problemCount = check ? check.problems.length : (review && !review.activationCheck ? 0 : null);
  const disabledReason = summary
    ? approveDisabledReason(summary.canApprove, summary.approveBlockedReason, problemCount)
    : null;
  const canDecide = !!summary && canDecideDraft(summary.status, summary.canApprove);
  // Placement problems the reviewer can fix here: an unmatched name, or a branch and department in
  // different legal entities.
  const placementProblems = Array.from(new Set((check?.problems ?? []).flatMap((p) =>
    p.key === 'legalEntity' ? ['department', 'branch'] as const
      : p.key === 'department' || p.key === 'designation' || p.key === 'branch' ? [p.key] as const
      : [])));
  const canWithdraw = !!summary && isOpenDraft(summary.status) && summary.isMine;

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">New hires</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            Accepted offers and prepared hires on their way to becoming employees. A different HR approver activates each one.
          </p>
        </div>
        <button type="button" onClick={() => void load()} className="btn-secondary inline-flex h-9 items-center gap-2 px-3 text-sm">
          <RefreshCw className="h-4 w-4" aria-hidden="true" />
          Refresh
        </button>
      </div>

      {notice && (
        <div role="status" className="flex flex-wrap items-center gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm font-medium text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200">
          <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span>{notice.text}</span>
          {notice.employeeId != null && (
            <Link href={`/people?employeeId=${notice.employeeId}`} className="font-semibold underline underline-offset-2">
              Open employee record
            </Link>
          )}
          <button type="button" onClick={() => setNotice(null)} className="ms-auto text-xs font-semibold underline">Dismiss</button>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-white/[0.03]" role="group" aria-label="Filter new hires by status">
        {DRAFT_FILTERS.map((f) => {
          const count = f.countKey && counts ? counts[f.countKey] : null;
          return (
            <button
              key={f.key}
              type="button"
              onClick={() => setFilter(f.key)}
              aria-pressed={filter === f.key}
              className={`inline-flex h-8 items-center gap-1.5 rounded-md px-3 text-sm font-semibold transition ${filter === f.key ? 'bg-slate-900 text-white dark:bg-white dark:text-slate-950' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
            >
              {f.label}
              {count !== null && <span className="rounded bg-black/10 px-1.5 text-xs dark:bg-white/15">{count}</span>}
            </button>
          );
        })}
        <form
          className="ms-auto flex items-center gap-2"
          onSubmit={(e) => { e.preventDefault(); setAppliedSearch(search.trim()); }}
        >
          <label htmlFor="new-hire-search" className="sr-only">Search new hires</label>
          <div className="relative">
            <Search className="pointer-events-none absolute start-2 top-2 z-10 h-4 w-4 text-slate-400" aria-hidden="true" />
            <input
              id="new-hire-search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Name, department or job title"
              className="input h-8 w-64 ps-8 text-sm"
            />
          </div>
          <button type="submit" className="btn-secondary h-8 px-3 text-sm">Search</button>
        </form>
      </div>

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[860px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['New hire', 'Placement', 'Joining', 'Status', 'Next action'].map((h) => (
                  <th key={h} scope="col" className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading && (
                <tr><td colSpan={5} className="py-12 text-center">
                  <Loader2 className="mx-auto h-6 w-6 animate-spin text-sapphire" aria-label="Loading new hires" />
                </td></tr>
              )}
              {!loading && loadError && (
                <tr><td colSpan={5} className="px-4 py-10">
                  <div role="alert" className="mx-auto flex max-w-xl flex-col items-center gap-3 text-center">
                    <AlertTriangle className="h-8 w-8 text-rose-500" aria-hidden="true" />
                    <p className="text-sm font-semibold text-rose-700 dark:text-rose-300">New hires could not be loaded.</p>
                    <p className="text-sm text-slate-600 dark:text-slate-300">{loadError}</p>
                    <button type="button" onClick={() => void load()} className="btn-secondary h-8 px-3 text-sm">Retry</button>
                  </div>
                </td></tr>
              )}
              {!loading && !loadError && data && data.items.length === 0 && (
                <tr><td colSpan={5} className="py-14 text-center">
                  <UserPlus className="mx-auto mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" aria-hidden="true" />
                  <p className="text-sm text-slate-500 dark:text-slate-400">
                    {filter === 'awaiting' ? 'Nothing is waiting for approval.' : 'No new hires in this view.'}
                  </p>
                </td></tr>
              )}
              {!loading && !loadError && data?.items.map((row) => {
                const chip = draftStatusChip(row.status);
                const action = nextDraftAction(row);
                return (
                  <tr key={row.id} className="align-top hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                    <td className="px-4 py-3">
                      <p className="font-semibold text-slate-900 dark:text-white">{row.name}</p>
                      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
                        {row.source === 'Recruitment' ? `Accepted offer${row.jobTitle ? `: ${row.jobTitle}` : ''}` : 'Prepared by HR'}
                        {row.createdByName ? ` · by ${row.createdByName}` : ''}
                      </p>
                    </td>
                    <td className="px-4 py-3 text-slate-700 dark:text-slate-200">{placement(row)}</td>
                    <td className="px-4 py-3 text-slate-700 dark:text-slate-200">{fmtDate(row.joiningDate)}</td>
                    <td className="px-4 py-3">
                      <StatusChip label={chip.label} tone={chip.tone} dot />
                      {row.submittedAtUtc && isOpenDraft(row.status) && (
                        <p className="mt-1 text-xs text-slate-400">Sent {fmtDateTime(row.submittedAtUtc)}</p>
                      )}
                      {!isOpenDraft(row.status) && row.decidedAtUtc && (
                        <p className="mt-1 text-xs text-slate-400">
                          {fmtDateTime(row.decidedAtUtc)}{row.decidedByName ? ` by ${row.decidedByName}` : ''}
                        </p>
                      )}
                      {row.decisionReason && (
                        <p className="mt-1 max-w-xs text-xs text-slate-500 dark:text-slate-400">Reason: {row.decisionReason}</p>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      {action.kind === 'review' && (
                        <button type="button" onClick={() => openReview(row)} className="btn-primary h-8 px-3 text-xs">{action.label}</button>
                      )}
                      {action.kind === 'submit' && (
                        <div className="flex flex-wrap gap-2">
                          <button type="button" disabled={rowBusy === row.id} onClick={() => void submitDraft(row)} className="btn-primary h-8 px-3 text-xs disabled:opacity-60">
                            {rowBusy === row.id ? 'Sending…' : action.label}
                          </button>
                          <button type="button" onClick={() => openReview(row)} className="btn-secondary h-8 px-3 text-xs">View</button>
                        </div>
                      )}
                      {action.kind === 'waiting' && (
                        <div>
                          <button type="button" onClick={() => openReview(row)} className="btn-secondary h-8 px-3 text-xs">{action.label}</button>
                          <p className="mt-1 max-w-xs text-xs text-slate-500 dark:text-slate-400">{action.reason}</p>
                        </div>
                      )}
                      {action.kind === 'openEmployee' && (
                        <Link href={`/people?employeeId=${action.employeeId}`} className="btn-secondary inline-flex h-8 items-center px-3 text-xs">
                          {action.label}{row.activatedEmployeeCode ? ` ${row.activatedEmployeeCode}` : ''}
                        </Link>
                      )}
                      {action.kind === 'closed' && (
                        <button type="button" onClick={() => openReview(row)} className="btn-secondary h-8 px-3 text-xs">View</button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {!loadError && data && totalPages > 1 && (
          <div className="flex items-center justify-between border-t border-slate-100 px-4 py-3 dark:border-white/[0.07]">
            <p className="text-xs text-slate-400">Page {page} of {totalPages} · {data.total} new hire{data.total === 1 ? '' : 's'}</p>
            <div className="flex gap-1">
              <button type="button" disabled={page === 1} onClick={() => setPage((p) => p - 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Previous</button>
              <button type="button" disabled={page === totalPages} onClick={() => setPage((p) => p + 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Next</button>
            </div>
          </div>
        )}
      </div>

      <Modal
        isOpen={!!selected}
        title={summary ? `New hire: ${summary.name}` : 'New hire'}
        onClose={closeReview}
        size="lg"
        footer={
          <>
            <button type="button" onClick={closeReview} disabled={!!deciding} className="btn-secondary">Close</button>
            {canWithdraw && (
              <button type="button" onClick={() => void withdraw()} disabled={!!deciding} className="btn-secondary disabled:opacity-60">
                {deciding === 'withdraw' ? 'Withdrawing…' : 'Withdraw draft'}
              </button>
            )}
            {canDecide && (
              <>
                <button type="button" onClick={() => void reject()} disabled={!!deciding} className="btn-secondary text-rose-600 hover:border-rose-300 disabled:opacity-60">
                  {deciding === 'reject' ? 'Rejecting…' : 'Reject hire'}
                </button>
                <button
                  type="button"
                  onClick={() => void approve()}
                  disabled={!!deciding || !!disabledReason}
                  aria-describedby="approve-help"
                  className="btn-primary disabled:opacity-60"
                >
                  {deciding === 'approve' ? 'Activating…' : 'Approve and activate'}
                </button>
              </>
            )}
          </>
        }
      >
        {summary && (
          <div className="space-y-4">
            <div className="flex flex-wrap items-center gap-2">
              <StatusChip {...draftStatusChip(summary.status)} dot />
              <span className="text-xs text-slate-500 dark:text-slate-400">
                {summary.source === 'Recruitment' ? `From an accepted offer${summary.jobTitle ? ` for ${summary.jobTitle}` : ''}` : 'Prepared by HR'}
                {summary.createdByName ? ` · prepared by ${summary.createdByName}` : ''}
              </span>
            </div>

            {reviewLoading && (
              <p className="flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> Loading the draft and checking activation…</p>
            )}
            {reviewError && (
              <div role="alert" className="flex flex-wrap items-center gap-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
                <span>The draft could not be loaded: {reviewError}</span>
                <button type="button" onClick={() => selected && void loadReview(selected.id)} className="font-semibold underline">Retry</button>
              </div>
            )}

            {review && (
              <dl className="grid gap-x-6 gap-y-3 rounded-lg border border-slate-200 bg-slate-50 p-4 text-sm sm:grid-cols-2 dark:border-white/10 dark:bg-white/[0.03]">
                {[
                  ['Designation', review.draft.designation || 'Not set'],
                  ['Department', review.draft.department || 'Not set'],
                  ['Branch', review.draft.branch || 'Not set'],
                  ['Legal entity', check?.resolvedCompanyName ?? 'Resolved on activation'],
                  ['Joining date', fmtDate(review.draft.joiningDate)],
                  ['Contract', review.draft.contractType || 'Not set'],
                  ['Personal email', review.draft.personalEmail || 'Not set'],
                  ['Work email', review.draft.workEmail || 'Not set (no login is created)'],
                  ...(review.draft.salary != null ? [['Basic salary (monthly, as offered)', review.draft.salary.toLocaleString('en-GB')]] : []),
                  ['Documents attached', String(review.documentCount)],
                ].map(([label, value]) => (
                  <div key={label}>
                    <dt className="text-xs font-bold uppercase tracking-wide text-slate-400">{label}</dt>
                    <dd className="mt-0.5 font-medium text-slate-800 dark:text-slate-100">{value}</dd>
                  </div>
                ))}
              </dl>
            )}

            {review && !isOpenDraft(review.summary.status) && (
              <div className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
                {review.summary.status === 'Activated'
                  ? `Activated${review.summary.activatedEmployeeCode ? ` as employee ${review.summary.activatedEmployeeCode}` : ''}${review.summary.decidedByName ? ` by ${review.summary.decidedByName}` : ''}.`
                  : `${draftStatusChip(review.summary.status).label}${review.summary.decidedByName ? ` by ${review.summary.decidedByName}` : ''}${review.summary.decisionReason ? `: ${review.summary.decisionReason}` : '.'}`}
              </div>
            )}

            {check && (
              <section aria-labelledby="activation-check-title">
                {check.canActivate ? (
                  <div className="flex items-start gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200">
                    <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
                    <p id="activation-check-title" className="font-semibold">Ready to activate. Nothing on this draft would stop activation.</p>
                  </div>
                ) : (
                  <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
                    <p id="activation-check-title" className="flex items-center gap-2 font-semibold">
                      <ShieldAlert className="h-4 w-4 shrink-0" aria-hidden="true" />
                      Activation would be refused. {check.problems.length === 1 ? 'One thing' : `${check.problems.length} things`} to fix first:
                    </p>
                    <ul className="mt-2 space-y-2">
                      {check.problems.map((p) => (
                        <li key={`${p.key}-${p.reason}`}>
                          <p className="font-medium">{p.label}: {p.reason}</p>
                          <p className="text-xs opacity-90">{p.fix}</p>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                {check.advisories.length > 0 && (
                  <details className="mt-2 text-xs text-slate-500 dark:text-slate-400">
                    <summary className="cursor-pointer font-semibold">
                      {check.advisories.length} recommendation{check.advisories.length === 1 ? '' : 's'} (not required to activate)
                    </summary>
                    <ul className="mt-1 list-disc space-y-0.5 ps-5">
                      {check.advisories.map((a) => <li key={a}>{a}</li>)}
                    </ul>
                  </details>
                )}
                <p className="mt-2 text-xs text-slate-400">The position budget is checked again when you approve.</p>
              </section>
            )}

            {review && check && placementProblems.length > 0 && isOpenDraft(review.summary.status) && (
              <DraftPlacementFix
                key={review.summary.id}
                draftId={review.summary.id}
                current={{ department: review.draft.department, designation: review.draft.designation, branch: review.draft.branch }}
                problemFields={placementProblems}
                isMaker={review.summary.isMine}
                onSaved={() => { void loadReview(review.summary.id); void load(); }}
              />
            )}

            {isOpenDraft(summary.status) && !summary.canApprove && (
              <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
                {summary.approveBlockedReason ?? 'Waiting for an HR approver.'}
              </div>
            )}

            {(canDecide || canWithdraw) && (
              <div>
                <label htmlFor="draft-decision-reason" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                  {canDecide ? 'Reason (required to reject)' : 'Reason for withdrawing (optional)'}
                </label>
                <textarea
                  id="draft-decision-reason"
                  value={reason}
                  onChange={(e) => { setReason(e.target.value); if (decisionError) setDecisionError(''); }}
                  rows={3}
                  className="input w-full resize-none"
                  aria-describedby="approve-help draft-decision-error"
                />
              </div>
            )}
            {canDecide && disabledReason && (
              <p id="approve-help" className="text-xs text-slate-500 dark:text-slate-400">{disabledReason}</p>
            )}
            {decisionError && (
              <p id="draft-decision-error" role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
                {decisionError}
              </p>
            )}
          </div>
        )}
      </Modal>

      <EstablishmentBlockedModal
        block={establishmentBlock?.block ?? null}
        employeeName={establishmentBlock?.name}
        onClose={() => setEstablishmentBlock(null)}
      />
    </div>
  );
}
