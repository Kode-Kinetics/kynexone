'use client';

/**
 * Approvals, as a real table on desktop (employee, request type, detail, waiting time, action)
 * and as cards on phones. Waiting is measured against the request's own due date when it has
 * one; without a due date the bar shows age against a 3-day reference, labelled as such, never
 * as an SLA the tenant did not set. Approve / reject stay behind the approvals page.
 */

import Link from 'next/link';
import { ArrowRight, CheckCircle2 } from 'lucide-react';
import { Avatar } from '../Avatar';
import type { ApprovalQueueItem } from '../../api/dashboard';
import { ageLabel, entityLabel, parseApprovalSubject } from './dashboardModel';
import { useT } from '../../hooks/useT';

const CHIP: Record<string, string> = {
  leave: 'bg-blue-50 text-blue-800 dark:bg-blue-500/15 dark:text-blue-200',
  change: 'bg-violet-50 text-violet-800 dark:bg-violet-500/15 dark:text-violet-200',
  overtime: 'bg-orange-50 text-orange-800 dark:bg-orange-500/15 dark:text-orange-200',
  money: 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200',
  other: 'bg-slate-100 text-slate-800 dark:bg-white/10 dark:text-slate-200',
};

interface Row {
  id: string;
  name: string;
  code: string | null;
  dept: string | null;
  kind: string;
  chip: string;
  detail: string | null;
  ageH: number;
  dueH: number | null; // hours until due (negative = overdue)
  refH: number;        // denominator for the bar
  over: boolean;
  hasDue: boolean;
}

function rowsFrom(queue: ApprovalQueueItem[], now: number): Row[] {
  return queue.map((q) => {
    const p = parseApprovalSubject(q.title);
    const kind = p.kind ? p.kind.charAt(0) + p.kind.slice(1).toLowerCase() : entityLabel(q.module);
    const k = kind.toLowerCase();
    const chip = /leave/.test(k) ? 'leave' : /change|profile/.test(k) ? 'change' : /overtime/.test(k) ? 'overtime' : /loan|advance|expense|salary/.test(k) ? 'money' : 'other';
    const ageH = (now - new Date(q.createdAtUtc).getTime()) / 3_600_000;
    const due = q.dueAtUtc ? new Date(q.dueAtUtc).getTime() : null;
    const dueH = due != null ? (due - now) / 3_600_000 : null;
    const refH = due != null ? Math.max(1, (due - new Date(q.createdAtUtc).getTime()) / 3_600_000) : 72;
    return {
      id: q.id,
      name: q.employeeName ?? p.name ?? q.title,
      code: q.employeeCode ?? p.code,
      dept: q.department ?? null,
      kind, chip,
      detail: q.detail ?? null,
      ageH, dueH, refH,
      over: dueH != null ? dueH < 0 : ageH > 72,
      hasDue: due != null,
    };
  }).sort((a, b) => b.ageH - a.ageH);
}

function Wait({ r }: { r: Row }) {
  const t = useT();
  const pct = Math.min(1, r.ageH / r.refH);
  const bar = r.over ? '#DC2626' : pct > 0.66 ? '#D97706' : '#16A34A';
  return (
    <span className="flex w-full max-w-[140px] flex-col gap-1">
      <span className={`text-xs font-semibold tabular-nums ${r.over ? 'text-rose-700 dark:text-rose-400' : 'text-slate-800 dark:text-slate-200'}`}>
        {ageLabel(r.ageH)}
        <span className="font-normal text-slate-600 dark:text-slate-400">
          {r.hasDue ? (r.over ? `, ${ageLabel(-(r.dueH as number))} ${t('overdue')}` : `, ${t('due in')} ${ageLabel(r.dueH as number)}`) : r.over ? ` (${t('over 3 days')})` : ''}
        </span>
      </span>
      <span className="block h-[5px] rounded-full bg-[color:var(--viz-track)]" aria-hidden>
        <span className="block h-[5px] rounded-full" ref={(n) => { if (n) { n.style.width = `${Math.max(4, pct * 100)}%`; n.style.background = bar; } }} />
      </span>
    </span>
  );
}

