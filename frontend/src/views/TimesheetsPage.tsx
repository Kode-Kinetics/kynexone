'use client';

import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import type { Formatter } from '../lib/format';
import { EnumLabel } from '../components/EnumLabel';
import { enumLabel } from '../i18n/enumLabel';
import { msg } from '../i18n/translations';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle,
  CalendarClock,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  ClipboardCheck,
  FileClock,
  Inbox,
  Scale,
  Send,
} from 'lucide-react';
import { costCentersApi, type CostCenterDto } from '../api/organization';
import {
  essTimesheetsApi,
  timesheetsApi,
  type SaveTimesheetEntry,
  type Timesheet,
  type TimesheetInboxItem,
  type TimesheetSummary,
  type TimesheetVarianceReport,
} from '../api/timesheets';
import { notifyApiError, parseApiErrorForTimesheets } from '../api/timesheetErrors';
import { useAuth } from '../contexts/AuthContext';
import { ErrorBanner } from '../components/ui/ErrorBanner';
import { Modal } from '../components/Modal';
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';
import { ListWindowFooter } from '../components/ListWindowFooter';
import { usePagedList } from '../hooks/usePagedList';
import { useAppToast } from '../components/ui/AppToast';

// ── Dates ────────────────────────────────────────────────────────────────────────────────────
// A timesheet date is a calendar fact, not a UTC instant. `new Date('2026-03-01')` parses as UTC
// midnight and renders as Feb 28 west of UTC, so every date here is split and rebuilt locally.

function parseDate(iso: string): Date {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return new Date(y, (m ?? 1) - 1, d ?? 1);
}
function toIso(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}
function addDays(d: Date, n: number): Date {
  const r = new Date(d);
  r.setDate(r.getDate() + n);
  return r;
}
/** The Sunday on or before the date — the same rule TimesheetPeriod.StartFor applies server-side. */
function weekStart(d: Date): Date {
  const r = new Date(d);
  r.setDate(r.getDate() - r.getDay());
  r.setHours(0, 0, 0, 0);
  return r;
}
type Translate = (key: string, params?: Record<string, string | number | null | undefined>) => string;

function fmtDay(fx: Formatter, iso: string) {
  return fx.date(parseDate(iso), 'dayMonth');
}
function fmtRange(fx: Formatter, startIso: string, endIso: string) {
  return `${fmtDay(fx, startIso)} – ${fx.date(parseDate(endIso), 'medium')}`;
}
function fmtDateTime(fx: Formatter, iso: string | null) {
  return iso ? fx.dateTime(iso, 'medium') : '—';
}

// ── Hours ────────────────────────────────────────────────────────────────────────────────────
// Entry is in hours because that is how people think about a day; storage is in minutes because
// that is what attendance records. The conversion lives here, once.

function fmtHours(t: Translate, minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined) return '—';
  const sign = minutes < 0 ? '-' : '';
  const abs = Math.abs(minutes);
  return `${sign}${t('{hours}h {minutes}m', { hours: Math.floor(abs / 60), minutes: String(abs % 60).padStart(2, '0') })}`;
}
/** '7.5', '7:30' and '7h30' all mean the same thing to a person filling in a week. */
function parseHoursToMinutes(raw: string): number | null {
  const text = raw.trim();
  if (text === '') return 0;
  const colon = /^(\d{1,2})\s*[:h]\s*(\d{1,2})$/.exec(text);
  if (colon) {
    const h = Number(colon[1]);
    const m = Number(colon[2]);
    return m > 59 ? null : h * 60 + m;
  }
  if (!/^\d{0,2}(\.\d{1,2})?$/.test(text)) return null;
  const hours = Number(text);
  if (Number.isNaN(hours)) return null;
  return Math.round(hours * 60);
}
function minutesToInput(minutes: number): string {
  return minutes === 0 ? '' : (minutes / 60).toFixed(2).replace(/\.?0+$/, '');
}

// ── House style ──────────────────────────────────────────────────────────────────────────────

const inp =
  'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const sel = `${inp} appearance-none`;
const btn = {
  primary:
    'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50',
  ghost:
    'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  sm: 'inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
};

const STATUS_COLOR: Record<string, string> = {
  Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700/60 dark:text-slate-300',
  Submitted: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Approved: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Rejected: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
};

