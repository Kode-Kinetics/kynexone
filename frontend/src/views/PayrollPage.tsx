'use client';

import React, { useEffect, useState } from 'react';
import {
  AlertTriangle, BarChart2, Bot, BookOpen, Building2, Calculator,
  CheckCircle2, ChevronDown, ChevronRight, Download, FileText, Landmark, Layers3,
  Lock, Play, Plus, RefreshCw, RotateCcw,
  Settings, TrendingUp, Users, WalletCards, X,
  Zap, Shield, Sparkles, ArrowUpRight, Circle,
} from 'lucide-react';
import {
  payrollApi,
  type PayrollRun, type PayrollSlip, type PayrollValidationResult,
  type SalaryStructure, type EmployeeSalaryStructure, type Payslip,
  type PayrollPaymentBatch, type PayrollPaymentRecord,
  type PayrollApproval, type PayrollSummary,
  type PayrollGLJournal, type PayrollReconciliation, type FinalSettlementResult,
  type PayrollCompany, type PayrollOverview, type PayrollReadiness,
  type PayrollCompanySummary, type AIInsight,
} from '../api/payroll';
import client from '../api/client';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import { InfoTip } from '../components/InfoTip';
import { useAuth } from '../contexts/AuthContext';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

// ── Payroll import/export helpers ───────────────────────────────────────────────

const salaryStructuresImportExport = {
  export: async () => {
    const csv = await client.get<string>('/api/payroll/salary-structures/export', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'salary-structures.csv');
  },
  template: async () => {
    const csv = await client.get<string>('/api/payroll/salary-structures/import-template', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'salary-structures-template.csv');
  },
  import: (csvContent: string) =>
    client.post<{ received: number; created: number; skipped: number; errors: string[] }>('/api/payroll/salary-structures/import', { csvContent }).then(r => r.data),
};

// ── Shared helpers ──────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined) {
  if (!s) return '—';
  return new Date(s).toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' });
}

function fmtAmt(n: number, currency = 'USD') {
  return `${currency} ${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

const STATUS_COLOR: Record<string, string> = {
  Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
  Processed: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  PendingFinanceReview: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400',
  Approved: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
  Locked: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Pending: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Paid: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Failed: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Generated: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  FileGenerated: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  SentBack: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Warning: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Error: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Info: 'bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-400',
};

function StatusBadge({ status }: { status: string }) {
  const cls = STATUS_COLOR[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${cls}`}>{status.replace(/([A-Z])/g, ' $1').trim()}</span>;
}

function KpiCard({ label, value, sub, icon: Icon, color }: {
  label: string; value: string | number; sub?: string;
  icon: React.ComponentType<{ className?: string }>; color: string;
}) {
  return (
    <div className="surface flex items-center gap-4 p-4">
      <div className={`grid h-10 w-10 shrink-0 place-items-center rounded-xl ${color}`}>
        <Icon className="h-5 w-5" />
      </div>
      <div className="min-w-0">
        <p className="text-2xl font-bold tabular-nums text-slate-900 dark:text-white">{value}</p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">{label}</p>
        {sub && <p className="text-[10px] text-slate-400 dark:text-slate-500">{sub}</p>}
      </div>
    </div>
  );
}

