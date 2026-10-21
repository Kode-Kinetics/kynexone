'use client';
import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown } from 'lucide-react';
import { attendanceApi, type AttendanceExceptionItem } from '../../api/attendance';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { useAuth } from '../../contexts/AuthContext';
import { requestFailureReason } from '../../lib/requestFailure';
import { msg } from '../../i18n/translations';
import { usePagedList } from '../../hooks/usePagedList';
import { ListWindowFooter } from '../ListWindowFooter';
import { EnumLabel } from '../EnumLabel';

/** What each exception means, in plain words (AttendanceService.ReviewedExceptionTypes). */
const TYPE_LABELS: Record<string, string> = {
  MissingPunch: msg('Missing punch'),
  PaidAttendanceChanged: msg('Paid day changed'),
  ImportedDayHasPunches: msg('Imported day has punches'),
};

const TYPE_HINTS: Record<string, string> = {
  MissingPunch: msg('Only one end of the day was recorded. Late and early-exit minutes are not deducted until it is corrected.'),
  PaidAttendanceChanged: msg('Payroll already paid this day, and the day has changed since. Correct the difference with a payroll adjustment.'),
  ImportedDayHasPunches: msg('The imported times were kept. If the punches are right, approve a correction for the day.'),
};

const MIN_REASON = 10;

/**
 * Attendance → Exceptions: the days HR has to look at, most serious first, each with its day (drill-down) and one
 * action — resolve, with a reason that is kept. A resolved exception stays resolved through recomputes, unless the
 * day's punches (or the paid difference) change.
 */
