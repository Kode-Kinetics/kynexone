'use client';

import { useCallback, useEffect, useState } from 'react';
import { Download } from 'lucide-react';
import {
  learningApi, type CompletionRow, type ExpiringRow, type Kpi, type ReportName, type StatusRow, type Summary,
} from '../../api/learning';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { assignmentChip, learningError, saveBlob } from '../../lib/learning';
import { msg } from '../../i18n/translations';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import type { RecordsFilter } from '../../views/LearningPage';
import { EmptyBlock, ErrorBlock, LoadingRows } from './LearningParts';

const kpiLabel: Record<Kpi['key'], string> = {
  assigned: msg('Assigned'),
  open: msg('Not finished'),
  completed: msg('Completed'),
  overdue: msg('Overdue'),
  expiring: msg('Certificates expiring in 30 days'),
  locked: msg('Locked after failed attempts'),
  pending: msg('Waiting for HR to verify'),
};

/** The definition shown under each KPI, in the viewer's language (the server's English definition is the contract). */
const kpiDefinition: Record<Kpi['key'], string> = {
  assigned: msg('Assignments of the people you can see, not cancelled, in any state.'),
  open: msg('Assigned or in progress — not yet completed.'),
  completed: msg('Assignments whose every required course has completion evidence.'),
  overdue: msg('Open mandatory assignments past their due date that still need the employee to act. Suspended people are included; optional training, people who have not started yet, and assignments only waiting for HR to verify are not.'),
  expiring: msg('Current certificates that expire within 30 days.'),
  locked: msg('Assignments with a course locked after its last failed assessment attempt, waiting for an HR reset.'),
  pending: msg('Assignments with a completion reported by course content, waiting for HR to verify it before a certificate is issued.'),
};

type Drill = { key: Kpi['key']; rows: StatusRow[] | ExpiringRow[] } | null;

/**
 * LRN-11 overview. Every number is a count of rows the API also lists: clicking a KPI shows exactly those rows, and
 * every table downloads as CSV. HR sees their companies; a manager sees their own reporting line (the server decides).
 */