export function ApprovalsTable({ queue, pending, loading, compact, dense = false }: { queue: ApprovalQueueItem[]; pending: number; loading: boolean; compact: boolean; dense?: boolean }) {
  const t = useT();
  const rows = rowsFrom(queue, Date.now());
  const overCount = rows.filter((r) => r.over).length;
  const anyDue = rows.some((r) => r.hasDue);

  return (
    <section aria-labelledby="approvals-heading" className="wg-card flex min-w-0 flex-col gap-2 p-5">
      <header className="flex items-start justify-between gap-3">
        <div>
          <h2 id="approvals-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">
            {t('Approvals')}
            {pending > 0 && <span className="ms-2 rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-semibold tabular-nums text-indigo-800 dark:bg-indigo-500/15 dark:text-indigo-200">{pending}</span>}
          </h2>
          {!loading && rows.length > 0 && (
            <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">
              {overCount > 0 ? `${overCount} ${anyDue ? t('past their due date') : t('waiting over 3 days')}. ` : ''}
              {t('Oldest waiting')} {ageLabel(rows[0].ageH)}.
            </p>
          )}
        </div>
        <Link href="/approvals" className="inline-flex shrink-0 items-center gap-1 text-[13px] font-semibold text-sapphire hover:underline dark:text-blue-300">
          {t('Open approvals')} <ArrowRight className="h-3.5 w-3.5" aria-hidden />
        </Link>
      </header>

      {loading && <div className="space-y-2" aria-hidden>{[0, 1, 2].map((i) => <div key={i} className="h-12 rounded-lg bg-slate-100 dark:bg-white/[0.05]" />)}</div>}
      {!loading && rows.length === 0 && (
        <p className="flex items-center gap-2 text-[13px] text-slate-700 dark:text-slate-300"><CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden />{t('Nothing is waiting for a decision.')}</p>
      )}

      {!loading && rows.length > 0 && (compact ? (
        <ul className="flex flex-col gap-2">
          {rows.map((r) => (
            <li key={r.id} className="flex items-center gap-3 rounded-xl border border-[color:var(--wg-line)] p-3">
              <Avatar name={r.name} size="lg" />
              <span className="flex min-w-0 flex-1 flex-col gap-1">
                <span className="truncate text-sm font-semibold text-slate-900 dark:text-white">{r.name}</span>
                <span className="flex flex-wrap items-center gap-1.5 text-xs">
                  <span className={`rounded-full px-2 py-0.5 font-semibold ${CHIP[r.chip]}`}>{r.kind}</span>
                  {r.detail && <span className="text-slate-700 dark:text-slate-300">{r.detail}</span>}
                </span>
                <Wait r={r} />
              </span>
              <Link href="/approvals" className="wg-press inline-flex h-9 shrink-0 items-center rounded-lg bg-sapphire px-3 text-xs font-semibold text-white dark:bg-blue-600">{t('Review')}</Link>
            </li>
          ))}
        </ul>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full table-fixed border-collapse text-start">
            <colgroup><col className="w-[33%]" /><col className="w-[24%] 2xl:w-[18%]" /><col className="hidden 2xl:table-column 2xl:w-[18%]" /><col className="w-[25%] 2xl:w-[18%]" /><col className="w-[18%] 2xl:w-[12%]" /></colgroup>
            <thead>
              <tr className="text-[11px] text-slate-600 dark:text-slate-400">
                <th scope="col" className="pb-2 text-start font-medium">{t('Employee')}</th>
                <th scope="col" className="pb-2 text-start font-medium">{t('Request')}</th>
                <th scope="col" className="hidden pb-2 text-start font-medium 2xl:table-cell">{t('Detail')}</th>
                <th scope="col" className="pb-2 text-start font-medium">{anyDue ? t('Waiting vs due date') : t('Waiting')}</th>
                <th scope="col" className="pb-2"><span className="sr-only">{t('Action')}</span></th>
              </tr>
            </thead>
            <tbody>
              {rows.slice(0, dense ? 4 : 6).map((r) => (
                <tr key={r.id} className="border-t border-[color:var(--wg-line)]">
                  <td className={`${dense ? 'py-1.5' : 'py-2'} pe-3`}>
                    <span className="flex items-center gap-2.5">
                      <Avatar name={r.name} size={dense ? 'sm' : 'md'} />
                      <span className="flex min-w-0 flex-col">
                        <span className="truncate text-[13px] font-semibold text-slate-900 dark:text-white">{r.name}</span>
                        <span className="truncate text-xs text-slate-600 dark:text-slate-400">{r.dept ?? r.code ?? ''}</span>
                      </span>
                    </span>
                  </td>
                  <td className="pe-3">
                    <span className="flex flex-col items-start gap-1">
                      <span title={r.kind} className={`max-w-full truncate rounded-full px-2 py-0.5 text-xs font-semibold ${CHIP[r.chip]}`}>{r.kind}</span>
                      {r.detail && <span className="text-xs text-slate-700 dark:text-slate-300 2xl:hidden">{r.detail}</span>}
                    </span>
                  </td>
                  <td className="hidden pe-3 text-[13px] text-slate-700 dark:text-slate-300 2xl:table-cell">{r.detail ?? ''}</td>
                  <td className="pe-3"><Wait r={r} /></td>
                  <td className="text-end">
                    <Link href="/approvals" className="wg-press inline-flex h-8 items-center rounded-lg border border-indigo-200 px-2.5 text-xs font-semibold text-indigo-800 hover:bg-indigo-50 dark:border-indigo-400/30 dark:text-indigo-200 dark:hover:bg-indigo-500/10">
                      {t('Review')}<span className="sr-only"> {r.kind} {t('for')} {r.name}</span>
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {pending > Math.min(rows.length, dense ? 4 : 6) && (
            <Link href="/approvals" className="mt-2 inline-flex items-center gap-1 text-[13px] font-semibold text-sapphire hover:underline dark:text-blue-300">
              {t('View all')} {pending} <ArrowRight className="h-3.5 w-3.5" aria-hidden />
            </Link>
          )}
        </div>
      ))}
    </section>
  );
}