function StatusBadge({ status }: { status: string }) {
  const cls = STATUS_COLOR[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-lg px-2 py-0.5 text-xs font-medium ${cls}`}><EnumLabel enum="Status" value={status} /></span>;
}

function Spinner({ label }: { label: string }) {
  return (
    <div className="flex flex-col items-center gap-3 py-16" role="status" aria-live="polite">
      <div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      <p className="text-sm text-slate-400">{label}</p>
    </div>
  );
}

function Empty({ icon: Icon, title, hint }: { icon: typeof FileClock; title: string; hint?: string }) {
  return (
    <div className="surface flex flex-col items-center px-4 py-16 text-center">
      <Icon className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
      <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{title}</p>
      {hint && <p className="mt-1 max-w-sm text-xs text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function KpiCard({ label, value, tone = 'default' }: { label: string; value: string; tone?: 'default' | 'warn' }) {
  return (
    <div className="surface p-4">
      <p className="text-xs font-medium uppercase tracking-wide text-slate-400">{label}</p>
      <p
        className={`mt-1 text-xl font-semibold ${
          tone === 'warn' ? 'text-amber-600 dark:text-amber-400' : 'text-slate-900 dark:text-white'
        }`}
      >
        {value}
      </p>
    </div>
  );
}

/**
 * The server answers a refused submit with a list of per-day violations. Rendering them as a
 * bulleted list rather than one run-on sentence is the difference between "fix these three days"
 * and "something is wrong".
 */
function ViolationList({ violations }: { violations: { date: string | null; message: string }[] }) {
  return (
    <ul className="mt-2 space-y-1 text-xs">
      {violations.map((v, i) => (
        <li key={`${v.date ?? 'general'}-${i}`} className="flex gap-2">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>{v.message}</span>
        </li>
      ))}
    </ul>
  );
}

// ══ My week ════════════════════════════════════════════════════════════════════════════════════

function MyWeekTab() {
  const t = useT();
  const tRef = useRef(t);
  tRef.current = t;
  const fx = useFormat();
  const toast = useAppToast();
  const [anchor, setAnchor] = useState<Date>(() => weekStart(new Date()));
  const [sheet, setSheet] = useState<Timesheet | null>(null);
  const [costCentres, setCostCentres] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [violations, setViolations] = useState<{ date: string | null; message: string }[]>([]);

  // One editable row per (day, cost centre) pair the employee has touched.
  const [rows, setRows] = useState<{ key: string; workDate: string; costCenterId: string; hours: string; notes: string }[]>([]);

  const hydrate = useCallback((data: Timesheet) => {
    setSheet(data);
    setRows(
      data.entries.map((e, i) => ({
        key: `${e.id}-${i}`,
        workDate: e.workDate.slice(0, 10),
        costCenterId: e.costCenterId ?? '',
        hours: minutesToInput(e.minutes),
        notes: e.notes,
      })),
    );
  }, []);

  const load = useCallback(
    async (date: Date) => {
      setLoading(true);
      setError(null);
      setViolations([]);
      try {
        hydrate(await essTimesheetsApi.current(toIso(date)));
      } catch (err) {
        setError(parseApiErrorForTimesheets(err, tRef.current('Your timesheet could not be opened.')).message);
      } finally {
        setLoading(false);
      }
    },
    [hydrate],
  );

  useEffect(() => {
    void load(anchor);
  }, [anchor, load]);

  useEffect(() => {
    costCentersApi
      .listAll()
      .then((all) => setCostCentres(all.filter((c) => c.isActive)))
      .catch(() => setCostCentres([]));
  }, []);

  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => addDays(anchor, i)), [anchor]);

  const dayTotals = useMemo(() => {
    const totals = new Map<string, number>();
    for (const row of rows) {
      const mins = parseHoursToMinutes(row.hours);
      if (mins === null) continue;
      totals.set(row.workDate, (totals.get(row.workDate) ?? 0) + mins);
    }
    return totals;
  }, [rows]);

  const weekTotal = useMemo(() => [...dayTotals.values()].reduce((a, b) => a + b, 0), [dayTotals]);
  const invalidRow = rows.some((r) => parseHoursToMinutes(r.hours) === null);
  const editable = sheet?.isEditable ?? false;

  const addRow = (workDate: string) =>
    setRows((prev) => [
      ...prev,
      { key: `new-${Date.now()}-${prev.length}`, workDate, costCenterId: '', hours: '', notes: '' },
    ]);

  const patchRow = (key: string, patch: Partial<(typeof rows)[number]>) =>
    setRows((prev) => prev.map((r) => (r.key === key ? { ...r, ...patch } : r)));

  const removeRow = (key: string) => setRows((prev) => prev.filter((r) => r.key !== key));

  const buildPayload = (): SaveTimesheetEntry[] =>
    rows
      .map((r) => ({
        workDate: r.workDate,
        costCenterId: r.costCenterId === '' ? null : r.costCenterId,
        minutes: parseHoursToMinutes(r.hours) ?? 0,
        notes: r.notes,
      }))
      .filter((e) => e.minutes > 0);

  const save = async (): Promise<Timesheet | null> => {
    if (!sheet) return null;
    setSaving(true);
    setError(null);
    setViolations([]);
    try {
      const updated = await essTimesheetsApi.saveEntries(sheet.id, buildPayload(), sheet.version);
      hydrate(updated);
      return updated;
    } catch (err) {
      const parsed = parseApiErrorForTimesheets(err, t('Your hours could not be saved.'));
      setError(parsed.message);
      setViolations(parsed.violations);
      return null;
    } finally {
      setSaving(false);
    }
  };

  const submit = async () => {
    const saved = await save();
    if (!saved) return;
    setSaving(true);
    try {
      hydrate(await essTimesheetsApi.submit(saved.id));
      toast.success(t('Your timesheet has gone to your approver.'), t('Submitted'));
    } catch (err) {
      const parsed = parseApiErrorForTimesheets(err, t('This timesheet could not be submitted.'));
      setError(parsed.message);
      setViolations(parsed.violations);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-4">
      {/* Week navigator */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-2">
          <button
            type="button"
            aria-label={t('Previous week')}
            onClick={() => setAnchor((w) => addDays(w, -7))}
            className="grid h-8 w-8 place-items-center rounded-lg border border-slate-200 text-slate-500 hover:bg-slate-50 dark:border-white/10 dark:text-slate-400 dark:hover:bg-white/10"
          >
            <ChevronLeft className="h-4 w-4" />
          </button>
          <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">
            {fmtRange(fx, toIso(anchor), toIso(addDays(anchor, 6)))}
          </span>
          <button
            type="button"
            aria-label={t('Next week')}
            onClick={() => setAnchor((w) => addDays(w, 7))}
            className="grid h-8 w-8 place-items-center rounded-lg border border-slate-200 text-slate-500 hover:bg-slate-50 dark:border-white/10 dark:text-slate-400 dark:hover:bg-white/10"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
          <button type="button" className={btn.sm} onClick={() => setAnchor(weekStart(new Date()))}>
            
            {t('This week')}
          </button>
        </div>
        <div className="flex items-center gap-3">
          {sheet && <StatusBadge status={sheet.status} />}
          <span className="text-sm text-slate-500 dark:text-slate-400">
            
            {t('Week total')} <strong className="text-slate-900 dark:text-white">{fmtHours(t, weekTotal)}</strong>
          </span>
        </div>
      </div>

      {error && (
        <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300" role="alert" aria-live="assertive">
          <p className="font-medium">{error}</p>
          {violations.length > 0 && <ViolationList violations={violations} />}
        </div>
      )}

      {sheet?.status === 'Rejected' && sheet.decisionComments && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
          <p className="font-medium">{t('Sent back by your approver')}</p>
          <p className="mt-0.5">{sheet.decisionComments}</p>
        </div>
      )}

      {loading && <div className="surface"><Spinner label={t('Opening your week…')} /></div>}

      {!loading && sheet && (
        <>
          {/* Day strip: logged vs attendance, the reconciliation the submit gate uses. */}
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-7">
            {days.map((d) => {
              const iso = toIso(d);
              const day = sheet.days.find((x) => x.date.slice(0, 10) === iso);
              const logged = dayTotals.get(iso) ?? 0;
              const over = day?.isOverAllocated ?? false;
              return (
                <div
                  key={iso}
                  className={`surface p-3 ${over ? 'border-amber-300 dark:border-amber-500/40' : ''}`}
                >
                  <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">
                    {fx.date(d, 'weekdayShort')}
                  </p>
                  <p className="text-caption text-slate-400">{fmtDay(fx, iso)}</p>
                  <p className="mt-1 text-sm font-semibold text-slate-900 dark:text-white">{fmtHours(t, logged)}</p>
                  <p className="mt-0.5 text-caption text-slate-400">
                    {day?.attendanceMinutes === null || day?.attendanceMinutes === undefined
                      ? t('No attendance')
                      : t('Attendance {hours}', { hours: fmtHours(t, day.attendanceMinutes) })}
                  </p>
                  {editable && (
                    <button
                      type="button"
                      className="mt-2 text-xs font-medium text-sapphire hover:underline dark:text-cyanAccent"
                      onClick={() => addRow(iso)}
                    >
                      
                      {t('+ Add')}
                    </button>
                  )}
                </div>
              );
            })}
          </div>

          {/* Entry rows */}
          <div className="surface overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full min-w-[640px] text-sm">
                <caption className="sr-only">{t('Hours entered for the week beginning {date}', { date: fmtDay(fx, toIso(anchor)) })}</caption>
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {[msg('Day'), msg('Cost centre'), msg('Hours'), msg('Notes'), ''].map((h, i) => (
                      <th key={`${h}-${i}`} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">
                        {h ? t(h) : <span className="sr-only">{t('Actions')}</span>}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {rows.length === 0 && (
                    <tr>
                      <td colSpan={5} className="px-3 py-10 text-center text-sm text-slate-400">
                        {editable ? t('Pick a day above and add your first entry.') : t('No hours were recorded for this week.')}
                      </td>
                    </tr>
                  )}
                  {rows.map((row) => {
                    const bad = parseHoursToMinutes(row.hours) === null;
                    return (
                      <tr key={row.key} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                        <td className="px-3 py-2">
                          <select
                            className={`${sel} w-36`}
                            aria-label={t('Day')}
                            disabled={!editable}
                            value={row.workDate}
                            onChange={(e) => patchRow(row.key, { workDate: e.target.value })}
                          >
                            {days.map((d) => (
                              <option key={toIso(d)} value={toIso(d)}>
                                {fx.date(d, 'weekdayShort')} {fmtDay(fx, toIso(d))}
                              </option>
                            ))}
                          </select>
                        </td>
                        <td className="px-3 py-2">
                          <select
                            className={`${sel} w-48`}
                            aria-label={t('Cost centre')}
                            disabled={!editable}
                            value={row.costCenterId}
                            onChange={(e) => patchRow(row.key, { costCenterId: e.target.value })}
                          >
                            <option value="">{t('Unattributed')}</option>
                            {costCentres.map((c) => (
                              <option key={c.id} value={c.id}>
                                {c.code} — {c.name}
                              </option>
                            ))}
                          </select>
                        </td>
                        <td className="px-3 py-2">
                          <input
                            className={`${inp} w-24 ${bad ? 'border-rose-400 dark:border-rose-500' : ''}`}
                            aria-label={t('Hours')}
                            aria-invalid={bad}
                            inputMode="decimal"
                            placeholder="7.5"
                            disabled={!editable}
                            value={row.hours}
                            onChange={(e) => patchRow(row.key, { hours: e.target.value })}
                          />
                        </td>
                        <td className="px-3 py-2">
                          <input
                            className={inp}
                            aria-label={t('Notes')}
                            maxLength={500}
                            disabled={!editable}
                            value={row.notes}
                            onChange={(e) => patchRow(row.key, { notes: e.target.value })}
                          />
                        </td>
                        <td className="px-3 py-2 text-end">
                          {editable && (
                            <button
                              type="button"
                              className={btn.sm}
                              aria-label={t('Remove entry for {date}', { date: fx.date(row.workDate, 'medium') })}
                              onClick={() => removeRow(row.key)}
                            >
                              
                              {t('Remove')}
                            </button>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>

          {editable ? (
            <div className="flex flex-col gap-2 sm:flex-row sm:justify-end">
              <button type="button" className={btn.ghost} disabled={saving || invalidRow} onClick={() => void save()}>
                {saving ? t('Saving…') : t('Save draft')}
              </button>
              <button type="button" className={btn.primary} disabled={saving || invalidRow || weekTotal === 0} onClick={() => void submit()}>
                <Send className="h-4 w-4" aria-hidden="true" />
                
                {t('Submit for approval')}
              </button>
            </div>
          ) : (
            <p className="text-end text-xs text-slate-400">
              {sheet.status === 'Submitted'
                ? (sheet.approval?.currentApproverRole
                    ? t('Awaiting {approver} — submitted {date}', { approver: sheet.approval.currentApproverRole, date: fmtDateTime(fx, sheet.submittedAtUtc) })
                    : t('Awaiting approval — submitted {date}', { date: fmtDateTime(fx, sheet.submittedAtUtc) }))
                : t('Decided {date}', { date: fmtDateTime(fx, sheet.decidedAtUtc) })}
            </p>
          )}
        </>
      )}
    </div>
  );
}

// ══ My history ═════════════════════════════════════════════════════════════════════════════════

function MyHistoryTab() {
  const t = useT();
  const tRef = useRef(t);
  tRef.current = t;
  const fx = useFormat();
  const [rows, setRows] = useState<TimesheetSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    essTimesheetsApi
      .mine(26)
      .then((r) => setRows(r))
      .catch((err) => setError(parseApiErrorForTimesheets(err, tRef.current('Your timesheets could not be loaded.')).message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) return <div className="surface"><Spinner label={t('Loading your timesheets…')} /></div>;
  if (error) return <ErrorBanner message={error} onDismiss={() => setError(null)} />;
  if (rows.length === 0) return <Empty icon={FileClock} title={t('No timesheets yet')} hint={t('Open “My week” and record some hours.')} />;

  return (
    <div className="surface overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[560px] text-sm">
          <thead>
            <tr className="border-b border-slate-100 dark:border-white/[0.07]">
              {[msg('Period'), msg('Hours'), msg('Status'), msg('Submitted'), msg('Decided')].map((h) => (
                <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {rows.map((r) => (
              <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{fmtRange(fx, r.periodStart, r.periodEnd)}</td>
                <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtHours(t, r.totalMinutes)}</td>
                <td className="px-3 py-2"><StatusBadge status={r.status} /></td>
                <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDateTime(fx, r.submittedAtUtc)}</td>
                <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDateTime(fx, r.decidedAtUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ══ Approvals ══════════════════════════════════════════════════════════════════════════════════

function ApprovalsTab() {
  const t = useT();
  const tRef = useRef(t);
  tRef.current = t;
  const fx = useFormat();
  const toast = useAppToast();
  const [items, setItems] = useState<TimesheetInboxItem[]>([]);
  const [queue, setQueue] = useState<'mine' | 'team' | 'all'>('mine');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [rejecting, setRejecting] = useState<TimesheetInboxItem | null>(null);
  const [reason, setReason] = useState('');

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    timesheetsApi
      .inbox(queue)
      .then((r) => setItems(r.items))
      .catch((err) => setError(parseApiErrorForTimesheets(err, tRef.current('The approval queue could not be loaded.')).message))
      .finally(() => setLoading(false));
  }, [queue]);

  useEffect(load, [load]);

  const decide = async (item: TimesheetInboxItem, decision: 'Approve' | 'Reject', comments?: string) => {
    setBusyId(item.timesheet.id);
    try {
      await timesheetsApi.decide(item.timesheet.id, decision, comments);
      toast.success(
        decision === 'Approve'
          ? t('Approved. The hours are now reconciled against attendance.')
          : t('Sent back to the employee.'),
        decision === 'Approve' ? t('Timesheet approved') : t('Timesheet rejected'),
      );
      setRejecting(null);
      setReason('');
      load();
    } catch (err) {
      notifyApiError(err, t('The decision could not be recorded.'));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <label className="text-xs font-medium uppercase tracking-wide text-slate-400" htmlFor="ts-queue">
          
          {t('Queue')}
        </label>
        <select
          id="ts-queue"
          className={`${sel} w-44`}
          value={queue}
          onChange={(e) => setQueue(e.target.value as 'mine' | 'team' | 'all')}
        >
          <option value="mine">{t('Waiting on me')}</option>
          <option value="team">{t('My team')}</option>
          <option value="all">{t('Everything')}</option>
        </select>
      </div>

      {error && <ErrorBanner message={error} onDismiss={() => setError(null)} />}
      {loading && <div className="surface"><Spinner label={t('Loading the approval queue…')} /></div>}

      {!loading && items.length === 0 && (
        <Empty icon={Inbox} title={t('Nothing waiting for you')} hint={t('Submitted timesheets routed to you will appear here.')} />
      )}

      {!loading &&
        items.map((item) => {
          const ts = item.timesheet;
          const flagged = ts.days.filter((d) => d.isOverAllocated);
          return (
            <div key={item.approvalRequestId} className="surface p-5">
              <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                <div className="min-w-0">
                  <p className="font-semibold text-slate-900 dark:text-white">{ts.employeeName}</p>
                  <p className="text-sm text-slate-500 dark:text-slate-400">
                    {fmtRange(fx, ts.periodStart, ts.periodEnd)} · <strong>{fmtHours(t, ts.totalMinutes)}</strong>
                  </p>
                  <p className="mt-0.5 text-xs text-slate-400">{t('Submitted {date}', { date: fmtDateTime(fx, ts.submittedAtUtc) })}</p>
                  {flagged.length > 0 && (
                    <p className="mt-2 inline-flex items-center gap-1.5 rounded-lg bg-amber-50 px-2 py-1 text-xs font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
                      <AlertTriangle className="h-3.5 w-3.5" aria-hidden="true" />
                      {t('{count, plural, one {# day above recorded attendance} other {# days above recorded attendance}}', { count: flagged.length })}
                    </p>
                  )}
                </div>
                <div className="flex shrink-0 gap-2">
                  <button
                    type="button"
                    className={btn.ghost}
                    disabled={busyId === ts.id}
                    onClick={() => {
                      setRejecting(item);
                      setReason('');
                    }}
                  >
                    
                    {t('Reject')}
                  </button>
                  <button
                    type="button"
                    className={btn.primary}
                    disabled={busyId === ts.id}
                    onClick={() => void decide(item, 'Approve')}
                  >
                    <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
                    {busyId === ts.id ? t('Working…') : t('Approve')}
                  </button>
                </div>
              </div>

              {/* The reconciliation the approver is actually deciding on. */}
              <div className="mt-4 overflow-x-auto">
                <table className="w-full min-w-[560px] text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                      {[msg('Day'), msg('Logged'), msg('Attendance'), msg('Variance')].map((h) => (
                        <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {ts.days.map((d) => (
                      <tr key={d.date} className={d.isOverAllocated ? 'bg-amber-50/60 dark:bg-amber-500/[0.06]' : ''}>
                        <td className="px-3 py-1.5 text-slate-900 dark:text-white">{fmtDay(fx, d.date)}</td>
                        <td className="px-3 py-1.5 text-slate-500 dark:text-slate-400">{fmtHours(t, d.loggedMinutes)}</td>
                        <td className="px-3 py-1.5 text-slate-500 dark:text-slate-400">
                          {d.attendanceMinutes === null ? <span className="text-slate-400">{t('No record')}</span> : fmtHours(t, d.attendanceMinutes)}
                        </td>
                        <td className="px-3 py-1.5 text-slate-500 dark:text-slate-400">{fmtHours(t, d.varianceMinutes)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          );
        })}

      <Modal
        isOpen={rejecting !== null}
        title={t('Send this timesheet back')}
        onClose={() => setRejecting(null)}
        footer={
          <div className="flex justify-end gap-2">
            <button type="button" className={btn.ghost} onClick={() => setRejecting(null)}>
              
              {t('Cancel')}
            </button>
            <button
              type="button"
              className={btn.primary}
              disabled={reason.trim().length === 0}
              onClick={() => rejecting && void decide(rejecting, 'Reject', reason.trim())}
            >
              
              {t('Send back')}
            </button>
          </div>
        }
      >
        <label className="block text-sm font-medium text-slate-700 dark:text-slate-300" htmlFor="ts-reject-reason">
          
          {t('Why? The employee sees this.')}
        </label>
        <textarea
          id="ts-reject-reason"
          className={`${inp} mt-2 h-28`}
          maxLength={1000}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          placeholder={t('For example, Thursday looks like it belongs to the other cost centre.')}
        />
      </Modal>
    </div>
  );
}

// ══ Register ═══════════════════════════════════════════════════════════════════════════════════

function RegisterTab() {
  const t = useT();
  const fx = useFormat();
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [errorDismissed, setErrorDismissed] = useState(false);

  // One page at a time with the server's total: the register used to show the first 100 as if
  // they were every timesheet. Later pages are one click away and the count is the server's.
  const list = usePagedList<TimesheetSummary>((page, pageSize) =>
    timesheetsApi.list({ status: status || undefined, search: search.trim() || undefined, page, pageSize }));
  const { items: rows, loading, reload } = list;
  const error = list.error != null && !errorDismissed
    ? parseApiErrorForTimesheets(list.error, t('Timesheets could not be loaded.')).message
    : null;

  useEffect(() => {
    const timer = setTimeout(() => { setErrorDismissed(false); void reload(); }, 250);
    return () => clearTimeout(timer);
  }, [status, search, reload]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <select className={`${sel} w-44`} aria-label={t('Status')} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">{t('All statuses')}</option>
          {['Draft', 'Submitted', 'Approved', 'Rejected'].map((s) => (
            <option key={s} value={s}>{enumLabel(t, 'Status', s)}</option>
          ))}
        </select>
        <input
          className={`${inp} w-60`}
          aria-label={t('Search by employee')}
          placeholder={t('Search employee…')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {error && <ErrorBanner message={error} onDismiss={() => setErrorDismissed(true)} />}
      {loading && <div className="surface"><Spinner label={t('Loading timesheets…')} /></div>}
      {!loading && !error && rows.length === 0 && (
        <Empty icon={FileClock} title={t('No timesheets match')} hint={t('Try clearing the filters.')} />
      )}

      {!loading && rows.length > 0 && (
        <div className="surface overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[720px] text-sm">
              <thead>
                <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                  {[msg('Employee'), msg('Period'), msg('Hours'), msg('Status'), msg('Submitted'), msg('Decided')].map((h) => (
                    <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                {rows.map((r) => (
                  <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                    <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{r.employeeName}</td>
                    <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtRange(fx, r.periodStart, r.periodEnd)}</td>
                    <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtHours(t, r.totalMinutes)}</td>
                    <td className="px-3 py-2"><StatusBadge status={r.status} /></td>
                    <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDateTime(fx, r.submittedAtUtc)}</td>
                    <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDateTime(fx, r.decidedAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <ListWindowFooter shown={rows.length} total={list.total} noun="timesheets" labels={{ window: t('Showing {shown} of {total} timesheets', { shown: fx.integer(rows.length), total: fx.integer(list.total ?? rows.length) }), loadMore: t('Load more'), loading: t('Loading…') }} loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} />
        </div>
      )}
    </div>
  );
}

// ══ Variance (the consumer, as HR sees it) ═════════════════════════════════════════════════════

function VarianceTab() {
  const t = useT();
  const tRef = useRef(t);
  tRef.current = t;
  const fx = useFormat();
  const today = toIso(new Date());
  const [from, setFrom] = useState(() => toIso(addDays(weekStart(new Date()), -28)));
  const [to, setTo] = useState(today);
  const [flaggedOnly, setFlaggedOnly] = useState(false);
  const [report, setReport] = useState<TimesheetVarianceReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    timesheetsApi
      .attendanceVariance({ from, to, overAllocatedOnly: flaggedOnly })
      .then(setReport)
      .catch((err) => setError(parseApiErrorForTimesheets(err, tRef.current('The variance report could not be run.')).message))
      .finally(() => setLoading(false));
  }, [from, to, flaggedOnly]);

  useEffect(load, [load]);

  return (
    <div className="space-y-4">
      <p className="max-w-3xl text-sm text-slate-500 dark:text-slate-400">
        
        {t('What approved timesheets say, against what attendance recorded. Rows are written when a week is approved, so this is the picture as it stood at the decision — not a figure recomputed today.')}
      </p>

      <div className="flex flex-wrap items-end gap-3">
        <div>
          <label className="mb-1 block text-xs font-medium uppercase tracking-wide text-slate-400" htmlFor="ts-var-from">{t('From')}</label>
          <input id="ts-var-from" type="date" className={`${inp} w-44`} value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div>
          <label className="mb-1 block text-xs font-medium uppercase tracking-wide text-slate-400" htmlFor="ts-var-to">{t('To')}</label>
          <input id="ts-var-to" type="date" className={`${inp} w-44`} value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
        <label className="flex items-center gap-2 pb-2 text-sm text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={flaggedOnly} onChange={(e) => setFlaggedOnly(e.target.checked)} />
          
          {t('Only days above attendance')}
        </label>
      </div>

      {error && <ErrorBanner message={error} onDismiss={() => setError(null)} />}
      {loading && <div className="surface"><Spinner label={t('Running the report…')} /></div>}

      {!loading && report && (
        <>
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <KpiCard label={t('Logged')} value={fmtHours(t, report.loggedMinutes)} />
            <KpiCard label={t('Attendance')} value={fmtHours(t, report.attendanceMinutes)} />
            <KpiCard label={t('Days without attendance')} value={String(report.daysWithoutAttendance)} />
            <KpiCard label={t('Days above attendance')} value={String(report.overAllocatedDays)} tone={report.overAllocatedDays > 0 ? 'warn' : 'default'} />
          </div>

          {report.items.length === 0 ? (
            <Empty
              icon={Scale}
              title={t('Nothing to reconcile in this window')}
              hint={t('Rows appear once a timesheet covering these dates is approved.')}
            />
          ) : (
            <div className="surface overflow-hidden">
              <div className="overflow-x-auto">
                <table className="w-full min-w-[720px] text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                      {[msg('Employee'), msg('Day'), msg('Logged'), msg('Attendance'), msg('Variance'), msg('Attendance status')].map((h) => (
                        <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {report.items.map((r) => (
                      <tr
                        key={`${r.timesheetId}-${r.workDate}`}
                        className={r.isOverAllocated ? 'bg-amber-50/60 dark:bg-amber-500/[0.06]' : 'hover:bg-slate-50 dark:hover:bg-white/[0.03]'}
                      >
                        <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{r.employeeName}</td>
                        <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDay(fx, r.workDate)}</td>
                        <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtHours(t, r.loggedMinutes)}</td>
                        <td className="px-3 py-2 text-slate-500 dark:text-slate-400">
                          {r.attendanceMinutes === null ? <span className="text-slate-400">{t('No record')}</span> : fmtHours(t, r.attendanceMinutes)}
                        </td>
                        <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtHours(t, r.varianceMinutes)}</td>
                        <td className="px-3 py-2 text-slate-500 dark:text-slate-400"><EnumLabel enum="AttendanceStatus" value={r.attendanceStatus} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ══ Page ═══════════════════════════════════════════════════════════════════════════════════════

type Tab = 'my-week' | 'my-history' | 'approvals' | 'register' | 'variance';

const TABS: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: 'my-week', label: msg('My week'), icon: CalendarClock },
  { id: 'my-history', label: msg('My timesheets'), icon: FileClock },
  { id: 'approvals', label: msg('Approvals'), icon: ClipboardCheck },
  { id: 'register', label: msg('All timesheets'), icon: Inbox },
  { id: 'variance', label: msg('Attendance variance'), icon: Scale },
];

export function TimesheetsPage() {
  const t = useT();
  const { user, hasPermission } = useAuth();

  const canSelfServe = hasPermission('ess.read');
  const canOversee = hasPermission('attendance.read') || hasPermission('manager.read');
  const canDecide = hasPermission('approvals.decide');

  const visibleTabs = useMemo(
    () =>
      TABS.filter((tab) => {
        if (tab.id === 'my-week' || tab.id === 'my-history') return canSelfServe;
        if (tab.id === 'approvals') return canDecide;
        return canOversee;
      }),
    [canSelfServe, canDecide, canOversee],
  );

  const [tab, setTab] = useState<Tab>(() => visibleTabs[0]?.id ?? 'my-week');

  useEffect(() => {
    if (!visibleTabs.some((v) => v.id === tab) && visibleTabs.length > 0) setTab(visibleTabs[0].id);
  }, [visibleTabs, tab]);

  const role = canDecide || canOversee ? (canSelfServe ? t('Manager') : t('Admin / HR')) : t('Employee');

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Timesheets')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            
            {t('Record the week, send it for approval, and see it against the attendance on record.')}
          </p>
        </div>
        <span className="shrink-0 rounded-full bg-sapphire/10 px-3 py-1 text-xs font-semibold text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent">
          {role}
        </span>
      </div>

      {visibleTabs.length === 0 ? (
        <Empty icon={FileClock} title={t('You do not have access to timesheets')} />
      ) : (
        <>
          <RovingTabList
            items={visibleTabs.map(({ id, label, icon }) => ({ id, label: t(label), icon }))}
            activeId={tab}
            onChange={setTab}
            idPrefix="timesheets"
            label={t('Timesheet sections')}
          />
          <TabPanel idPrefix="timesheets" tabId={tab}>
            {tab === 'my-week' && <MyWeekTab />}
            {tab === 'my-history' && <MyHistoryTab />}
            {tab === 'approvals' && <ApprovalsTab />}
            {tab === 'register' && <RegisterTab />}
            {tab === 'variance' && <VarianceTab />}
          </TabPanel>
        </>
      )}

      {user === null && <p className="sr-only">{t('Loading your session…')}</p>}
    </div>
  );
}
