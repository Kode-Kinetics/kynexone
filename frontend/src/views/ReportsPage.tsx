'use client';

import { useCallback, useEffect, useState } from 'react';
import { notifyApiError } from '../api/client';
import { useSearchParams } from 'next/navigation';
import {
  BarChart2, BookOpen, Clock, Download, Play, Plus, RefreshCw, Save, Trash2, ToggleLeft, ToggleRight,
} from 'lucide-react';
import dynamic from 'next/dynamic';
import { reportsApi, analyticsApi } from '../api/reports';

const ReportsHeadcountTrendChart = dynamic(
  () => import('../components/charts/reports/ReportsHeadcountTrendChart').then((m) => m.ReportsHeadcountTrendChart),
  { ssr: false },
);
const ReportsPayrollTrendChart = dynamic(
  () => import('../components/charts/reports/ReportsPayrollTrendChart').then((m) => m.ReportsPayrollTrendChart),
  { ssr: false },
);
const ReportsAttendanceTrendChart = dynamic(
  () => import('../components/charts/reports/ReportsAttendanceTrendChart').then((m) => m.ReportsAttendanceTrendChart),
  { ssr: false },
);
const ReportsLeaveTrendChart = dynamic(
  () => import('../components/charts/reports/ReportsLeaveTrendChart').then((m) => m.ReportsLeaveTrendChart),
  { ssr: false },
);
import type {
  ReportCatalogItem, ReportFilters, ReportResult, SavedReport,
  ReportSchedule, ReportExecutionLog, AnalyticsKPIs,
} from '../api/reports';
import { Modal } from '../components/Modal';

type Tab = 'analytics' | 'library' | 'saved' | 'schedules' | 'executions';

const tabs: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'analytics', label: 'Analytics Dashboard', icon: BarChart2 },
  { id: 'library', label: 'Report Library', icon: BookOpen },
  { id: 'saved', label: 'Saved Reports', icon: Save },
  { id: 'schedules', label: 'Scheduled Reports', icon: Clock },
  { id: 'executions', label: 'Execution History', icon: RefreshCw },
];

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmt(n: number) { return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }

function KpiCard({ label, value, sub }: { label: string; value: string | number; sub?: string }) {
  return (
    <div className="surface p-4">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-bold text-slate-900 dark:text-white">{value}</p>
      {sub && <p className="mt-0.5 text-xs text-slate-400">{sub}</p>}
    </div>
  );
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: React.ReactNode }) {
  return (
    <div>
      <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label}{required && <span className="ml-0.5 text-red-500">*</span>}
      </label>
      {children}
    </div>
  );
}

function FormError({ error }: { error: string }) {
  if (!error) return null;
  return <p className="mb-3 rounded-lg bg-red-50 px-3 py-2.5 text-sm text-red-600 dark:bg-red-500/10 dark:text-red-400">{error}</p>;
}

const CHART_COLORS = ['#2F6BFF', '#00C896', '#5EEBFF', '#F59E0B', '#EF4444', '#8B5CF6'];

// ── Analytics Dashboard ───────────────────────────────────────────────────────