export function LearningReports({ onDrill }: { onDrill: (filter: RecordsFilter) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [summary, setSummary] = useState<Summary | null>(null);
  const [groupBy, setGroupBy] = useState<'path' | 'department'>('path');
  const [completion, setCompletion] = useState<CompletionRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [drill, setDrill] = useState<Drill>(null);
  const [drillLoading, setDrillLoading] = useState<Kpi['key'] | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [s, c] = await Promise.all([learningApi.summary(), learningApi.completionReport(groupBy)]);
      setSummary(s);
      setCompletion(c);
    } catch (e) {
      setError(learningError(e, t));
    }
  }, [groupBy, t]);
  useEffect(() => { void load(); }, [load]);

  const openDrill = async (kpi: Kpi) => {
    setDrillLoading(kpi.key);
    try {
      const rows = kpi.key === 'overdue' ? await learningApi.overdueReport()
        : kpi.key === 'locked' ? await learningApi.lockedReport()
        : kpi.key === 'pending' ? await learningApi.pendingReport()
          : kpi.key === 'expiring' ? await learningApi.expiringReport(30)
            : await learningApi.statusReport({ status: kpi.drillFilter ?? undefined });
      setDrill({ key: kpi.key, rows });
    } catch (e) {
      toast.error(learningError(e, t));
    } finally {
      setDrillLoading(null);
    }
  };

  const download = async (report: ReportName, params: Record<string, string | number | undefined>, fileName: string) => {
    try {
      saveBlob(await learningApi.exportCsv(report, params), fileName);
    } catch (e) {
      toast.error(learningError(e, t));
    }
  };

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!summary || !completion) return <LoadingRows label={t('Loading learning reports')} />;

  const drillReport = (key: Kpi['key']): [ReportName, Record<string, string | number | undefined>] =>
    key === 'overdue' ? ['overdue', {}] : key === 'locked' ? ['locked', {}] : key === 'pending' ? ['pending', {}] : key === 'expiring' ? ['expiring', { days: 30 }]
      : ['status', { status: summary.kpis.find((k) => k.key === key)?.drillFilter ?? undefined }];

  return (
    <div className="space-y-5" data-testid="learning-reports">
      <p className="text-xs text-slate-500 dark:text-slate-400">
        {summary.scope === 'organization'
          ? t('Showing everyone in the companies you manage, as of {date}.', { date: fx.date(summary.asOf) })
          : t('Showing the people in your team, as of {date}.', { date: fx.date(summary.asOf) })}
        {' '}{t('Completion rate: {rate} of assignments completed.', { rate: fx.percent(summary.completionRatePercent, 1) })}
      </p>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" role="list" aria-label={t('Learning indicators')}>
        {summary.kpis.map((kpi) => (
          <div key={kpi.key} role="listitem" className="wg-card flex flex-col justify-between gap-2 rounded-xl p-4" data-testid={`kpi-${kpi.key}`}>
            <div>
              <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">{t(kpiLabel[kpi.key])}</p>
              <p className="mt-1 text-2xl font-semibold tabular-nums text-slate-900 dark:text-white">{fx.integer(kpi.value)}</p>
              <p className="mt-1 text-caption text-slate-500 dark:text-slate-400">{t(kpiDefinition[kpi.key])}</p>
            </div>
            <button type="button" className="btn-secondary self-start text-xs" onClick={() => void openDrill(kpi)} disabled={drillLoading !== null}
              aria-label={t('Show the {count} records behind "{kpi}"', { count: kpi.value, kpi: t(kpiLabel[kpi.key]) })}>
              {drillLoading === kpi.key ? t('Loading…') : t('Show the records')}
            </button>
          </div>
        ))}
      </div>

      {drill && (
        <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="learning-drill" data-testid="learning-drill">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="learning-drill" className="text-sm font-semibold text-slate-800 dark:text-slate-100">
              {t('{kpi}: {count, plural, one {# record} other {# records}}', { kpi: t(kpiLabel[drill.key]), count: drill.rows.length })}
            </h2>
            <div className="flex gap-2">
              <button type="button" className="btn-secondary inline-flex items-center gap-1 text-xs"
                onClick={() => { const [r, p] = drillReport(drill.key); void download(r, p, `learning-${drill.key}.csv`); }}>
                <Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download CSV')}
              </button>
              <button type="button" className="btn-secondary text-xs" onClick={() => setDrill(null)}>{t('Close')}</button>
            </div>
          </div>
          {drill.rows.length === 0 ? <p className="text-xs text-slate-500">{t('No records.')}</p>
            : drill.key === 'expiring' ? <ExpiringTable rows={drill.rows as ExpiringRow[]} />
              : <StatusTable rows={drill.rows as StatusRow[]} onOpen={(id) => onDrill({ assignmentId: id })} />}
        </section>
      )}

      <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="learning-completion">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="learning-completion" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Completion')}</h2>
          <div className="flex flex-wrap items-center gap-2">
            <label className="text-xs text-slate-600 dark:text-slate-300">
              {t('Group by')}{' '}
              <select className="input ms-1 inline-block w-auto py-1 text-xs" value={groupBy} onChange={(e) => setGroupBy(e.target.value as 'path' | 'department')}>
                <option value="path">{t('Learning path')}</option>
                <option value="department">{t('Department when assigned')}</option>
              </select>
            </label>
            <button type="button" className="btn-secondary inline-flex items-center gap-1 text-xs" onClick={() => void download('completion', { groupBy }, `learning-completion-${groupBy}.csv`)}>
              <Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download CSV')}
            </button>
          </div>
        </div>
        {completion.length === 0 ? (
          <EmptyBlock title={t('No assignments yet')} body={t('Assign a learning path to see completion here.')} />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-xs" data-testid="completion-table">
              <thead className="text-slate-500 dark:text-slate-400">
                <tr>
                  <th scope="col" className="py-2 text-start font-semibold">{groupBy === 'path' ? t('Learning path') : t('Department')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Assigned')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Completed')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('In progress')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Not started')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Overdue')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Completion rate')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {completion.map((row) => (
                  <tr key={row.groupId ?? 'none'}>
                    <td className="py-2 pe-3 font-semibold text-slate-800 dark:text-slate-100">{row.group || t('Not recorded')}</td>
                    <td className="py-2 text-end tabular-nums">{fx.integer(row.assigned)}</td>
                    <td className="py-2 text-end tabular-nums">{fx.integer(row.completed)}</td>
                    <td className="py-2 text-end tabular-nums">{fx.integer(row.inProgress)}</td>
                    <td className="py-2 text-end tabular-nums">{fx.integer(row.notStarted)}</td>
                    <td className={`py-2 text-end tabular-nums ${row.overdue > 0 ? 'font-semibold text-rose-600 dark:text-rose-400' : ''}`}>{fx.integer(row.overdue)}</td>
                    <td className="py-2 text-end tabular-nums">{fx.percent(row.completionRatePercent, 1)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="text-caption text-slate-500 dark:text-slate-400">{t('Assigned = completed + in progress + not started. Cancelled assignments are not counted.')}</p>
      </section>
    </div>
  );
}

export function StatusTable({ rows, onOpen }: { rows: StatusRow[]; onOpen?: (assignmentId: string) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[720px] text-xs" data-testid="status-table">
        <thead className="text-slate-500 dark:text-slate-400">
          <tr>
            <th scope="col" className="py-2 text-start font-semibold">{t('Employee')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Department')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Learning path')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Status')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Due')}</th>
            <th scope="col" className="py-2 text-end font-semibold">{t('Progress')}</th>
            {onOpen && <th scope="col" className="py-2"><span className="sr-only">{t('Actions')}</span></th>}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/5">
          {rows.map((r) => (
            <tr key={r.assignmentId}>
              <td className="py-2 pe-3">
                <span className="font-semibold text-slate-800 dark:text-slate-100">{r.employeeName}</span> <span className="text-slate-500" dir="ltr">{r.employeeCode}</span>
                {r.employeeStatus === 'Suspended' && <span className="ms-1.5 rounded bg-amber-100 px-1.5 py-0.5 text-caption font-semibold text-amber-800 dark:bg-amber-500/15 dark:text-amber-200" data-testid="employee-suspended">{t('Suspended')}</span>}
              </td>
              <td className="py-2 pe-3">{r.department || t('Not recorded')}</td>
              <td className="py-2 pe-3">{r.path}</td>
              <td className="py-2 pe-3">{(() => { const chip = assignmentChip(r.status, r.overdue, r.awaitingVerification); return <StatusChip label={t(chip.label)} tone={chip.tone} />; })()}</td>
              <td className="py-2 pe-3 tabular-nums">{r.dueDate ? fx.date(r.dueDate) : t('No due date')}</td>
              <td className="py-2 text-end tabular-nums">{t('{done} of {total}', { done: r.completedItems, total: r.requiredItems })}</td>
              {onOpen && <td className="py-2 text-end"><button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => onOpen(r.assignmentId)}>{t('Open')}</button></td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ExpiringTable({ rows }: { rows: ExpiringRow[] }) {
  const { t } = useLocale();
  const fx = useFormat();
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-xs" data-testid="expiring-table">
        <thead className="text-slate-500 dark:text-slate-400">
          <tr>
            <th scope="col" className="py-2 text-start font-semibold">{t('Employee')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Course')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Certificate number')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Expires')}</th>
            <th scope="col" className="py-2 text-start font-semibold">{t('Renewal')}</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/5">
          {rows.map((r) => (
            <tr key={r.credentialId}>
              <td className="py-2 pe-3 font-semibold">{r.employeeName}</td>
              <td className="py-2 pe-3">{r.course}</td>
              <td className="py-2 pe-3 font-mono" dir="ltr">{r.certificateNumber}</td>
              <td className="py-2 pe-3 tabular-nums">{t('{date} (in {count, plural, one {# day} other {# days}})', { date: fx.date(r.expiresOn), count: r.daysLeft })}</td>
              <td className="py-2">{r.renewalAssignmentId ? t('Renewal assigned') : t('Not yet assigned')}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