function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className={`relative w-full rounded-2xl bg-white shadow-2xl dark:bg-slate-800 ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
        <div className="sticky top-0 flex items-center justify-between border-b border-slate-100 bg-white px-6 py-4 dark:border-white/10 dark:bg-slate-800">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
            <X className="h-4 w-4 text-slate-500" />
          </button>
        </div>
        <div className="p-6">{children}</div>
      </div>
    </div>
  );
}

function Field({ label, children, info, infoKey }: { label: string; children: React.ReactNode; info?: string; infoKey?: string }) {
  return (
    <div>
      <label className="mb-1 flex items-center gap-1.5 text-xs font-medium text-slate-600 dark:text-slate-300">{label}{info && <InfoTip text={info} fieldKey={infoKey} />}</label>
      {children}
    </div>
  );
}

const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const sel = `${inp} appearance-none`;
const btn = {
  primary: 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50',
  ghost: 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  danger: 'inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50',
  sm: 'inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
};

// ── Tabs ────────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'salary-structures' | 'employee-salary' | 'runs' | 'validation' | 'approvals' | 'payslips' | 'bank-wps' | 'payment-tracking' | 'reports' | 'eosb' | 'ai-validation' | 'gl-journal' | 'reconciliation' | 'final-settlement';

const TABS: { key: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { key: 'dashboard',        label: 'Dashboard',          icon: BarChart2 },
  { key: 'salary-structures', label: 'Salary Structures',  icon: Layers3 },
  { key: 'employee-salary',  label: 'Employee Salary',     icon: Users },
  { key: 'runs',             label: 'Payroll Runs',        icon: Play },
  { key: 'validation',       label: 'Validation',          icon: AlertTriangle },
  { key: 'approvals',        label: 'Approvals',           icon: CheckCircle2 },
  { key: 'payslips',         label: 'Payslips',            icon: FileText },
  { key: 'bank-wps',         label: 'Bank / WPS Files',    icon: Landmark },
  { key: 'payment-tracking', label: 'Payment Tracking',    icon: TrendingUp },
  { key: 'reports',          label: 'Reports',             icon: BarChart2 },
  { key: 'eosb',             label: 'EOSB / Gratuity',    icon: FileText },
  { key: 'gl-journal',       label: 'GL Journal',          icon: BookOpen },
  { key: 'reconciliation',   label: 'Reconciliation',      icon: RefreshCw },
  { key: 'final-settlement', label: 'Final Settlement',    icon: Calculator },
  { key: 'ai-validation',    label: 'AI Validation',       icon: Bot },
];

// ── Payroll Setup Wizard ───────────────────────────────────────────────────────

function PayrollSetupWizard({ readiness, onNavigate }: { readiness: PayrollReadiness; onNavigate: (t: Tab) => void }) {
  const stepToTab: Record<number, Tab> = {
    1: 'salary-structures',
    2: 'salary-structures',
    3: 'employee-salary',
    4: 'runs',
    5: 'validation',
    6: 'approvals',
  };

  return (
    <div className="surface overflow-hidden">
      <div className="border-b border-slate-100 bg-gradient-to-r from-sapphire/5 to-transparent px-6 py-4 dark:border-white/10 dark:from-cyanAccent/5">
        <div className="flex items-center gap-3">
          <div className="grid h-9 w-9 place-items-center rounded-xl bg-sapphire/10 dark:bg-cyanAccent/10">
            <Settings className="h-5 w-5 text-sapphire dark:text-cyanAccent" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Payroll Setup Wizard</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">Complete these steps before running payroll · {readiness.completionPercent}% done</p>
          </div>
          <div className="ml-auto">
            <div className="h-2 w-32 overflow-hidden rounded-full bg-slate-200 dark:bg-white/10">
              <div
                className="h-full rounded-full bg-sapphire transition-all dark:bg-cyanAccent [width:var(--progress-w)]"
                style={{ '--progress-w': `${readiness.completionPercent}%` } as React.CSSProperties}
              />
            </div>
          </div>
        </div>
      </div>
      <div className="divide-y divide-slate-100 dark:divide-white/5">
        {readiness.steps.map((s) => (
          <div key={s.step} className="flex items-center gap-4 px-6 py-3.5">
            <div className={`grid h-7 w-7 shrink-0 place-items-center rounded-full text-xs font-bold ${s.complete ? 'bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'}`}>
              {s.complete ? <CheckCircle2 className="h-4 w-4" /> : s.step}
            </div>
            <div className="min-w-0 flex-1">
              <p className={`text-sm font-medium ${s.complete ? 'text-slate-500 line-through dark:text-slate-500' : 'text-slate-800 dark:text-white'}`}>{s.label}</p>
              <p className="text-xs text-slate-400 dark:text-slate-500">{s.detail}</p>
            </div>
            {!s.complete && (
              <button type="button" onClick={() => onNavigate(stepToTab[s.step] ?? 'salary-structures')}
                className="shrink-0 inline-flex items-center gap-1 rounded-lg bg-sapphire/10 px-3 py-1.5 text-xs font-medium text-sapphire hover:bg-sapphire/20 dark:bg-cyanAccent/10 dark:text-cyanAccent dark:hover:bg-cyanAccent/20">
                Configure <ChevronRight className="h-3 w-3" />
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Company Bird's-Eye Table ───────────────────────────────────────────────────

function CompanyBirdsEyeTable({ overview, onDrillDown }: { overview: PayrollOverview; onDrillDown: (company: PayrollCompanySummary) => void }) {
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">
          All Group Companies — {MONTHS[overview.month - 1]} {overview.year}
        </h3>
        <span className="text-xs text-slate-400">{overview.totalCompanies} companies · {overview.totalActiveEmployees.toLocaleString()} employees</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b border-slate-100 bg-slate-50 dark:border-white/5 dark:bg-white/3">
              {['Company', 'Employees', 'Salary Coverage', 'Gross Payroll', 'Net Payroll', 'Errors', 'Pending Approvals', 'Status', ''].map(h => (
                <th key={h} className="px-4 py-2.5 text-left font-medium text-slate-500 dark:text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {overview.companies.map(c => (
              <tr key={c.companyId} className="group hover:bg-slate-50 dark:hover:bg-white/3">
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-white">{c.companyName}</p>
                  {c.tradeName && c.tradeName !== c.companyName && <p className="text-slate-400">{c.tradeName}</p>}
                </td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.activeEmployees.toLocaleString()}</td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <div className="h-1.5 w-16 overflow-hidden rounded-full bg-slate-200 dark:bg-white/10">
                      <div
                        className={`h-full rounded-full transition-all [width:var(--cov-w)] ${c.salaryCoveragePercent >= 90 ? 'bg-emerald-500' : c.salaryCoveragePercent >= 70 ? 'bg-amber-500' : 'bg-rose-500'}`}
                        style={{ '--cov-w': `${c.salaryCoveragePercent}%` } as React.CSSProperties}
                      />
                    </div>
                    <span className={c.salaryCoveragePercent < 70 ? 'font-semibold text-rose-600 dark:text-rose-400' : 'text-slate-600 dark:text-slate-300'}>
                      {c.salaryCoveragePercent.toFixed(0)}%
                    </span>
                  </div>
                  {c.employeesMissingSalary > 0 && <p className="mt-0.5 text-rose-500">{c.employeesMissingSalary} missing</p>}
                </td>
                <td className="px-4 py-3 font-mono text-slate-700 dark:text-slate-300">{c.hasPayrollRun ? fmtAmt(c.grossPayroll, c.currency) : '—'}</td>
                <td className="px-4 py-3 font-mono font-semibold text-slate-800 dark:text-white">{c.hasPayrollRun ? fmtAmt(c.netPayroll, c.currency) : '—'}</td>
                <td className="px-4 py-3">
                  {c.validationErrors > 0
                    ? <span className="inline-flex items-center gap-1 rounded-md bg-rose-50 px-1.5 py-0.5 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400"><AlertTriangle className="h-3 w-3" />{c.validationErrors}</span>
                    : <span className="text-slate-300 dark:text-slate-600">—</span>}
                </td>
                <td className="px-4 py-3">
                  {c.pendingApprovals > 0
                    ? <span className="inline-flex items-center gap-1 rounded-md bg-amber-50 px-1.5 py-0.5 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400"><Circle className="h-2 w-2 fill-current" />{c.pendingApprovals}</span>
                    : <span className="text-slate-300 dark:text-slate-600">—</span>}
                </td>
                <td className="px-4 py-3">{c.payrollRunStatus ? <StatusBadge status={c.payrollRunStatus} /> : <span className="rounded-md bg-slate-100 px-2 py-0.5 text-slate-500 dark:bg-white/10 dark:text-slate-400">Not started</span>}</td>
                <td className="px-4 py-3">
                  <button type="button" onClick={() => onDrillDown(c)}
                    className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-600 opacity-0 transition-opacity hover:bg-slate-50 group-hover:opacity-100 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
                    Drill down <ArrowUpRight className="h-3 w-3" />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="border-t border-slate-100 bg-slate-50 px-5 py-3 dark:border-white/5 dark:bg-white/3">
        <div className="flex items-center gap-8 text-xs">
          <span className="text-slate-500 dark:text-slate-400">Group totals:</span>
          <span className="font-semibold text-slate-800 dark:text-white">Gross: {fmtAmt(overview.totalGrossPayroll)}</span>
          <span className="font-semibold text-emerald-600 dark:text-emerald-400">Net: {fmtAmt(overview.totalNetPayroll)}</span>
          {overview.totalValidationErrors > 0 && <span className="font-semibold text-rose-600 dark:text-rose-400">{overview.totalValidationErrors} errors</span>}
          {overview.totalPendingApprovals > 0 && <span className="font-semibold text-amber-600 dark:text-amber-400">{overview.totalPendingApprovals} pending approvals</span>}
        </div>
      </div>
    </div>
  );
}

// ── AI Insights Panel ─────────────────────────────────────────────────────────

function AiInsightsPanel() {
  const [insights, setInsights] = useState<AIInsight[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    client.get<{ items: AIInsight[] }>('/api/ai/insights', { params: { acknowledged: false, pageSize: 5 } })
      .then(r => setInsights(r.data.items))
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const severityStyle: Record<string, string> = {
    Critical: 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/5',
    Warning: 'border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5',
    Info: 'border-blue-200 bg-blue-50 dark:border-blue-500/20 dark:bg-blue-500/5',
  };
  const severityIcon: Record<string, string> = {
    Critical: 'text-rose-600 dark:text-rose-400',
    Warning: 'text-amber-600 dark:text-amber-400',
    Info: 'text-blue-600 dark:text-blue-400',
  };

  if (loading) return null;
  if (insights.length === 0) return (
    <div className="surface flex items-center gap-3 p-5">
      <Sparkles className="h-4 w-4 shrink-0 text-emerald-500" />
      <p className="text-sm text-slate-500 dark:text-slate-400">AI engine has no active alerts — all payroll and HR signals look normal.</p>
    </div>
  );

  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center gap-2 border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
        <Sparkles className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">AI Insights</h3>
        <span className="ml-auto rounded-full bg-rose-100 px-2 py-0.5 text-xs font-semibold text-rose-600 dark:bg-rose-500/20 dark:text-rose-400">{insights.length} active</span>
      </div>
      <div className="divide-y divide-slate-100 dark:divide-white/5">
        {insights.map(ins => (
          <div key={ins.id} className={`mx-4 my-2 rounded-xl border p-3.5 ${severityStyle[ins.severity] ?? severityStyle.Info}`}>
            <div className="flex items-start gap-3">
              <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${severityIcon[ins.severity] ?? severityIcon.Info}`} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <p className="text-xs font-semibold text-slate-800 dark:text-white">{ins.title}</p>
                  <span className="rounded-full bg-white/60 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-white/10 dark:text-slate-400">{ins.module}</span>
                </div>
                <p className="mt-1 text-xs leading-relaxed text-slate-600 dark:text-slate-300">{ins.summary}</p>
                <p className="mt-1.5 text-[10px] text-slate-400">{new Date(ins.createdAtUtc).toLocaleString()}</p>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Dashboard Tab (Command Center) ─────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (t: Tab) => void }) {
  const now = new Date();
  const [selectedCompanyId, setSelectedCompanyId] = useState<string>('all');
  const [selectedYear, setSelectedYear] = useState(now.getFullYear());
  const [selectedMonth, setSelectedMonth] = useState(now.getMonth() + 1);
  const [companies, setCompanies] = useState<PayrollCompany[]>([]);
  const [overview, setOverview] = useState<PayrollOverview | null>(null);
  const [readiness, setReadiness] = useState<PayrollReadiness | null>(null);
  const [drillDown, setDrillDown] = useState<PayrollCompanySummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState<PayrollSummary | null>(null);

  const load = () => {
    setLoading(true);
    const params = {
      companyId: selectedCompanyId !== 'all' ? selectedCompanyId : undefined,
      year: selectedYear,
      month: selectedMonth,
    };
    Promise.all([
      payrollApi.listCompanies().then(setCompanies).catch(() => {}),
      payrollApi.getOverview(params).then(setOverview).catch(() => {}),
      payrollApi.getReadiness(params).then(setReadiness).catch(() => {}),
      payrollApi.reportSummary().then(setSummary).catch(() => {}),
    ]).finally(() => setLoading(false));
  };

  useEffect(load, [selectedCompanyId, selectedYear, selectedMonth]);

  const isNotConfigured = !loading && readiness && readiness.completionPercent < 30;

  const years = Array.from({ length: 3 }, (_, i) => now.getFullYear() - i);

  return (
    <div className="space-y-5">
      {/* ── Scope Selector ── */}
      <div className="surface flex flex-wrap items-center gap-3 p-3.5">
        <Building2 className="h-4 w-4 shrink-0 text-slate-400" />
        <select
          aria-label="Company scope"
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedCompanyId}
          onChange={e => { setSelectedCompanyId(e.target.value); setDrillDown(null); }}
        >
          <option value="all">All Group Companies</option>
          {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <select
          aria-label="Month"
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedMonth}
          onChange={e => setSelectedMonth(Number(e.target.value))}
        >
          {MONTHS.map((m, i) => <option key={i} value={i + 1}>{m}</option>)}
        </select>
        <select
          aria-label="Year"
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedYear}
          onChange={e => setSelectedYear(Number(e.target.value))}
        >
          {years.map(y => <option key={y}>{y}</option>)}
        </select>
        <button type="button" onClick={load} className="ml-auto inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
          <RefreshCw className="h-3.5 w-3.5" /> Refresh
        </button>
      </div>

      {/* ── Setup Wizard (when payroll not yet configured) ── */}
      {isNotConfigured && readiness && (
        <div className="rounded-xl border border-amber-200 bg-amber-50 px-5 py-4 dark:border-amber-500/20 dark:bg-amber-500/5">
          <div className="mb-3 flex items-center gap-2">
            <AlertTriangle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
            <p className="text-sm font-semibold text-amber-800 dark:text-amber-300">Payroll is not yet configured</p>
          </div>
          <p className="mb-4 text-xs text-amber-700 dark:text-amber-400">
            Complete the setup steps below to enable payroll processing. Once configured, this dashboard will show live payroll data.
          </p>
          <PayrollSetupWizard readiness={readiness} onNavigate={onNavigate} />
        </div>
      )}

      {/* ── Summary KPIs ── */}
      {!loading && overview && (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <KpiCard label="Active Employees" value={overview.totalActiveEmployees.toLocaleString()} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label="Gross Payroll" value={overview.totalGrossPayroll > 0 ? fmtAmt(overview.totalGrossPayroll) : '—'} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
          <KpiCard label="Net Payroll" value={overview.totalNetPayroll > 0 ? fmtAmt(overview.totalNetPayroll) : '—'} icon={TrendingUp} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label="Locked Runs YTD" value={summary?.lockedRuns ?? '—'} icon={Lock} color="bg-violet-100 text-violet-600 dark:bg-violet-500/20 dark:text-violet-400" />
        </div>
      )}

      {loading && (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          {[0, 1, 2, 3].map(i => <div key={i} className="surface h-20 animate-pulse rounded-xl" />)}
        </div>
      )}

      {/* ── AI Insights Panel ── */}
      <AiInsightsPanel />

      {/* ── Drill-down panel (when a company row is clicked) ── */}
      {drillDown && (
        <div className="surface overflow-hidden">
          <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
            <div className="flex items-center gap-2">
              <Building2 className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{drillDown.companyName} — Payroll Drill-Down</h3>
            </div>
            <button type="button" aria-label="Close drill-down" onClick={() => setDrillDown(null)} className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
              <X className="h-4 w-4 text-slate-400" />
            </button>
          </div>
          <div className="grid grid-cols-2 gap-4 p-5 lg:grid-cols-4">
            <div>
              <p className="text-xs text-slate-400">Active Employees</p>
              <p className="text-xl font-bold text-slate-800 dark:text-white">{drillDown.activeEmployees}</p>
              {drillDown.employeesMissingSalary > 0 && <p className="text-xs text-rose-500">{drillDown.employeesMissingSalary} missing salary</p>}
            </div>
            <div>
              <p className="text-xs text-slate-400">Gross Payroll</p>
              <p className="text-xl font-bold text-slate-800 dark:text-white">{drillDown.hasPayrollRun ? fmtAmt(drillDown.grossPayroll, drillDown.currency) : '—'}</p>
            </div>
            <div>
              <p className="text-xs text-slate-400">Net Payroll</p>
              <p className="text-xl font-bold text-emerald-600 dark:text-emerald-400">{drillDown.hasPayrollRun ? fmtAmt(drillDown.netPayroll, drillDown.currency) : '—'}</p>
            </div>
            <div>
              <p className="text-xs text-slate-400">Run Status</p>
              {drillDown.payrollRunStatus
                ? <StatusBadge status={drillDown.payrollRunStatus} />
                : <span className="text-sm text-slate-400">Not started</span>}
            </div>
          </div>
          <div className="flex flex-wrap gap-2 border-t border-slate-100 px-5 py-3.5 dark:border-white/10">
            {drillDown.validationErrors > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-3 py-1 text-xs font-medium text-rose-600 dark:bg-rose-500/10 dark:text-rose-400"><AlertTriangle className="h-3 w-3" />{drillDown.validationErrors} validation error(s)</span>}
            {drillDown.validationWarnings > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-amber-50 px-3 py-1 text-xs font-medium text-amber-600 dark:bg-amber-500/10 dark:text-amber-400"><AlertTriangle className="h-3 w-3" />{drillDown.validationWarnings} warning(s)</span>}
            {drillDown.pendingApprovals > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-3 py-1 text-xs font-medium text-blue-600 dark:bg-blue-500/10 dark:text-blue-400"><CheckCircle2 className="h-3 w-3" />{drillDown.pendingApprovals} pending approval(s)</span>}
            <button type="button" onClick={() => onNavigate('runs')} className="inline-flex items-center gap-1 rounded-full bg-sapphire/10 px-3 py-1 text-xs font-medium text-sapphire hover:bg-sapphire/20 dark:bg-cyanAccent/10 dark:text-cyanAccent">
              Go to Payroll Runs <ChevronRight className="h-3 w-3" />
            </button>
          </div>
        </div>
      )}

      {/* ── Company bird's-eye view (All Group Companies) ── */}
      {!loading && overview && selectedCompanyId === 'all' && overview.companies.length > 0 && (
        <CompanyBirdsEyeTable overview={overview} onDrillDown={setDrillDown} />
      )}

      {/* ── Readiness progress (when single company selected) ── */}
      {!loading && readiness && selectedCompanyId !== 'all' && !isNotConfigured && (
        <PayrollSetupWizard readiness={readiness} onNavigate={onNavigate} />
      )}

      {/* ── Quick actions ── */}
      <div className="surface p-5">
        <h3 className="mb-3 text-sm font-semibold text-slate-800 dark:text-white">Quick Actions</h3>
        <div className="grid grid-cols-2 gap-2 lg:grid-cols-3">
          {([
            ['New Payroll Run', 'runs', Plus],
            ['Salary Structures', 'salary-structures', Layers3],
            ['Employee Salary', 'employee-salary', Users],
            ['Validation', 'validation', AlertTriangle],
            ['Approvals', 'approvals', CheckCircle2],
            ['AI Validation', 'ai-validation', Bot],
          ] as [string, Tab, React.ComponentType<{ className?: string }>][]).map(([label, t, Icon]) => (
            <button key={t} type="button" onClick={() => onNavigate(t)}
              className="flex items-center gap-3 rounded-xl border border-slate-200 p-3 text-left hover:border-sapphire/40 hover:bg-sapphire/3 dark:border-white/10 dark:hover:border-cyanAccent/30 dark:hover:bg-cyanAccent/5">
              <div className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
                <Icon className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              </div>
              <span className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

// ── Salary Structures Tab ───────────────────────────────────────────────────────

function CreateSalaryStructureModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { currencyCode } = useTenantSettings();
  const [form, setForm] = useState({ code: '', name: '', currency: '', effectiveDate: new Date().toISOString().slice(0, 10) });
  const effectiveCurrency = form.currency || currencyCode;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.code || !form.name) { setError('Code and name are required.'); return; }
    setSaving(true); setError('');
    try { await payrollApi.createSalaryStructure({ ...form, components: [] }); onSaved(); } catch { setError('Save failed.'); setSaving(false); }
  };

  return (
    <Modal title="New Salary Structure" onClose={onClose}>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code *" info="Short unique reference for this salary structure, e.g. GCC-STD. Used when assigning salaries to employees." infoKey="payroll.structure_code"><input aria-label="Structure code" className={inp} value={form.code} onChange={e => set('code', e.target.value)} placeholder="e.g. GCC-STD" /></Field>
          <Field label="Name *"><input aria-label="Structure name" className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder="e.g. GCC Standard" /></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Currency">
            <select aria-label="Currency" className={sel} value={form.currency} onChange={e => set('currency', e.target.value)}>
              {['USD', 'GBP', 'EUR', 'CAD', 'AUD', 'SGD', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR', 'EGP', 'INR', 'PKR', 'PHP', 'JOD', 'ZAR', 'NGN', 'KES'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
          <Field label="Effective Date"><input type="date" aria-label="Effective date" className={inp} value={form.effectiveDate} onChange={e => set('effectiveDate', e.target.value)} /></Field>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Create Structure'}</button>
        </div>
      </div>
    </Modal>
  );
}

function SalaryStructuresTab() {
  const [structures, setStructures] = useState<SalaryStructure[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);

  const load = () => { setLoading(true); payrollApi.listSalaryStructures().then(setStructures).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <ImportExportToolbar
          entityName="Salary Structures"
          onExport={salaryStructuresImportExport.export}
          onDownloadTemplate={salaryStructuresImportExport.template}
          onImport={salaryStructuresImportExport.import}
        />
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Structure</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : structures.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Layers3 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No salary structures yet</p>
          <p className="text-xs text-slate-400">Create a salary structure to define components like Basic, Housing, Transport allowances.</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {structures.map(s => (
            <div key={s.id} className="surface p-4">
              <div className="mb-2 flex items-start justify-between gap-2">
                <div>
                  <p className="font-semibold text-slate-900 dark:text-white">{s.name}</p>
                  <p className="text-xs text-slate-400">{s.code}</p>
                </div>
                <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${s.isActive ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500'}`}>{s.isActive ? 'Active' : 'Inactive'}</span>
              </div>
              <p className="mt-2 text-xs text-slate-500">Currency: <span className="font-medium text-slate-700 dark:text-slate-300">{s.currency}</span></p>
              <p className="text-xs text-slate-500">Effective: <span className="font-medium text-slate-700 dark:text-slate-300">{fmtDate(s.effectiveDate)}</span></p>
            </div>
          ))}
        </div>
      )}
      {showCreate && <CreateSalaryStructureModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
    </div>
  );
}

// ── Employee Salary Setup Tab ───────────────────────────────────────────────────

function AssignSalaryModal({ structures, onClose, onSaved }: { structures: SalaryStructure[]; onClose: () => void; onSaved: () => void }) {
  const today = new Date().toISOString().slice(0, 10);
  const { currencyCode } = useTenantSettings();
  const [form, setForm] = useState({
    employeeId: '', salaryStructureId: '', basicSalary: '', housingAllowance: '',
    transportAllowance: '', foodAllowance: '', mobileAllowance: '', otherAllowance: '',
    fixedDeduction: '', effectiveDate: today, currency: '',
  });
  const effectiveCurrency = form.currency || currencyCode;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.employeeId || !form.salaryStructureId || !form.basicSalary) { setError('Employee ID, salary structure, and basic salary are required.'); return; }
    setSaving(true); setError('');
    try {
      await payrollApi.assignEmployeeSalary({
        employeeId: Number(form.employeeId), salaryStructureId: form.salaryStructureId,
        basicSalary: Number(form.basicSalary), housingAllowance: Number(form.housingAllowance) || 0,
        transportAllowance: Number(form.transportAllowance) || 0, foodAllowance: Number(form.foodAllowance) || 0,
        mobileAllowance: Number(form.mobileAllowance) || 0, otherAllowance: Number(form.otherAllowance) || 0,
        fixedDeduction: Number(form.fixedDeduction) || 0, effectiveDate: form.effectiveDate, currency: form.currency,
      });
      onSaved();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Save failed.'); setSaving(false);
    }
  };

  const gross = ['basicSalary', 'housingAllowance', 'transportAllowance', 'foodAllowance', 'mobileAllowance', 'otherAllowance']
    .reduce((sum, k) => sum + (Number(form[k as keyof typeof form]) || 0), 0);

  return (
    <Modal title="Assign Salary to Employee" onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Employee ID *"><input type="number" aria-label="Employee ID" className={inp} value={form.employeeId} onChange={e => set('employeeId', e.target.value)} /></Field>
          <Field label="Salary Structure *" info="The pay template (basic %, allowances, deductions) applied to this employee. Defined under the Structures tab." infoKey="payroll.salary_structure">
            <select aria-label="Salary structure" className={sel} value={form.salaryStructureId} onChange={e => set('salaryStructureId', e.target.value)}>
              <option value="">Select…</option>
              {structures.map(s => <option key={s.id} value={s.id}>{s.name} ({s.currency})</option>)}
            </select>
          </Field>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Earnings</p>
          <div className="grid grid-cols-3 gap-3">
            {[
              ['basicSalary', 'Basic Salary *'], ['housingAllowance', 'Housing Allowance'],
              ['transportAllowance', 'Transport Allowance'], ['foodAllowance', 'Food Allowance'],
              ['mobileAllowance', 'Mobile Allowance'], ['otherAllowance', 'Other Allowances'],
            ].map(([k, label]) => (
              <Field key={k} label={label}><input type="number" step="0.01" aria-label={label} className={inp} value={form[k as keyof typeof form]} onChange={e => set(k as keyof typeof form, e.target.value)} /></Field>
            ))}
          </div>
          <div className="mt-3 rounded-lg bg-slate-50 px-4 py-2.5 dark:bg-white/5">
            <p className="text-sm font-semibold text-sapphire dark:text-cyanAccent">Gross Total: {form.currency} {gross.toLocaleString('en-US', { minimumFractionDigits: 2 })}</p>
          </div>
        </div>

        <div className="grid grid-cols-3 gap-3">
          <Field label="Fixed Deduction" info="A fixed amount removed every month (e.g. accommodation recovery), in the salary currency. Numbers only." infoKey="payroll.fixed_deduction"><input type="number" step="0.01" aria-label="Fixed deduction" className={inp} value={form.fixedDeduction} onChange={e => set('fixedDeduction', e.target.value)} /></Field>
          <Field label="Effective Date"><input type="date" aria-label="Effective date" className={inp} value={form.effectiveDate} onChange={e => set('effectiveDate', e.target.value)} /></Field>
          <Field label="Currency">
            <select aria-label="Currency" className={sel} value={effectiveCurrency} onChange={e => set('currency', e.target.value)}>
              {['USD', 'GBP', 'EUR', 'CAD', 'AUD', 'SGD', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR', 'EGP', 'INR', 'PKR', 'PHP', 'JOD', 'ZAR', 'NGN', 'KES'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
        </div>

        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Assign Salary'}</button>
        </div>
      </div>
    </Modal>
  );
}

function EmployeeSalaryTab() {
  const [assignments, setAssignments] = useState<EmployeeSalaryStructure[]>([]);
  const [structures, setStructures] = useState<SalaryStructure[]>([]);
  const [loading, setLoading] = useState(true);
  const [empIdFilter, setEmpIdFilter] = useState('');
  const [showAssign, setShowAssign] = useState(false);

  const load = () => {
    setLoading(true);
    Promise.all([
      payrollApi.listEmployeeSalaryStructures(empIdFilter ? Number(empIdFilter) : undefined).then(setAssignments),
      payrollApi.listSalaryStructures().then(setStructures),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const structName = (id: string) => structures.find(s => s.id === id)?.name ?? id.slice(0, 8);

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <input type="number" aria-label="Employee ID filter" placeholder="Filter by Employee ID" className={`${inp} w-52`} value={empIdFilter} onChange={e => setEmpIdFilter(e.target.value)} />
        <button type="button" className={btn.ghost} onClick={load}>Search</button>
        <button type="button" className={`ml-auto ${btn.primary}`} onClick={() => setShowAssign(true)}><Plus className="h-4 w-4" /> Assign Salary</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : assignments.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Users className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No salary assignments found</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full min-w-[640px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'Structure', 'Basic', 'Housing', 'Transport', 'Gross', 'Deduction', 'Effective', 'Status'].map(h => (
                  <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {assignments.map(a => {
                const gross = a.basicSalary + a.housingAllowance + a.transportAllowance + a.foodAllowance + a.mobileAllowance + a.otherAllowance;
                return (
                  <tr key={a.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                    <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">Emp #{a.employeeId}</td>
                    <td className="px-3 py-2 text-slate-500">{structName(a.salaryStructureId)}</td>
                    <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{a.basicSalary.toLocaleString()}</td>
                    <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{a.housingAllowance.toLocaleString()}</td>
                    <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{a.transportAllowance.toLocaleString()}</td>
                    <td className="px-3 py-2 text-right font-semibold text-slate-900 dark:text-white">{gross.toLocaleString()}</td>
                    <td className="px-3 py-2 text-right text-rose-500">{a.fixedDeduction > 0 ? `(${a.fixedDeduction.toLocaleString()})` : '—'}</td>
                    <td className="px-3 py-2 text-xs text-slate-400">{fmtDate(a.effectiveDate)}</td>
                    <td className="px-3 py-2"><span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${a.isActive ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500'}`}>{a.isActive ? 'Active' : 'Inactive'}</span></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {showAssign && <AssignSalaryModal structures={structures} onClose={() => setShowAssign(false)} onSaved={() => { setShowAssign(false); load(); }} />}
    </div>
  );
}

// ── Payroll Runs Tab ────────────────────────────────────────────────────────────

function RunsTab({ onSelectRun }: { onSelectRun: (run: PayrollRun, tab: Tab) => void }) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [selectedRun, setSelectedRun] = useState<PayrollRun | null>(null);
  const [slips, setSlips] = useState<PayrollSlip[]>([]);
  const [slipsLoading, setSlipsLoading] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [createYear, setCreateYear] = useState(new Date().getFullYear());
  const [createMonth, setCreateMonth] = useState(new Date().getMonth() + 1);
  const [createCompanyId, setCreateCompanyId] = useState('');
  const [companies, setCompanies] = useState<{ id: string; name: string }[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = () => {
    setLoading(true);
    payrollApi.listRuns({ pageSize: 50 }).then(r => { setRuns(r.items); setTotal(r.total); }).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => {
    load();
    payrollApi.listCompanies().then(cs => {
      setCompanies(cs.map(c => ({ id: c.id, name: c.name || c.tradeName })));
      if (cs.length === 1) setCreateCompanyId(cs[0].id);
    }).catch(() => {});
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const openSlips = async (run: PayrollRun) => {
    setSelectedRun(run);
    setSlipsLoading(true);
    payrollApi.slips(run.id, { pageSize: 200 }).then(r => setSlips(r.items)).catch(() => {}).finally(() => setSlipsLoading(false));
  };

  const createRun = async () => {
    setSaving(true); setError('');
    try {
      await payrollApi.createRun(createYear, createMonth, createCompanyId || undefined);
      setShowCreate(false); load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to create payroll run.');
    } finally { setSaving(false); }
  };

  const processRun = async (id: string) => {
    const updated = await payrollApi.processRun(id).catch(() => null);
    if (updated) { setRuns(rs => rs.map(r => r.id === id ? updated : r)); if (selectedRun?.id === id) { setSelectedRun(updated); openSlips(updated); } }
  };

  const lockRun = async (id: string) => {
    const updated = await payrollApi.lockRun(id).catch(() => null);
    if (updated) { setRuns(rs => rs.map(r => r.id === id ? updated : r)); if (selectedRun?.id === id) setSelectedRun(updated); }
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500">{total} payroll run{total !== 1 ? 's' : ''}</p>
        <button type="button" className={btn.primary} onClick={() => { setShowCreate(true); setError(''); }}><Plus className="h-4 w-4" /> New Payroll Run</button>
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
            <p className="text-sm font-semibold text-slate-900 dark:text-white">Payroll Runs</p>
          </div>
          {loading && <div className="flex justify-center py-10"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>}
          {!loading && runs.length === 0 && <p className="py-10 text-center text-sm text-slate-400">No runs yet. Create one above.</p>}
          <div className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {runs.map(run => (
              <div key={run.id} role="button" tabIndex={0} onClick={() => openSlips(run)}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openSlips(run); } }}
                className={`w-full cursor-pointer px-4 py-3 text-left transition hover:bg-slate-50 dark:hover:bg-white/[0.03] ${selectedRun?.id === run.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}>
                <div className="flex items-center justify-between">
                  <p className="font-semibold text-slate-900 dark:text-white">{MONTHS[run.month - 1]} {run.year}</p>
                  <StatusBadge status={run.status} />
                </div>
                <p className="mt-1 text-xs text-slate-500">{run.employeeCount} employees · Net {fmt(run.totalNetSalary)}</p>
                {run.status !== 'Locked' && (
                  <div className="mt-2 flex gap-1">
                    <button type="button" onClick={e => { e.stopPropagation(); processRun(run.id); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                      <Play className="h-3 w-3" /> Process
                    </button>
                    {run.status === 'Processed' && (
                      <button type="button" onClick={e => { e.stopPropagation(); onSelectRun(run, 'approvals'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <CheckCircle2 className="h-3 w-3" /> Approve
                      </button>
                    )}
                    {run.status === 'Approved' && (
                      <button type="button" onClick={e => { e.stopPropagation(); lockRun(run.id); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <Lock className="h-3 w-3" /> Lock
                      </button>
                    )}
                    <button type="button" onClick={e => { e.stopPropagation(); onSelectRun(run, 'validation'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                      <AlertTriangle className="h-3 w-3" /> Validate
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>

        <div className="surface overflow-hidden lg:col-span-2">
          {!selectedRun ? (
            <div className="flex flex-col items-center justify-center py-24 text-center">
              <FileText className="mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" />
              <p className="text-sm text-slate-400">Select a payroll run to view salary slips</p>
            </div>
          ) : (
            <>
              <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Slips — {MONTHS[selectedRun.month - 1]} {selectedRun.year}</p>
                <div className="flex items-center gap-3 text-xs text-slate-500">
                  <span>Gross: <span className="font-semibold text-slate-900 dark:text-white">{fmt(selectedRun.totalGrossSalary)}</span></span>
                  <span>Net: <span className="font-bold text-emerald-600 dark:text-emerald-400">{fmt(selectedRun.totalNetSalary)}</span></span>
                </div>
              </div>
              {slipsLoading ? <div className="flex justify-center py-12"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div> : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[560px] text-sm">
                    <thead>
                      <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                        {['Employee', 'Dept', 'Basic', 'Gross', 'Loans', 'Deductions', 'Net', 'YTD Gross', 'YTD Net'].map(h => (
                          <th key={h} className="px-3 py-3 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                      {slips.length === 0 && <tr><td colSpan={9} className="py-10 text-center text-sm text-slate-400">No slips. Click Process to generate.</td></tr>}
                      {slips.map(s => (
                        <tr key={s.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                          <td className="px-3 py-2.5">
                            <p className="font-medium text-slate-900 dark:text-white">{s.employeeName}</p>
                            <p className="text-xs text-slate-400">{s.employeeCode}</p>
                          </td>
                          <td className="px-3 py-2.5 text-xs text-slate-500">{s.department || '—'}</td>
                          <td className="px-3 py-2.5 text-right text-slate-700 dark:text-slate-300">{fmt(s.basicSalary)}</td>
                          <td className="px-3 py-2.5 text-right font-semibold text-slate-900 dark:text-white">{fmt(s.grossSalary)}</td>
                          <td className="px-3 py-2.5 text-right text-amber-600 dark:text-amber-400">{s.loanDeductions > 0 ? `(${fmt(s.loanDeductions)})` : '—'}</td>
                          <td className="px-3 py-2.5 text-right text-rose-500">
                            ({fmt(s.deductions)})
                            {s.employeeStatutoryTotal > 0 && (
                              <p className="text-[10px] text-rose-400 leading-tight">GOSI {fmt(s.employeeStatutoryTotal)}</p>
                            )}
                          </td>
                          <td className="px-3 py-2.5 text-right font-bold text-emerald-600 dark:text-emerald-400">{fmt(s.netSalary)}</td>
                          <td className="px-3 py-2.5 text-right text-xs text-slate-500">{fmt(s.ytdGross)}</td>
                          <td className="px-3 py-2.5 text-right text-xs font-semibold text-slate-700 dark:text-slate-300">{fmt(s.ytdNet)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </div>
      </div>

      {showCreate && (
        <Modal title="New Payroll Run" onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
            <div className="grid grid-cols-2 gap-3">
              <Field label="Year" info="The calendar year this payroll run covers, e.g. 2026." infoKey="payroll.run_year">
                <input type="number" aria-label="Year" className={inp} value={createYear} onChange={e => setCreateYear(Number(e.target.value))} min={2020} max={2035} />
              </Field>
              <Field label="Month" info="The salary month to process. One run per month — duplicates are rejected." infoKey="payroll.run_month">
                <select aria-label="Month" className={sel} value={createMonth} onChange={e => setCreateMonth(Number(e.target.value))}>
                  {MONTHS.map((m, i) => <option key={m} value={i + 1}>{m}</option>)}
                </select>
              </Field>
            </div>
            {companies.length > 0 && (
              <Field label="Company" info="The company whose country pack (GOSI/GPSSA/GRSIA) will be applied. Required for statutory deductions." infoKey="payroll.run_company">
                <select aria-label="Company" className={sel} value={createCompanyId} onChange={e => setCreateCompanyId(e.target.value)}>
                  {companies.length > 1 && <option value="">— select company —</option>}
                  {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
            )}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={createRun} disabled={saving}>{saving ? 'Creating…' : 'Create Run'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Validation Tab ──────────────────────────────────────────────────────────────

function ValidationTab({ selectedRunId }: { selectedRunId?: string }) {
  const [runId, setRunId] = useState(selectedRunId ?? '');
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [results, setResults] = useState<PayrollValidationResult[]>([]);
  const [loading, setLoading] = useState(false);

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);

  const validate = async () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.validateRun(runId).then(r => {
      setResults(Array.isArray(r) ? r : (r as { warnings?: PayrollValidationResult[] }).warnings ?? []);
    }).catch(() => {}).finally(() => setLoading(false));
  };

  useEffect(() => { if (selectedRunId) { setRunId(selectedRunId); } }, [selectedRunId]);

  const warnings = results.filter(r => r.severity === 'Warning');
  const errors = results.filter(r => r.severity === 'Error');

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run" className={`${sel} flex-1 max-w-xs`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select a payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        <button type="button" className={btn.primary} onClick={validate} disabled={!runId || loading}>
          {loading ? 'Validating…' : 'Run Validation'}
        </button>
      </div>

      {results.length > 0 && (
        <div className="grid grid-cols-3 gap-4">
          <KpiCard label="Total Issues" value={results.length} icon={AlertTriangle} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          <KpiCard label="Warnings" value={warnings.length} icon={AlertTriangle} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          <KpiCard label="Errors" value={errors.length} icon={AlertTriangle} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
        </div>
      )}

      {results.length === 0 && !loading ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <CheckCircle2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
            {runId ? 'No validation results — click "Run Validation" to check' : 'Select a run and click validate'}
          </p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {results.map(r => (
            <div key={r.id} className="flex items-start gap-3 px-5 py-4">
              <div className={`mt-0.5 h-2 w-2 shrink-0 rounded-full ${r.severity === 'Error' ? 'bg-rose-500' : r.severity === 'Warning' ? 'bg-amber-500' : 'bg-sky-400'}`} />
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <StatusBadge status={r.severity} />
                  <span className="font-mono text-xs text-slate-500">{r.code}</span>
                  {r.employeeId && <span className="text-xs text-slate-400">Emp #{r.employeeId}</span>}
                </div>
                <p className="mt-1 text-sm text-slate-700 dark:text-slate-300">{r.message}</p>
              </div>
              {r.isResolved && <span className="ml-auto shrink-0 rounded bg-emerald-50 px-1.5 py-0.5 text-[10px] font-medium text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">Resolved</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Approvals Tab ───────────────────────────────────────────────────────────────

function ApprovalsTab({ selectedRunId, isAdmin, isFinance, isHROrPayroll }: {
  selectedRunId?: string;
  isAdmin: boolean;
  isFinance: boolean;
  isHROrPayroll: boolean;
}) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState(selectedRunId ?? '');
  const [approvals, setApprovals] = useState<PayrollApproval[]>([]);
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const refreshRuns = () =>
    payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {});

  useEffect(() => { refreshRuns(); }, []);
  useEffect(() => { if (selectedRunId) setRunId(selectedRunId); }, [selectedRunId]);
  useEffect(() => {
    if (!runId) return;
    payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
  }, [runId]);

  const selectedRun = runs.find(r => r.id === runId);

  const handleApprove = async () => {
    if (!runId) return;
    setSaving(true); setError('');
    try {
      await payrollApi.approveRun(runId, notes);
      await refreshRuns();
      setRuns(r => r.map(x => x.id === runId ? { ...x, status: selectedRun?.status === 'Processed' && (isHROrPayroll && !isFinance && !isAdmin) ? 'PendingFinanceReview' : 'Approved' } : x));
      payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
      setNotes('');
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Action failed.');
    } finally { setSaving(false); }
  };

  const handleSendBack = async () => {
    if (!runId) return;
    setSaving(true); setError('');
    try {
      await payrollApi.sendBackRun(runId, notes);
      await refreshRuns();
      payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
      setNotes('');
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Action failed.');
    } finally { setSaving(false); }
  };

  const canApproveStep1 = (isHROrPayroll || isAdmin) && selectedRun?.status === 'Processed';
  const canApproveStep2 = (isFinance || isAdmin) && selectedRun?.status === 'PendingFinanceReview';
  const canFinanceApproveDirectly = (isFinance || isAdmin) && selectedRun?.status === 'Processed';
  const canSendBack = (isFinance || isAdmin) && selectedRun?.status === 'PendingFinanceReview';
  const canAct = canApproveStep1 || canApproveStep2 || canFinanceApproveDirectly;

  return (
    <div className="space-y-4">
      {/* Approval chain diagram */}
      <div className="surface p-4">
        <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">Approval Workflow</p>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          {[
            { label: 'Process', badge: 'Payroll Officer', color: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300' },
            { label: '→', badge: null, color: '' },
            { label: 'HR / Payroll Review', badge: 'Payroll Manager · HR Manager', color: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400' },
            { label: '→', badge: null, color: '' },
            { label: 'Finance Approval', badge: 'Finance Controller · Finance Approver', color: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400' },
            { label: '→', badge: null, color: '' },
            { label: 'Approved', badge: null, color: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400' },
          ].map((step, i) => step.badge === null && step.label === '→' ? (
            <span key={i} className="text-slate-300 dark:text-slate-600">→</span>
          ) : (
            <div key={i} className={`rounded-lg px-2 py-1 text-xs font-medium ${step.color}`}>
              {step.label}
              {step.badge && <span className="ml-1 opacity-60">({step.badge})</span>}
            </div>
          ))}
        </div>
      </div>

      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run to approve" className={`${sel} flex-1 max-w-xs`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
      </div>

      {selectedRun && (
        <div className="surface p-5 space-y-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">Payroll Run — {MONTHS[selectedRun.month - 1]} {selectedRun.year}</p>
              <p className="text-xs text-slate-400">{selectedRun.employeeCount} employees · Gross {fmtAmt(selectedRun.totalGrossSalary)} · Net {fmtAmt(selectedRun.totalNetSalary)}</p>
            </div>
            <StatusBadge status={selectedRun.status} />
          </div>

          {canApproveStep1 && !canFinanceApproveDirectly && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-700 dark:border-amber-400/20 dark:bg-amber-500/10 dark:text-amber-400">
              Your approval will advance this run to <strong>Pending Finance Review</strong> for final Finance Controller sign-off.
            </div>
          )}

          {canAct && (
            <div className="space-y-3">
              <Field label="Notes / Comments">
                <textarea aria-label="Approval notes" className={inp} rows={2} value={notes} onChange={e => setNotes(e.target.value)} placeholder="Optional comments for the record…" />
              </Field>
              {error && <p className="text-xs text-rose-500">{error}</p>}
              <div className="flex flex-wrap gap-2">
                <button type="button" className={btn.primary} onClick={handleApprove} disabled={saving}>
                  <CheckCircle2 className="h-4 w-4" />
                  {saving ? 'Saving…' : canFinanceApproveDirectly || canApproveStep2 ? 'Approve — Final' : 'Approve → Send to Finance'}
                </button>
                {canSendBack && (
                  <button type="button" className={btn.danger} onClick={handleSendBack} disabled={saving}>
                    <RotateCcw className="h-4 w-4" /> Send Back to Payroll
                  </button>
                )}
              </div>
            </div>
          )}

          {!canAct && (
            <p className="text-sm text-slate-400">
              {selectedRun.status === 'Approved' ? 'Payroll run has been approved and is ready to lock.' :
               selectedRun.status === 'Locked' ? 'Run is locked and finalised.' :
               selectedRun.status === 'PendingFinanceReview' ? 'Awaiting Finance Controller approval.' :
               'No action available for this run at its current stage.'}
            </p>
          )}
        </div>
      )}

      {approvals.length > 0 && (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          <div className="px-5 py-3"><p className="text-sm font-semibold text-slate-800 dark:text-white">Approval Chain History</p></div>
          {approvals.map(a => (
            <div key={a.id} className="flex items-start gap-3 px-5 py-4">
              {a.decision === 'SentBack' ? <RotateCcw className="mt-0.5 h-4 w-4 shrink-0 text-rose-500" /> : <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />}
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <StatusBadge status={a.decision} />
                  <span className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-500 dark:bg-white/10 dark:text-slate-400">{a.approvalLevel}</span>
                </div>
                {a.notes && <p className="mt-1 text-xs text-slate-500">{a.notes}</p>}
                <p className="mt-1 text-xs text-slate-400">{fmtDate(a.decidedAtUtc)}</p>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Payslips Tab ────────────────────────────────────────────────────────────────

function PayslipsTab() {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState('');
  const [payslips, setPayslips] = useState<Payslip[]>([]);
  const [loading, setLoading] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);

  const loadPayslips = () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.listPayslips(runId, { pageSize: 200 }).then(r => setPayslips(r.items)).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { if (runId) loadPayslips(); }, [runId]);

  const generate = async () => {
    if (!runId) return;
    setGenerating(true);
    await payrollApi.generatePayslips(runId).catch(() => {});
    loadPayslips();
    setGenerating(false);
  };

  const published = payslips.filter(p => p.isPublishedToEss).length;

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run" className={`${sel} flex-1 max-w-xs`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select a payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        {runId && (
          <button type="button" className={btn.primary} onClick={generate} disabled={generating}>
            {generating ? 'Generating…' : 'Generate Payslips'}
          </button>
        )}
      </div>

      {payslips.length > 0 && (
        <div className="grid grid-cols-3 gap-4">
          <KpiCard label="Total Payslips" value={payslips.length} icon={FileText} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label="Published to ESS" value={published} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label="Pending Publish" value={payslips.length - published} icon={AlertTriangle} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        </div>
      )}

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : payslips.length === 0 && runId ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <FileText className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No payslips yet — click Generate to create payslips for this run</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'Payslip #', 'Language', 'ESS Published', 'Published At', 'Generated', ''].map(h => (
                  <th key={h} className="px-4 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {payslips.map(p => (
                <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">Emp #{p.employeeId}</td>
                  <td className="px-4 py-2 font-mono text-xs text-slate-500">{p.payslipNumber}</td>
                  <td className="px-4 py-2 text-slate-500">{p.language.toUpperCase()}</td>
                  <td className="px-4 py-2">
                    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${p.isPublishedToEss ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10'}`}>
                      {p.isPublishedToEss ? 'Published' : 'Not published'}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-xs text-slate-400">{p.publishedAtUtc ? fmtDate(p.publishedAtUtc) : '—'}</td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fmtDate(p.createdAtUtc)}</td>
                  <td className="px-4 py-2">
                    <button
                      type="button"
                      title="Download payslip PDF"
                      disabled={downloadingId === p.id}
                      onClick={() => {
                        setDownloadingId(p.id);
                        payrollApi.downloadSlipPdf(p.id).finally(() => setDownloadingId(null));
                      }}
                      className="flex items-center gap-1 text-xs text-sapphire hover:text-sapphire/80 disabled:opacity-40"
                    >
                      <Download className="h-3.5 w-3.5" />
                      {downloadingId === p.id ? 'Downloading…' : 'PDF'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Bank / WPS Files Tab ────────────────────────────────────────────────────────

function BankWpsTab() {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState('');
  const [batches, setBatches] = useState<PayrollPaymentBatch[]>([]);
  const [selectedBatch, setSelectedBatch] = useState<PayrollPaymentBatch | null>(null);
  const [records, setRecords] = useState<PayrollPaymentRecord[]>([]);
  const [creating, setCreating] = useState(false);
  const [paymentMethod, setPaymentMethod] = useState('WPS');

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);

  useEffect(() => {
    if (!runId) { setBatches([]); return; }
    payrollApi.listPaymentBatches(runId).then(setBatches).catch(() => {});
  }, [runId]);

  const openBatch = (batch: PayrollPaymentBatch) => {
    setSelectedBatch(batch);
    payrollApi.paymentRecords(batch.id).then(setRecords).catch(() => {});
  };

  const createBatch = async () => {
    if (!runId) return;
    setCreating(true);
    const batch = await payrollApi.createPaymentBatch(runId, paymentMethod).catch(() => null);
    if (batch) { setBatches(b => [batch, ...b]); setSelectedBatch(batch); payrollApi.paymentRecords(batch.id).then(setRecords).catch(() => {}); }
    setCreating(false);
  };

  const generateWps = async (batchId: string) => {
    await payrollApi.generateWpsFile(batchId).catch(() => alert('WPS generation failed.'));
    payrollApi.listPaymentBatches(runId).then(setBatches).catch(() => {});
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <select aria-label="Select payroll run" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        {runId && (
          <>
            <select aria-label="Payment method" className={`${sel} w-40`} value={paymentMethod} onChange={e => setPaymentMethod(e.target.value)}>
              {['WPS', 'BankTransfer', 'Cash'].map(m => <option key={m}>{m}</option>)}
            </select>
            <button type="button" className={btn.primary} onClick={createBatch} disabled={creating}>
              {creating ? 'Creating…' : 'Create Payment Batch'}
            </button>
          </>
        )}
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
            <p className="text-sm font-semibold text-slate-800 dark:text-white">Payment Batches</p>
          </div>
          {batches.length === 0 ? <p className="p-5 text-sm text-slate-400">No batches yet.</p> : (
            <div className="divide-y divide-slate-100 dark:divide-white/5">
              {batches.map(b => (
                <div key={b.id} role="button" tabIndex={0} onClick={() => openBatch(b)}
                  onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openBatch(b); } }}
                  className={`w-full cursor-pointer px-4 py-3 text-left hover:bg-slate-50 dark:hover:bg-white/5 ${selectedBatch?.id === b.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}>
                  <div className="flex items-center justify-between">
                    <p className="text-sm font-medium text-slate-800 dark:text-white">{b.batchNumber}</p>
                    <StatusBadge status={b.status} />
                  </div>
                  <p className="mt-1 text-xs text-slate-400">{b.paymentMethod} · {fmtAmt(b.totalAmount, b.currency)}</p>
                  {b.status !== 'FileGenerated' && (
                    <button type="button" onClick={e => { e.stopPropagation(); generateWps(b.id); }} className={`mt-1.5 ${btn.sm} h-6 px-2 text-xs`}>
                      Generate WPS/SIF
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="surface overflow-hidden lg:col-span-2">
          {!selectedBatch ? (
            <div className="flex flex-col items-center justify-center py-20 text-center">
              <Landmark className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm text-slate-400">Select a batch to view payment records</p>
            </div>
          ) : (
            <>
              <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
                <p className="text-sm font-semibold text-slate-800 dark:text-white">Records — {selectedBatch.batchNumber}</p>
              </div>
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {['Employee', 'Amount', 'IBAN', 'WPS Reference', 'Status'].map(h => (
                      <th key={h} className="px-4 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {records.length === 0 && <tr><td colSpan={5} className="py-8 text-center text-sm text-slate-400">No records.</td></tr>}
                  {records.map(r => (
                    <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">Emp #{r.employeeId}</td>
                      <td className="px-4 py-2 font-semibold text-slate-700 dark:text-slate-300">{r.amount.toLocaleString('en-US', { minimumFractionDigits: 2 })}</td>
                      <td className="px-4 py-2 font-mono text-xs text-slate-500">{r.iban || <span className="text-rose-500">Missing IBAN</span>}</td>
                      <td className="px-4 py-2 text-xs text-slate-400">{r.wpsReference}</td>
                      <td className="px-4 py-2"><StatusBadge status={r.status} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Payment Tracking Tab ────────────────────────────────────────────────────────

function PaymentTrackingTab() {
  const [batches, setBatches] = useState<PayrollPaymentBatch[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    payrollApi.listPaymentBatches().then(setBatches).catch(() => {}).finally(() => setLoading(false));
  }, []);

  const total = batches.reduce((sum, b) => sum + b.totalAmount, 0);
  const fileGenerated = batches.filter(b => b.status === 'FileGenerated').length;

  return (
    <div className="space-y-4">
      {batches.length > 0 && (
        <div className="grid grid-cols-3 gap-4">
          <KpiCard label="Total Batches" value={batches.length} icon={WalletCards} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label="WPS Files Generated" value={fileGenerated} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label="Total Amount" value={fmtAmt(total)} icon={TrendingUp} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
        </div>
      )}
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : batches.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <TrendingUp className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No payment batches yet</p>
          <p className="text-xs text-slate-400">Create payment batches from the Bank/WPS Files tab after processing a payroll run.</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Batch', 'Method', 'Total Amount', 'Currency', 'Status', 'Created'].map(h => (
                  <th key={h} className="px-4 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {batches.map(b => (
                <tr key={b.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-mono text-xs font-medium text-slate-900 dark:text-white">{b.batchNumber}</td>
                  <td className="px-4 py-2 text-slate-500">{b.paymentMethod}</td>
                  <td className="px-4 py-2 font-semibold text-slate-700 dark:text-slate-300">{b.totalAmount.toLocaleString('en-US', { minimumFractionDigits: 2 })}</td>
                  <td className="px-4 py-2 text-slate-500">{b.currency}</td>
                  <td className="px-4 py-2"><StatusBadge status={b.status} /></td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fmtDate(b.createdAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Reports Tab ─────────────────────────────────────────────────────────────────

function ReportsTab() {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState('');
  const [slips, setSlips] = useState<PayrollSlip[]>([]);
  const [summary, setSummary] = useState<PayrollSummary | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {});
    payrollApi.reportSummary().then(setSummary).catch(() => {});
  }, []);

  const loadRegister = () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.reportRegister(runId).then(setSlips).catch(() => {}).finally(() => setLoading(false));
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2 });

  return (
    <div className="space-y-6">
      {summary && (
        <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <KpiCard label="Total Runs" value={summary.totalRuns} icon={FileText} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label="Locked Runs" value={summary.lockedRuns} icon={Lock} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label="Gross YTD" value={fmtAmt(summary.totalGrossYtd)} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
          <KpiCard label="Net YTD" value={fmtAmt(summary.totalNetYtd)} icon={TrendingUp} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        </div>
      )}

      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Payroll Register</p>
        <div className="flex items-center gap-3">
          <select aria-label="Select payroll run for register" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
            <option value="">Select a payroll run…</option>
            {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
          </select>
          <button type="button" className={btn.primary} onClick={loadRegister} disabled={!runId || loading}>
            {loading ? 'Loading…' : 'Load Register'}
          </button>
        </div>

        {slips.length > 0 && (
          <>
            <div className="mt-4 rounded-lg bg-slate-50 px-4 py-3 dark:bg-white/5">
              <p className="text-sm text-slate-600 dark:text-slate-400">
                {slips.length} employees · Gross {fmtAmt(slips.reduce((s, x) => s + x.grossSalary, 0))} · Net {fmtAmt(slips.reduce((s, x) => s + x.netSalary, 0))}
              </p>
            </div>
            <div className="mt-4 overflow-x-auto">
              <table className="w-full min-w-[640px] text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {['Code', 'Employee', 'Department', 'Basic', 'Housing', 'Transport', 'Other', 'Gross', 'Loans', 'Deductions', 'Net', 'YTD Gross', 'YTD Net'].map(h => (
                      <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {slips.map(s => (
                    <tr key={s.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-3 py-2 font-mono text-xs text-slate-500">{s.employeeCode}</td>
                      <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{s.employeeName}</td>
                      <td className="px-3 py-2 text-xs text-slate-500">{s.department || '—'}</td>
                      <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{fmt(s.basicSalary)}</td>
                      <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{fmt(s.housingAllowance)}</td>
                      <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{fmt(s.transportAllowance)}</td>
                      <td className="px-3 py-2 text-right text-slate-700 dark:text-slate-300">{fmt(s.otherAllowances)}</td>
                      <td className="px-3 py-2 text-right font-semibold text-slate-900 dark:text-white">{fmt(s.grossSalary)}</td>
                      <td className="px-3 py-2 text-right text-amber-600 dark:text-amber-400">{s.loanDeductions > 0 ? `(${fmt(s.loanDeductions)})` : '—'}</td>
                      <td className="px-3 py-2 text-right text-rose-500">
                        ({fmt(s.deductions)})
                        {s.employeeStatutoryTotal > 0 && (
                          <p className="text-[10px] text-rose-400 leading-tight">GOSI {fmt(s.employeeStatutoryTotal)}</p>
                        )}
                      </td>
                      <td className="px-3 py-2 text-right font-bold text-emerald-600 dark:text-emerald-400">{fmt(s.netSalary)}</td>
                      <td className="px-3 py-2 text-right text-xs text-slate-500">{fmt(s.ytdGross)}</td>
                      <td className="px-3 py-2 text-right text-xs font-semibold text-slate-700 dark:text-slate-300">{fmt(s.ytdNet)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

// ── AI Validation Tab ───────────────────────────────────────────────────────────

function AIValidationTab() {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState('');
  const [result, setResult] = useState<{ advisoryOnly: boolean; warnings: PayrollValidationResult[]; summary: string } | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);

  const runAI = async () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.aiValidation(runId).then(r => setResult(r as typeof result)).catch(() => {}).finally(() => setLoading(false));
  };

  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <div className="flex items-start gap-3">
          <Bot className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
          <div>
            <p className="text-sm font-semibold text-slate-800 dark:text-white">AI Payroll Validation — Advisory Only</p>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              AI checks flag anomalies like salary variance, unusual overtime, missing IBANs, and duplicate entries.
              AI does not approve payroll or change salaries automatically.
            </p>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run for AI validation" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select a payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        <button type="button" className={btn.primary} onClick={runAI} disabled={!runId || loading}>
          <Zap className="h-4 w-4" /> {loading ? 'Analyzing…' : 'Run AI Checks'}
        </button>
      </div>

      {result && (
        <>
          <div className="surface p-4">
            <div className="flex items-center gap-2">
              <Shield className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              <p className="text-sm font-semibold text-slate-800 dark:text-white">{result.summary}</p>
            </div>
            <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">Advisory: AI findings are recommendations only — HR/Finance must review and act.</p>
          </div>

          {result.warnings.length === 0 ? (
            <div className="surface flex flex-col items-center py-12 text-center">
              <CheckCircle2 className="mb-3 h-8 w-8 text-emerald-500" />
              <p className="text-sm font-medium text-slate-700 dark:text-slate-300">No AI anomalies detected for this payroll run.</p>
            </div>
          ) : (
            <div className="space-y-3">
              {result.warnings.map(w => (
                <div key={w.id} className="surface p-4">
                  <div className="flex items-start gap-3">
                    <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${w.severity === 'Error' ? 'text-rose-500' : 'text-amber-500'}`} />
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <StatusBadge status={w.severity} />
                        <span className="font-mono text-xs text-slate-500">{w.code}</span>
                        {w.employeeId && <span className="text-xs text-slate-400">Emp #{w.employeeId}</span>}
                      </div>
                      <p className="mt-1 text-sm text-slate-700 dark:text-slate-300">{w.message}</p>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ── EOSB / Gratuity Tab ──────────────────────────────────────────────────────────

function EOSBTab() {
  const [employeeId, setEmployeeId] = useState('');
  const [asOfDate, setAsOfDate] = useState(new Date().toISOString().substring(0, 10));
  const [result, setResult] = useState<Record<string, unknown> | null>(null);
  const [loading, setLoading] = useState(false);
  const [history, setHistory] = useState<unknown[]>([]);
  const [error, setError] = useState('');

  const calculate = async () => {
    if (!employeeId) return;
    setLoading(true); setError(''); setResult(null);
    try {
      const res = await payrollApi.calculateEosb(Number(employeeId), asOfDate);
      setResult(res as Record<string, unknown>);
      payrollApi.listEosb(Number(employeeId)).then(h => setHistory(h as unknown[])).catch(() => {});
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Calculation failed.';
      setError(msg);
    } finally { setLoading(false); }
  };

  return (
    <div className="space-y-5 max-w-2xl">
      <div className="surface p-4 space-y-3">
        <p className="text-sm font-semibold text-slate-800 dark:text-white">EOSB / Gratuity Calculator</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Calculates End-of-Service Benefit per UAE Labour Law (21 days/year for first 5 years, 30 days/year thereafter).
          Rates are configurable in Setup → GCC Settings.
        </p>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Employee ID *</label>
            <input type="number" className={inp} placeholder="e.g. 1" value={employeeId} onChange={e => setEmployeeId(e.target.value)} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">As of Date</label>
            <input type="date" className={inp} aria-label="As of date" value={asOfDate} onChange={e => setAsOfDate(e.target.value)} />
          </div>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <button type="button" className={btn.primary} onClick={calculate} disabled={!employeeId || loading}>
          {loading ? 'Calculating…' : 'Calculate EOSB'}
        </button>
      </div>

      {result && (
        <div className="surface p-5 space-y-3">
          <p className="font-semibold text-slate-900 dark:text-white">{result.employeeName as string}</p>
          <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
            <div className="text-slate-500">Total Service</div><div className="font-medium">{(result.totalYears as number).toFixed(2)} years</div>
            <div className="text-slate-500">Eligible Salary</div><div className="font-medium">{result.currency as string} {(result.eligibleSalary as number).toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">Daily Rate</div><div className="font-medium">{result.currency as string} {(result.dailySalary as number).toFixed(4)}</div>
            <div className="text-slate-500">Rate (1–5 yrs)</div><div className="font-medium">{result.rate1To5Years as number} days/year</div>
            <div className="text-slate-500">Rate (5+ yrs)</div><div className="font-medium">{result.rateAbove5Years as number} days/year</div>
          </div>
          <div className="rounded-xl bg-sapphire/10 p-4 dark:bg-cyanAccent/10">
            <p className="text-xs text-slate-500 dark:text-slate-400">Calculated EOSB</p>
            <p className="text-2xl font-bold text-sapphire dark:text-cyanAccent">{result.currency as string} {(result.eosbAmount as number).toLocaleString('en-US', { minimumFractionDigits: 2 })}</p>
            <p className="mt-1 text-xs text-slate-500">{result.message as string}</p>
          </div>
          <p className="text-xs text-amber-600 dark:text-amber-400">Advisory only. Consult legal/HR before processing EOSB payment.</p>
        </div>
      )}

      {history.length > 0 && (
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-white/10">
            <p className="text-sm font-semibold text-slate-900 dark:text-white">Previous Calculations</p>
          </div>
          <table className="min-w-full text-sm">
            <thead><tr className="border-b border-slate-100 dark:border-white/10">
              {['Date', 'Eligible Salary', 'EOSB Amount', 'Status'].map(h => <th key={h} className="px-4 py-2.5 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}
            </tr></thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {(history as Array<Record<string, unknown>>).map((h, i) => (
                <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{h.calculationDate as string}</td>
                  <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">AED {(h.eligibleSalary as number).toLocaleString()}</td>
                  <td className="px-4 py-2.5 font-medium text-sapphire dark:text-cyanAccent">AED {(h.calculatedAmount as number).toLocaleString()}</td>
                  <td className="px-4 py-2.5"><StatusBadge status={h.status as string} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── GL Journal Tab ──────────────────────────────────────────────────────────────

function GlJournalTab({ selectedRunId }: { selectedRunId?: string }) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState(selectedRunId ?? '');
  const [journal, setJournal] = useState<PayrollGLJournal | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);
  useEffect(() => { if (selectedRunId) setRunId(selectedRunId); }, [selectedRunId]);

  const load = async () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.glJournal(runId).then(j => setJournal(j)).catch(() => {}).finally(() => setLoading(false));
  };

  const debits  = journal?.entries.filter(e => e.entryType === 'DR') ?? [];
  const credits = journal?.entries.filter(e => e.entryType === 'CR') ?? [];

  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Double-entry GL journal generated from processed payroll earnings and deductions. Use this to post payroll to your accounting system.
        </p>
      </div>
      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run for GL journal" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        <button type="button" className={btn.primary} onClick={load} disabled={!runId || loading}>
          <BookOpen className="h-4 w-4" /> {loading ? 'Loading…' : 'Generate Journal'}
        </button>
      </div>

      {journal && (
        <div className="space-y-4">
          <div className="grid grid-cols-3 gap-4">
            <div className="surface p-4">
              <p className="text-xs text-slate-400">Period</p>
              <p className="text-lg font-bold text-slate-900 dark:text-white">{journal.period}</p>
            </div>
            <div className="surface p-4">
              <p className="text-xs text-slate-400">Total Debits</p>
              <p className="text-lg font-bold text-sapphire dark:text-cyanAccent">{fmtAmt(journal.totalDebits)}</p>
            </div>
            <div className="surface p-4">
              <p className="text-xs text-slate-400">Total Credits</p>
              <p className="text-lg font-bold text-sapphire dark:text-cyanAccent">{fmtAmt(journal.totalCredits)}</p>
            </div>
          </div>

          {journal.isBalanced ? (
            <div className="flex items-center gap-2 rounded-lg bg-emerald-50 px-4 py-2.5 dark:bg-emerald-500/10">
              <CheckCircle2 className="h-4 w-4 text-emerald-500" />
              <p className="text-sm font-medium text-emerald-700 dark:text-emerald-400">Journal is balanced — Debits = Credits</p>
            </div>
          ) : (
            <div className="flex items-center gap-2 rounded-lg bg-rose-50 px-4 py-2.5 dark:bg-rose-500/10">
              <AlertTriangle className="h-4 w-4 text-rose-500" />
              <p className="text-sm font-medium text-rose-700 dark:text-rose-400">Journal is out of balance — investigate before posting</p>
            </div>
          )}

          <div className="grid gap-4 lg:grid-cols-2">
            {[{ title: 'Debit Entries (DR)', entries: debits, color: 'text-sapphire dark:text-cyanAccent' }, { title: 'Credit Entries (CR)', entries: credits, color: 'text-emerald-600 dark:text-emerald-400' }].map(side => (
              <div key={side.title} className="surface overflow-hidden">
                <div className="border-b border-slate-100 px-4 py-2.5 dark:border-white/5">
                  <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{side.title}</p>
                </div>
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/5">
                      {['GL Account', 'Description', 'Amount'].map(h => <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>)}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {side.entries.map((e, i) => (
                      <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                        <td className="px-3 py-2 font-mono text-xs text-slate-500">{e.glAccount}</td>
                        <td className="px-3 py-2 text-slate-700 dark:text-slate-300">{e.glAccountName}</td>
                        <td className={`px-3 py-2 text-right font-semibold tabular-nums ${side.color}`}>{fmtAmt(e.amount)}</td>
                      </tr>
                    ))}
                    <tr className="border-t-2 border-slate-200 dark:border-white/10">
                      <td colSpan={2} className="px-3 py-2 text-xs font-bold text-slate-600 dark:text-slate-300">Total</td>
                      <td className={`px-3 py-2 text-right text-sm font-extrabold tabular-nums ${side.color}`}>{fmtAmt(side.entries.reduce((s, e) => s + e.amount, 0))}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ── Reconciliation Tab ──────────────────────────────────────────────────────────

function ReconciliationTab({ selectedRunId }: { selectedRunId?: string }) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useState(selectedRunId ?? '');
  const [report, setReport] = useState<PayrollReconciliation | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { payrollApi.listRuns({ pageSize: 50 }).then(r => setRuns(r.items)).catch(() => {}); }, []);
  useEffect(() => { if (selectedRunId) setRunId(selectedRunId); }, [selectedRunId]);

  const load = async () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.reconciliation(runId).then(r => setReport(r)).catch(() => {}).finally(() => setLoading(false));
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2 });

  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Month-over-month payroll reconciliation. Compares current run headcount and compensation vs the prior month. Variances above 5% are flagged for review.
        </p>
      </div>

      <div className="flex items-center gap-3">
        <select aria-label="Select payroll run for reconciliation" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">Select payroll run…</option>
          {runs.map(r => <option key={r.id} value={r.id}>{MONTHS[r.month - 1]} {r.year} — {r.status}</option>)}
        </select>
        <button type="button" className={btn.primary} onClick={load} disabled={!runId || loading}>
          <RefreshCw className="h-4 w-4" /> {loading ? 'Loading…' : 'Reconcile'}
        </button>
      </div>

      {report && (
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <KpiCard label="Headcount" value={`${report.priorHeadcount} → ${report.currentHeadcount}`} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
            <KpiCard label="Joiners" value={report.joinerCount} icon={Plus} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
            <KpiCard label="Leavers" value={report.leaverCount} icon={X} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
            <KpiCard label="Flagged Variances" value={report.flaggedVariances} icon={AlertTriangle} color={report.flaggedVariances > 0 ? 'bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400' : 'bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400'} />
          </div>

          <div className="surface overflow-hidden">
            <div className="grid grid-cols-2 divide-x divide-slate-100 dark:divide-white/5">
              {[{ label: 'Gross', prior: report.priorTotalGross, current: report.currentTotalGross }, { label: 'Net', prior: report.priorTotalNet, current: report.currentTotalNet }].map(m => (
                <div key={m.label} className="p-4">
                  <p className="text-xs text-slate-400">Total {m.label}</p>
                  <p className="text-lg font-bold text-slate-900 dark:text-white">{fmtAmt(m.current)}</p>
                  <p className={`text-xs ${m.current >= m.prior ? 'text-emerald-500' : 'text-rose-500'}`}>
                    {m.current >= m.prior ? '+' : ''}{fmtAmt(m.current - m.prior)} vs {report.priorPeriod ?? 'prior period'}
                  </p>
                </div>
              ))}
            </div>
          </div>

          {report.variances.length > 0 && (
            <div className="surface overflow-hidden">
              <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
                <p className="text-sm font-semibold text-slate-800 dark:text-white">Employee Variance Detail</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[640px] text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                      {['Employee', 'Prior Gross', 'Current Gross', 'Δ Gross', 'Var %', 'Prior Net', 'Current Net', 'Flag'].map(h => (
                        <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {report.variances.map(v => (
                      <tr key={v.employeeId} className={`hover:bg-slate-50 dark:hover:bg-white/[0.03] ${v.isVarianceFlag ? 'bg-amber-50/50 dark:bg-amber-500/5' : ''}`}>
                        <td className="px-3 py-2">
                          <p className="font-medium text-slate-900 dark:text-white">{v.employeeName}</p>
                          <p className="text-xs text-slate-400">{v.employeeCode}</p>
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums text-slate-600 dark:text-slate-300">{fmt(v.priorGross)}</td>
                        <td className="px-3 py-2 text-right tabular-nums font-semibold text-slate-900 dark:text-white">{fmt(v.currentGross)}</td>
                        <td className={`px-3 py-2 text-right tabular-nums ${v.grossDelta >= 0 ? 'text-emerald-600' : 'text-rose-500'}`}>{v.grossDelta >= 0 ? '+' : ''}{fmt(v.grossDelta)}</td>
                        <td className={`px-3 py-2 text-right tabular-nums font-semibold ${Math.abs(v.grossVariancePct) > 5 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-500'}`}>{v.grossVariancePct >= 0 ? '+' : ''}{v.grossVariancePct.toFixed(1)}%</td>
                        <td className="px-3 py-2 text-right tabular-nums text-slate-500">{fmt(v.priorNet)}</td>
                        <td className="px-3 py-2 text-right tabular-nums text-slate-700 dark:text-slate-300">{fmt(v.currentNet)}</td>
                        <td className="px-3 py-2">{v.isVarianceFlag && <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/20 dark:text-amber-400"><AlertTriangle className="h-3 w-3" />Flag</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── Final Settlement Tab ────────────────────────────────────────────────────────

function FinalSettlementTab() {
  const [form, setForm] = useState({ employeeId: '', lastWorkingDay: new Date().toISOString().slice(0, 10), noticePeriodDaysShort: '0' });
  const [result, setResult] = useState<FinalSettlementResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const calculate = async () => {
    if (!form.employeeId || !form.lastWorkingDay) return;
    setLoading(true); setError(''); setResult(null);
    try {
      const res = await payrollApi.finalSettlement(Number(form.employeeId), form.lastWorkingDay, Number(form.noticePeriodDaysShort));
      setResult(res);
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Calculation failed.');
    } finally { setLoading(false); }
  };

  return (
    <div className="space-y-5 max-w-2xl">
      <div className="surface p-4">
        <p className="text-sm font-semibold text-slate-800 dark:text-white">Final Settlement Calculator</p>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          Calculates the total amount payable to a departing employee: pro-rata salary, EOSB/Gratuity, leave encashment, minus notice period shortfall.
        </p>
      </div>

      <div className="surface p-5 space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Employee ID *</label>
            <input type="number" className={inp} placeholder="e.g. 1" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Last Working Day *</label>
            <input type="date" className={inp} aria-label="Last working day" value={form.lastWorkingDay} onChange={e => setForm(f => ({ ...f, lastWorkingDay: e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Notice Period Short (days)</label>
            <input type="number" className={inp} min="0" placeholder="0" value={form.noticePeriodDaysShort} onChange={e => setForm(f => ({ ...f, noticePeriodDaysShort: e.target.value }))} />
            <p className="mt-0.5 text-xs text-slate-400">Days employee served less than the contractual notice period</p>
          </div>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <button type="button" className={btn.primary} onClick={calculate} disabled={!form.employeeId || !form.lastWorkingDay || loading}>
          <Calculator className="h-4 w-4" /> {loading ? 'Calculating…' : 'Calculate Settlement'}
        </button>
      </div>

      {result && (
        <div className="surface p-5 space-y-4">
          <div>
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{result.employeeName}</p>
            <p className="text-xs text-slate-400">Last working day: {result.lastWorkingDay} · Service: {result.totalYears.toFixed(2)} years</p>
          </div>

          <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
            <div className="text-slate-500">Basic Salary</div><div className="font-medium text-right">{result.currency} {result.basicSalary.toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">Gross Salary</div><div className="font-medium text-right">{result.currency} {result.grossSalary.toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">Days Worked in Month</div><div className="font-medium text-right">{result.daysWorkedInMonth} / {result.daysInMonth}</div>
            <div className="text-slate-500">Leave Balance</div><div className="font-medium text-right">{result.leaveBalanceDays.toFixed(2)} days</div>
          </div>

          <div className="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-white/5 dark:border-white/10">
            {result.breakdown.map(b => (
              <div key={b.component} className="flex items-center justify-between px-4 py-3">
                <span className="text-sm text-slate-600 dark:text-slate-300">{b.component}</span>
                <span className={`text-sm font-semibold tabular-nums ${b.amount < 0 ? 'text-rose-500' : 'text-emerald-600 dark:text-emerald-400'}`}>
                  {b.amount < 0 ? '-' : '+'}{result.currency} {Math.abs(b.amount).toLocaleString('en-US', { minimumFractionDigits: 2 })}
                </span>
              </div>
            ))}
          </div>

          <div className="rounded-xl bg-sapphire/10 px-5 py-4 dark:bg-cyanAccent/10">
            <p className="text-xs text-slate-500 dark:text-slate-400">Total Settlement Payable</p>
            <p className="text-2xl font-extrabold text-sapphire dark:text-cyanAccent">{result.currency} {result.totalPayable.toLocaleString('en-US', { minimumFractionDigits: 2 })}</p>
          </div>

          <p className="text-xs text-amber-600 dark:text-amber-400">Advisory only. Consult legal/HR before processing final settlement payment. Values are based on current salary records and leave balance data.</p>
        </div>
      )}
    </div>
  );
}

// ── Main Page ───────────────────────────────────────────────────────────────────

export function PayrollPage() {
  const { user } = useAuth();
  const { currencyCode } = useTenantSettings();
  const isAdmin    = user?.roles.some(r => r === 'Admin') ?? false;
  const isFinance  = user?.roles.some(r => ['Finance Controller', 'Finance Approver'].includes(r)) ?? false;
  const isHROrPayroll = !isAdmin && !isFinance && (user?.roles.some(r => ['HR Manager', 'Payroll Manager', 'Payroll Officer'].includes(r)) ?? false);

  const [activeTab, setActiveTab] = useState<Tab>('dashboard');
  const [contextRunId, setContextRunId] = useState<string | undefined>(undefined);

  const handleSelectRun = (run: PayrollRun, tab: Tab) => {
    setContextRunId(run.id);
    setActiveTab(tab);
  };

  const visibleTabs = TABS.filter(t => {
    // Finance-only tabs
    if (['gl-journal', 'reconciliation'].includes(t.key)) return isAdmin || isFinance || isHROrPayroll;
    return true;
  });

  const renderTab = () => {
    switch (activeTab) {
      case 'dashboard':       return <DashboardTab onNavigate={setActiveTab} />;
      case 'salary-structures': return <SalaryStructuresTab />;
      case 'employee-salary': return <EmployeeSalaryTab />;
      case 'runs':            return <RunsTab onSelectRun={handleSelectRun} />;
      case 'validation':      return <ValidationTab selectedRunId={contextRunId} />;
      case 'approvals':       return <ApprovalsTab selectedRunId={contextRunId} isAdmin={isAdmin} isFinance={isFinance} isHROrPayroll={isHROrPayroll || isAdmin} />;
      case 'payslips':        return <PayslipsTab />;
      case 'bank-wps':        return <BankWpsTab />;
      case 'payment-tracking': return <PaymentTrackingTab />;
      case 'reports':         return <ReportsTab />;
      case 'eosb':            return <EOSBTab />;
      case 'gl-journal':      return <GlJournalTab selectedRunId={contextRunId} />;
      case 'reconciliation':  return <ReconciliationTab selectedRunId={contextRunId} />;
      case 'final-settlement': return <FinalSettlementTab />;
      case 'ai-validation':   return <AIValidationTab />;
    }
  };

  const roleBadge = isAdmin ? { label: 'Admin', cls: 'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-300' }
    : isFinance ? { label: 'Finance', cls: 'bg-violet-100 text-violet-700 dark:bg-violet-500/20 dark:text-violet-300' }
    : isHROrPayroll ? { label: 'HR / Payroll', cls: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-300' }
    : null;

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Payroll Management</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">End-to-end payroll lifecycle — structures, processing, Finance Controller approval, GL journal, WPS files, and final settlement.</p>
        </div>
        {roleBadge && <span className={`inline-flex shrink-0 items-center rounded-full px-2.5 py-1 text-xs font-semibold ${roleBadge.cls}`}>{roleBadge.label}</span>}
      </div>

      <div className="scrollbar-hide flex gap-1 overflow-x-auto rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/5">
        {visibleTabs.map(t => (
          <button
            key={t.key}
            type="button"
            onClick={() => setActiveTab(t.key)}
            className={`flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${activeTab === t.key ? 'bg-white shadow-sm text-sapphire dark:bg-slate-800 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}`}
          >
            <t.icon className="h-3.5 w-3.5 shrink-0" />
            <span className="whitespace-nowrap">{t.label}</span>
          </button>
        ))}
      </div>

      <div>{renderTab()}</div>
    </div>
  );
}