function AnalyticsDashboard() {
  const [kpis, setKpis] = useState<AnalyticsKPIs | null>(null);
  const [headcountTrend, setHeadcountTrend] = useState<{ period: string; headcount: number }[]>([]);
  const [payrollTrend, setPayrollTrend] = useState<{ period: string; TotalNetSalary: number; TotalGrossSalary: number }[]>([]);
  const [attendanceTrend, setAttendanceTrend] = useState<{ date: string; present: number; absent: number; late: number }[]>([]);
  const [leaveTrend, setLeaveTrend] = useState<{ period: string; totalRequests: number; totalDays: number }[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const load = async () => {
      setLoading(true);
      try {
        const [k, hc, pr, at, lv] = await Promise.allSettled([
          analyticsApi.kpis(),
          analyticsApi.headcountTrend(6),
          analyticsApi.payrollTrend(6),
          analyticsApi.attendanceTrend(30),
          analyticsApi.leaveTrend(6),
        ]);
        if (k.status === 'fulfilled') setKpis(k.value as AnalyticsKPIs);
        if (hc.status === 'fulfilled') setHeadcountTrend(hc.value as { period: string; headcount: number }[]);
        if (pr.status === 'fulfilled') setPayrollTrend(pr.value as { period: string; TotalNetSalary: number; TotalGrossSalary: number }[]);
        if (at.status === 'fulfilled') setAttendanceTrend(at.value as { date: string; present: number; absent: number; late: number }[]);
        if (lv.status === 'fulfilled') setLeaveTrend(lv.value as { period: string; totalRequests: number; totalDays: number }[]);
      } finally {
        setLoading(false);
      }
    };
    load();
  }, []);

  if (loading) return <div className="flex justify-center py-24"><div className="h-8 w-8 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>;

  return (
    <div className="space-y-6">
      {/* KPI Section */}
      {kpis && (
        <>
          <div>
            <h3 className="mb-3 text-sm font-bold uppercase tracking-wide text-slate-400">Workforce</h3>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <KpiCard label="Active Employees" value={kpis.headcount.totalActive} />
              <KpiCard label="New This Month" value={kpis.headcount.newThisMonth} />
              <KpiCard label="Exits This Month" value={kpis.headcount.exitsThisMonth} />
              <KpiCard label="Open Positions" value={kpis.recruitment.openPositions} />
            </div>
          </div>
          <div>
            <h3 className="mb-3 text-sm font-bold uppercase tracking-wide text-slate-400">Today</h3>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <KpiCard label="Present Today" value={kpis.attendance.presentToday} />
              <KpiCard label="Late Today" value={kpis.attendance.lateToday} />
              <KpiCard label="On Leave" value={kpis.leave.onLeaveToday} />
              <KpiCard label="Pending Leave" value={kpis.leave.pendingLeave} />
            </div>
          </div>
          <div>
            <h3 className="mb-3 text-sm font-bold uppercase tracking-wide text-slate-400">Finance & Compliance</h3>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <KpiCard label="Pending OT" value={kpis.overtime.pendingOT} />
              <KpiCard label="Active Loans" value={kpis.financial.activeLoans} sub={`Outstanding: ${fmt(kpis.financial.outstandingLoanBalance)}`} />
              <KpiCard label="Visas Expiring (30d)" value={kpis.compliance.visasExpiring} />
              <KpiCard label="Passports Expiring (30d)" value={kpis.compliance.passportsExpiring} />
            </div>
          </div>
          {kpis.payroll.lastRunYear && (
            <div>
              <h3 className="mb-3 text-sm font-bold uppercase tracking-wide text-slate-400">Last Payroll Run</h3>
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                <KpiCard label="Period" value={`${kpis.payroll.lastRunYear}-${String(kpis.payroll.lastRunMonth).padStart(2, '0')}`} />
                <KpiCard label="Status" value={kpis.payroll.lastRunStatus ?? '—'} />
                <KpiCard label="Total Net Salary" value={kpis.payroll.totalNetSalary ? fmt(kpis.payroll.totalNetSalary) : '—'} />
              </div>
            </div>
          )}
        </>
      )}

      {/* Charts */}
      <div className="grid gap-4 lg:grid-cols-2">
        {headcountTrend.length > 0 && (
          <div className="surface p-4">
            <h3 className="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-300">Headcount Trend</h3>
            <ReportsHeadcountTrendChart data={headcountTrend} />
          </div>
        )}

        {payrollTrend.length > 0 && (
          <div className="surface p-4">
            <h3 className="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-300">Payroll Trend (Net Salary)</h3>
            <ReportsPayrollTrendChart data={payrollTrend} />
          </div>
        )}

        {attendanceTrend.length > 0 && (
          <div className="surface p-4">
            <h3 className="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-300">Attendance (Last 30 Days)</h3>
            <ReportsAttendanceTrendChart data={attendanceTrend} />
          </div>
        )}

        {leaveTrend.length > 0 && (
          <div className="surface p-4">
            <h3 className="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-300">Leave Taken (Days)</h3>
            <ReportsLeaveTrendChart data={leaveTrend} />
          </div>
        )}
      </div>
    </div>
  );
}