export function AttendanceExceptionsPanel() {
  const t = useT();
  const f = useFormat();
  const { hasPermission } = useAuth();
  const canResolve = hasPermission('attendance.write');
  const [state, setState] = useState<'open' | 'resolved'>('open');
  const [type, setType] = useState('');
  // One page at a time (the API caps a page at 100); "Load more" reaches every exception the total counts.
  const list = usePagedList<AttendanceExceptionItem>((page, pageSize) =>
    attendanceApi.exceptions({ state, type: type || undefined, page, pageSize }));
  const items = list.items;
  const total = list.total ?? 0;
  const loading = list.loading;
  const loadError = list.error ? requestFailureReason(list.error) : '';
  const [openId, setOpenId] = useState<string | null>(null);
  const [resolvingId, setResolvingId] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [actionError, setActionError] = useState('');

  const { reload } = list;
  const load = useCallback(() => { void reload(); }, [reload]);

  useEffect(() => { load(); }, [load, state, type]);

  const resolve = async (id: string) => {
    setSaving(true); setActionError('');
    try {
      await attendanceApi.resolveException(id, reason.trim());
      setResolvingId(null); setReason('');
      load();
    } catch (e) {
      setActionError(t('The exception could not be resolved. {reason}', { reason: requestFailureReason(e) }));
    } finally { setSaving(false); }
  };

  const time = (value?: string | null) => (value ? f.time(value) : '—');

  return (
    <div className="space-y-4" data-testid="attendance-exceptions">
      <div className="flex flex-wrap items-center gap-3">
        <select aria-label={t('Show')} className="rounded-lg border border-slate-200 px-3 py-2 text-sm dark:border-white/10 dark:bg-transparent"
          value={state} onChange={e => setState(e.target.value as 'open' | 'resolved')}>
          <option value="open">{t('Open')}</option>
          <option value="resolved">{t('Resolved')}</option>
        </select>
        <select aria-label={t('Exception type')} className="rounded-lg border border-slate-200 px-3 py-2 text-sm dark:border-white/10 dark:bg-transparent"
          value={type} onChange={e => setType(e.target.value)}>
          <option value="">{t('All types')}</option>
          {Object.entries(TYPE_LABELS).map(([key, label]) => <option key={key} value={key}>{t(label)}</option>)}
        </select>
        <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count} exception(s)', { count: total })}</p>
      </div>

      {loadError && <p className="text-sm text-rose-700 dark:text-rose-400" role="alert">{t('Exceptions could not be loaded. {reason}', { reason: loadError })}</p>}
      {!loading && !loadError && items.length === 0 && (
        <div className="surface flex flex-col items-center py-12 text-center">
          <CheckCircle2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" aria-hidden />
          <p className="text-sm text-slate-600 dark:text-slate-400">
            {state === 'open' ? t('No open exceptions. Nothing needs your attention.') : t('No resolved exceptions yet.')}
          </p>
        </div>
      )}

      <ul className="surface divide-y divide-slate-100 dark:divide-white/5">
        {items.map(item => (
          <li key={item.id} className="px-5 py-4" data-testid="attendance-exception">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                <p className="flex items-center gap-2 text-sm font-medium text-slate-900 dark:text-white">
                  {item.severity === 'High' && <AlertTriangle className="h-4 w-4 shrink-0 text-rose-500" aria-hidden />}
                  <span>{t(TYPE_LABELS[item.exceptionType] ?? 'Missing punch')}</span>
                  <span className="text-slate-400">·</span>
                  <span>{item.employeeName || item.employeeCode}</span>
                  <span className="text-slate-400">·</span>
                  <span>{f.date(item.workDate)}</span>
                </p>
                <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{item.description}</p>
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t(TYPE_HINTS[item.exceptionType] ?? TYPE_HINTS.MissingPunch)}</p>
                {item.resolution && <p className="mt-1 text-xs text-emerald-700 dark:text-emerald-400">{t('Resolved: {note}', { note: item.resolution })}</p>}
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <button type="button" className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300"
                  aria-expanded={openId === item.id} onClick={() => setOpenId(openId === item.id ? null : item.id)}>
                  <ChevronDown className="h-3.5 w-3.5" aria-hidden /> {t('Show the day')}
                </button>
                {canResolve && !item.isResolved && (
                  <button type="button" className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90"
                    onClick={() => { setResolvingId(item.id); setReason(''); setActionError(''); }}>
                    {t('Resolve')}
                  </button>
                )}
              </div>
            </div>

            {openId === item.id && (
              <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 rounded-lg bg-slate-50 px-4 py-3 text-xs dark:bg-white/5 sm:grid-cols-3" data-testid="attendance-exception-day">
                {item.day ? (
                  <>
                    <dt className="text-slate-500">{t('Status')}</dt><dd><EnumLabel enum="AttendanceStatus" value={item.day.status} /></dd>
                    <dt className="text-slate-500">{t('First in')}</dt><dd>{time(item.day.firstInUtc)}</dd>
                    <dt className="text-slate-500">{t('Last out')}</dt><dd>{time(item.day.lastOutUtc)}</dd>
                    <dt className="text-slate-500">{t('Late minutes')}</dt><dd>{f.number(item.day.lateMinutes)}</dd>
                    <dt className="text-slate-500">{t('Early-exit minutes')}</dt><dd>{f.number(item.day.earlyExitMinutes)}</dd>
                  </>
                ) : (
                  <dd className="col-span-full text-slate-500">{t('This day has no attendance record.')}</dd>
                )}
              </dl>
            )}

            {resolvingId === item.id && (
              <div className="mt-3 space-y-2">
                <label className="block text-xs font-medium text-slate-600 dark:text-slate-300" htmlFor={`resolve-${item.id}`}>
                  {t('Why is this resolved? Say what you checked and what was done.')}
                </label>
                <textarea id={`resolve-${item.id}`} rows={2} value={reason} onChange={e => setReason(e.target.value)}
                  className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm dark:border-white/10 dark:bg-transparent" />
                {actionError && <p className="text-xs text-rose-700 dark:text-rose-400" role="alert">{actionError}</p>}
                <div className="flex gap-2">
                  <button type="button" disabled={saving || reason.trim().length < MIN_REASON} onClick={() => resolve(item.id)}
                    className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90 disabled:opacity-50">
                    {saving ? t('Saving…') : t('Mark as resolved')}
                  </button>
                  <button type="button" onClick={() => setResolvingId(null)}
                    className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 dark:border-white/10 dark:text-slate-300">
                    {t('Cancel')}
                  </button>
                </div>
              </div>
            )}
          </li>
        ))}
      </ul>
      <ListWindowFooter shown={items.length} total={list.total} noun="exceptions" loadingMore={list.loadingMore}
        onLoadMore={() => void list.loadMore()}
        labels={{
          window: t('Showing {shown} of {total} exceptions', { shown: f.number(items.length), total: f.number(total) }),
          loadMore: t('Load more'),
          loading: t('Loading…'),
        }} />
    </div>
  );
}