// ── Report Library & Runner ───────────────────────────────────────────────────

const CATEGORIES = ['HR', 'Attendance', 'Leave', 'Overtime', 'Payroll', 'Recruitment', 'Compliance', 'Finance'];

function ReportLibrary() {
  const searchParams = useSearchParams();
  const [catalog, setCatalog] = useState<ReportCatalogItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedReport, setSelectedReport] = useState<ReportCatalogItem | null>(null);
  const [filterCat, setFilterCat] = useState('');
  const [filters, setFilters] = useState<ReportFilters>({});
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<ReportResult | null>(null);
  const [runError, setRunError] = useState('');
  const [saveModal, setSaveModal] = useState(false);
  const [saveName, setSaveName] = useState('');
  const [saveShared, setSaveShared] = useState(false);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try { setCatalog(await reportsApi.catalog()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    const reportKey = searchParams?.get('report') ?? null;
    if (!reportKey || catalog.length === 0) return;
    const selected = catalog.find((item) => item.key === reportKey);
    if (selected) {
      setSelectedReport(selected);
      setFilters({});
      setResult(null);
      setRunError('');
    }
  }, [catalog, searchParams]);

  const displayed = filterCat ? catalog.filter((r) => r.category === filterCat) : catalog;

  const runReport = async () => {
    if (!selectedReport) return;
    setRunning(true); setRunError(''); setResult(null);
    try { setResult(await reportsApi.run(selectedReport.key, filters)); }
    catch { setRunError('Failed to run report. Please try again.'); }
    finally { setRunning(false); }
  };

  const saveReport = async () => {
    if (!selectedReport || !saveName.trim()) return;
    setSaving(true);
    try {
      await reportsApi.save({ reportKey: selectedReport.key, name: saveName, category: selectedReport.category, filters, isShared: saveShared });
      setSaveModal(false);
    } catch { /**/ }
    finally { setSaving(false); }
  };

  const columns = result && result.data.length > 0 ? Object.keys(result.data[0] as object) : [];

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <select value={filterCat} onChange={(e) => setFilterCat(e.target.value)} className="select" title="Filter by category">
          <option value="">All Categories</option>
          {CATEGORIES.map((c) => <option key={c}>{c}</option>)}
        </select>
        <span className="text-xs text-slate-400">{displayed.length} reports</span>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {loading ? (
          <div className="col-span-3 flex justify-center py-12"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
        ) : displayed.map((r) => (
          <button
            key={r.key}
            type="button"
            onClick={() => { setSelectedReport(r); setFilters({}); setResult(null); setRunError(''); }}
            className={`surface p-4 text-left transition hover:border-sapphire/40 ${selectedReport?.key === r.key ? 'border border-sapphire bg-sapphire/5' : ''}`}
          >
            <div className="mb-1 flex items-center justify-between">
              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-400">{r.category}</span>
            </div>
            <p className="font-semibold text-slate-900 dark:text-white">{r.name}</p>
            <p className="mt-0.5 text-xs text-slate-400">{r.description}</p>
          </button>
        ))}
      </div>

      {selectedReport && (
        <div className="surface p-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="font-semibold text-slate-900 dark:text-white">{selectedReport.name}</h3>
              <p className="text-xs text-slate-400">{selectedReport.description}</p>
            </div>
            <div className="flex gap-2">
              {result && (
                <button type="button" onClick={() => { setSaveName(selectedReport.name); setSaveShared(false); setSaveModal(true); }} className="btn-secondary h-8 px-3 text-sm">
                  <Save className="h-3.5 w-3.5" /> Save
                </button>
              )}
              <button type="button" onClick={runReport} disabled={running} className="btn-primary h-8 px-3 text-sm disabled:opacity-60">
                {running ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Play className="h-3.5 w-3.5" />}
                {running ? 'Running…' : 'Run Report'}
              </button>
            </div>
          </div>

          {/* Filters */}
          <div className="grid grid-cols-3 gap-3">
            <FormField label="Date From">
              <input type="date" value={filters.dateFrom ?? ''} onChange={(e) => setFilters(x => ({ ...x, dateFrom: e.target.value || undefined }))} className="input w-full" />
            </FormField>
            <FormField label="Date To">
              <input type="date" value={filters.dateTo ?? ''} onChange={(e) => setFilters(x => ({ ...x, dateTo: e.target.value || undefined }))} className="input w-full" />
            </FormField>
            <FormField label="Status">
              <input type="text" value={filters.status ?? ''} onChange={(e) => setFilters(x => ({ ...x, status: e.target.value || undefined }))} className="input w-full" placeholder="e.g. Active" />
            </FormField>
            <FormField label="Department">
              <input type="text" value={filters.department ?? ''} onChange={(e) => setFilters(x => ({ ...x, department: e.target.value || undefined }))} className="input w-full" placeholder="Optional" />
            </FormField>
            <FormField label="Location / Branch">
              <input type="text" value={filters.location ?? ''} onChange={(e) => setFilters(x => ({ ...x, location: e.target.value || undefined }))} className="input w-full" placeholder="Optional" />
            </FormField>
            <FormField label="Period">
              <input type="text" value={filters.period ?? ''} onChange={(e) => setFilters(x => ({ ...x, period: e.target.value || undefined }))} className="input w-full" placeholder="e.g. 2025-01" />
            </FormField>
            <FormField label="Days Ahead">
              <input type="number" value={filters.daysAhead ?? ''} onChange={(e) => setFilters(x => ({ ...x, daysAhead: e.target.value ? Number(e.target.value) : undefined }))} className="input w-full" placeholder="30" />
            </FormField>
          </div>

          {runError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-600 dark:bg-red-500/10 dark:text-red-400">{runError}</p>}

          {result && (
            <div className="space-y-2">
              <div className="flex items-center gap-4 text-xs text-slate-400">
                <span>{result.rowCount} rows</span>
                <span>{result.durationMs}ms</span>
                <span>{new Date(result.generatedAt).toLocaleTimeString()}</span>
              </div>
              <div className="overflow-x-auto max-h-80 surface">
                <table className="w-full text-xs">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/10 sticky top-0 bg-white dark:bg-slate-900">
                      {columns.map((c) => (
                        <th key={c} className="px-3 py-2 text-left font-bold uppercase tracking-wide text-slate-400 whitespace-nowrap">{c}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {result.data.slice(0, 200).map((row, i) => (
                      <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                        {columns.map((c) => (
                          <td key={c} className="px-3 py-1.5 text-slate-600 dark:text-slate-300 whitespace-nowrap">
                            {String((row as Record<string, unknown>)[c] ?? '—')}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {result.rowCount > 200 && <p className="text-xs text-slate-400">Showing first 200 of {result.rowCount} rows</p>}
            </div>
          )}
        </div>
      )}

      <Modal isOpen={saveModal} title="Save Report" onClose={() => setSaveModal(false)}
        footer={<><button type="button" onClick={() => setSaveModal(false)} className="btn-secondary">Cancel</button><button type="button" onClick={saveReport} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <div className="space-y-3">
          <FormField label="Report Name" required><input value={saveName} onChange={(e) => setSaveName(e.target.value)} className="input w-full" placeholder="My Report" autoFocus /></FormField>
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" checked={saveShared} onChange={(e) => setSaveShared(e.target.checked)} className="h-4 w-4 accent-sapphire" title="Share with team" /> Share with team
          </label>
        </div>
      </Modal>
    </div>
  );
}

// ── Saved Reports ─────────────────────────────────────────────────────────────

function SavedReportsTab() {
  const [items, setItems] = useState<SavedReport[]>([]);
  const [loading, setLoading] = useState(true);
  const [running, setRunning] = useState<string | null>(null);
  const [result, setResult] = useState<{ report: SavedReport; data: ReportResult } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await reportsApi.listSaved()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const runSaved = async (r: SavedReport) => {
    setRunning(r.id);
    try {
      const filters = r.filtersJson ? JSON.parse(r.filtersJson) : {};
      const data = await reportsApi.run(r.reportKey, filters);
      setResult({ report: r, data });
    } catch { /**/ }
    finally { setRunning(null); }
  };

  const deleteSaved = async (id: string) => {
    if (!confirm('Delete this saved report?')) return;
    try { await reportsApi.deleteSaved(id); load(); } catch (e) { notifyApiError(e); }
  };

  const columns = result && result.data.data.length > 0 ? Object.keys(result.data.data[0] as object) : [];

  return (
    <div className="space-y-4">
      <div className="surface overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-100 dark:border-white/[0.07]">
              {['Name', 'Category', 'Report', 'Created By', 'Shared', ''].map((h) => (
                <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {loading ? (
              <tr><td colSpan={6} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={6} className="py-12 text-center text-slate-400">No saved reports. Run a report and click Save.</td></tr>
            ) : items.map((r) => (
              <tr key={r.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{r.name}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.category}</td>
                <td className="px-4 py-3 font-mono text-xs text-slate-400">{r.reportKey}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.createdByName}</td>
                <td className="px-4 py-3">{r.isShared ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">Shared</span> : '—'}</td>
                <td className="px-4 py-3">
                  <div className="flex gap-1 opacity-0 group-hover:opacity-100">
                    <button type="button" onClick={() => runSaved(r)} disabled={running === r.id} className="btn-primary h-6 px-2 text-xs disabled:opacity-60">
                      {running === r.id ? <RefreshCw className="h-3 w-3 animate-spin" /> : <Play className="h-3 w-3" />} Run
                    </button>
                    <button type="button" onClick={() => deleteSaved(r.id)} className="h-6 w-6 flex items-center justify-center rounded-lg border border-red-200 text-red-500 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20" aria-label="Delete saved report">
                      <Trash2 className="h-3 w-3" />
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {result && (
        <div className="surface p-4 space-y-3">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="font-semibold text-slate-900 dark:text-white">{result.report.name}</h3>
              <p className="text-xs text-slate-400">{result.data.rowCount} rows · {result.data.durationMs}ms</p>
            </div>
            <button type="button" onClick={() => setResult(null)} className="btn-secondary h-7 px-2 text-xs">Clear</button>
          </div>
          <div className="overflow-x-auto max-h-64 surface">
            <table className="w-full text-xs">
              <thead><tr className="border-b border-slate-100 dark:border-white/10">{columns.map((c) => <th key={c} className="px-3 py-2 text-left font-bold uppercase tracking-wide text-slate-400 whitespace-nowrap">{c}</th>)}</tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                {result.data.data.slice(0, 100).map((row, i) => (
                  <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                    {columns.map((c) => <td key={c} className="px-3 py-1.5 text-slate-600 dark:text-slate-300 whitespace-nowrap">{String((row as Record<string, unknown>)[c] ?? '—')}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Scheduled Reports ─────────────────────────────────────────────────────────

function ScheduledReportsTab() {
  const [items, setItems] = useState<ReportSchedule[]>([]);
  const [catalog, setCatalog] = useState<ReportCatalogItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [createModal, setCreateModal] = useState(false);
  const [form, setForm] = useState({ reportKey: '', reportName: '', category: '', frequency: 'Daily', deliveryMethod: 'Email', recipients: '', exportFormat: 'Excel' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [toggling, setToggling] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [s, c] = await Promise.all([reportsApi.listSchedules(), reportsApi.catalog()]);
      setItems(s); setCatalog(c);
    } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const createSchedule = async () => {
    if (!form.reportKey || !form.recipients.trim()) { setError('Report and recipients are required'); return; }
    setSaving(true); setError('');
    try { await reportsApi.createSchedule(form); setCreateModal(false); load(); }
    catch { setError('Failed to create schedule.'); }
    finally { setSaving(false); }
  };

  const toggle = async (id: string) => {
    setToggling(id);
    try { await reportsApi.toggleSchedule(id); load(); } catch { /**/ }
    finally { setToggling(null); }
  };

  const deleteSchedule = async (id: string) => {
    if (!confirm('Delete this schedule?')) return;
    try { await reportsApi.deleteSchedule(id); load(); } catch (e) { notifyApiError(e); }
  };

  const selectReport = (key: string) => {
    const r = catalog.find((c) => c.key === key);
    setForm(x => ({ ...x, reportKey: key, reportName: r?.name ?? key, category: r?.category ?? '' }));
  };

  return (
    <>
      <div className="space-y-4">
        <div className="flex justify-end">
          <button type="button" onClick={() => { setForm({ reportKey: catalog[0]?.key ?? '', reportName: catalog[0]?.name ?? '', category: catalog[0]?.category ?? '', frequency: 'Daily', deliveryMethod: 'Email', recipients: '', exportFormat: 'Excel' }); setError(''); setCreateModal(true); }} className="btn-primary">
            <Plus className="h-4 w-4" /> New Schedule
          </button>
        </div>
        <div className="surface overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Report', 'Frequency', 'Delivery', 'Recipients', 'Format', 'Next Run', 'Active', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading ? (
                <tr><td colSpan={8} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
              ) : items.length === 0 ? (
                <tr><td colSpan={8} className="py-12 text-center text-slate-400">No schedules configured</td></tr>
              ) : items.map((s) => (
                <tr key={s.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{s.reportName}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{s.frequency}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{s.deliveryMethod}</td>
                  <td className="px-4 py-3 text-slate-500 text-xs max-w-[150px] truncate">{s.recipients}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{s.exportFormat}</td>
                  <td className="px-4 py-3 text-slate-500 text-xs">{s.nextRunAtUtc ? new Date(s.nextRunAtUtc).toLocaleDateString() : '—'}</td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => toggle(s.id)} disabled={toggling === s.id} aria-label="Toggle schedule" className="text-slate-400 hover:text-sapphire disabled:opacity-50 transition">
                      {s.isActive ? <ToggleRight className="h-5 w-5 text-emerald-500" /> : <ToggleLeft className="h-5 w-5" />}
                    </button>
                  </td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => deleteSchedule(s.id)} className="h-6 w-6 flex items-center justify-center rounded-lg border border-red-200 text-red-500 hover:bg-red-50 opacity-0 group-hover:opacity-100 dark:border-red-800 dark:hover:bg-red-900/20" aria-label="Delete schedule">
                      <Trash2 className="h-3 w-3" />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <Modal isOpen={createModal} title="New Report Schedule" onClose={() => setCreateModal(false)} size="lg"
        footer={<><button type="button" onClick={() => setCreateModal(false)} className="btn-secondary">Cancel</button><button type="button" onClick={createSchedule} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Creating…' : 'Create'}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <div className="col-span-2">
            <FormField label="Report" required>
              <select value={form.reportKey} onChange={(e) => selectReport(e.target.value)} className="select w-full" title="Report">
                <option value="">Select report</option>
                {catalog.map((r) => <option key={r.key} value={r.key}>{r.category} — {r.name}</option>)}
              </select>
            </FormField>
          </div>
          <FormField label="Frequency">
            <select value={form.frequency} onChange={(e) => setForm(x => ({ ...x, frequency: e.target.value }))} className="select w-full" title="Frequency">
              {['Daily', 'Weekly', 'Monthly', 'Quarterly'].map((v) => <option key={v}>{v}</option>)}
            </select>
          </FormField>
          <FormField label="Delivery Method">
            <select value={form.deliveryMethod} onChange={(e) => setForm(x => ({ ...x, deliveryMethod: e.target.value }))} className="select w-full" title="Delivery Method">
              {['Email', 'SFTP', 'Portal'].map((v) => <option key={v}>{v}</option>)}
            </select>
          </FormField>
          <FormField label="Export Format">
            <select value={form.exportFormat} onChange={(e) => setForm(x => ({ ...x, exportFormat: e.target.value }))} className="select w-full" title="Export Format">
              {['Excel', 'CSV', 'PDF'].map((v) => <option key={v}>{v}</option>)}
            </select>
          </FormField>
          <div className="col-span-2">
            <FormField label="Recipients" required>
              <input value={form.recipients} onChange={(e) => setForm(x => ({ ...x, recipients: e.target.value }))} className="input w-full" placeholder="hr@company.com, finance@company.com" />
            </FormField>
          </div>
        </div>
      </Modal>
    </>
  );
}

// ── Execution History ─────────────────────────────────────────────────────────

function ExecutionHistoryTab() {
  const [items, setItems] = useState<ReportExecutionLog[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await reportsApi.executions({ page, pageSize }); setItems(r.items); setTotal(r.total); } catch { /**/ }
    finally { setLoading(false); }
  }, [page]);
  useEffect(() => { load(); }, [load]);

  const statusColor = (s: string) => s === 'Success' ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400' : s === 'Failed' ? 'bg-red-500/10 text-red-500' : 'bg-amber-500/10 text-amber-600';

  return (
    <div className="space-y-4">
      <div className="surface overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-100 dark:border-white/[0.07]">
              {['Report', 'Format', 'Rows', 'Duration', 'Run By', 'Time', 'Status'].map((h) => (
                <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {loading ? (
              <tr><td colSpan={7} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={7} className="py-12 text-center text-slate-400">No executions yet</td></tr>
            ) : items.map((log) => (
              <tr key={log.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{log.reportName}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{log.exportFormat}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{log.rowCount.toLocaleString()}</td>
                <td className="px-4 py-3 text-slate-500 text-xs">{log.durationMs}ms</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{log.runByName}</td>
                <td className="px-4 py-3 text-slate-500 text-xs whitespace-nowrap">{new Date(log.createdAtUtc).toLocaleString()}</td>
                <td className="px-4 py-3">
                  <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusColor(log.status)}`}>{log.status}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {total > pageSize && (
        <div className="flex items-center justify-between text-sm text-slate-500">
          <span>{total} total</span>
          <div className="flex gap-2">
            <button type="button" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page === 1} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">← Prev</button>
            <span className="px-2">Page {page} of {Math.ceil(total / pageSize)}</span>
            <button type="button" onClick={() => setPage((p) => p + 1)} disabled={page >= Math.ceil(total / pageSize)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Next →</button>
          </div>
        </div>
      )}
    </div>
  );
}

// ── ReportsPage ───────────────────────────────────────────────────────────────

export function ReportsPage() {
  const [activeTab, setActiveTab] = useState<Tab>('analytics');

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Reports & Analytics</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          Enterprise reporting, live analytics, and scheduled delivery
        </p>
      </div>

      <div className="flex items-center gap-1 border-b border-slate-200 dark:border-white/[0.08]">
        {tabs.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            onClick={() => setActiveTab(id)}
            className={`flex items-center gap-2 border-b-2 px-4 py-2.5 text-sm font-semibold transition whitespace-nowrap ${
              activeTab === id
                ? 'border-sapphire text-sapphire'
                : 'border-transparent text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
            }`}
          >
            <Icon className="h-4 w-4" />
            {label}
          </button>
        ))}
      </div>

      <div>
        {activeTab === 'analytics' && <AnalyticsDashboard />}
        {activeTab === 'library' && <ReportLibrary />}
        {activeTab === 'saved' && <SavedReportsTab />}
        {activeTab === 'schedules' && <ScheduledReportsTab />}
        {activeTab === 'executions' && <ExecutionHistoryTab />}
      </div>
    </div>
  );
}
