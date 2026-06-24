'use client';

import { useEffect, useState } from 'react';
import { notifyApiError } from '../api/client';
import {
  FileText, Shield, Globe, AlertTriangle, BarChart3, Lightbulb,
  Plus, CheckCircle, Clock, XCircle, RefreshCw,
} from 'lucide-react';
import {
  complianceContractsApi, complianceVisaApi, compliancePassportsApi,
  complianceWorkPermitsApi, complianceRenewalsApi, complianceReportsApi,
} from '../api/compliance';
import type {
  EmployeeContract, VisaRecord, PassportRecord, WorkPermitRecord,
  ComplianceRenewal, ComplianceDashboard, ExpiryAlert, ComplianceAIInsight,
} from '../api/compliance';

// ── Helpers ───────────────────────────────────────────────────────────────────

function daysUntil(dateStr: string): number {
  return Math.ceil((new Date(dateStr).getTime() - Date.now()) / 86400000);
}

function ExpiryBadge({ daysLeft }: { daysLeft: number }) {
  const cls = daysLeft < 0
    ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400'
    : daysLeft <= 30
    ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400'
    : daysLeft <= 60
    ? 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400'
    : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400';
  const label = daysLeft < 0 ? `${Math.abs(daysLeft)}d overdue` : `${daysLeft}d left`;
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{label}</span>;
}

// ── Dashboard Tab ─────────────────────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
  const [dashboard, setDashboard] = useState<ComplianceDashboard | null>(null);
  const [alerts, setAlerts] = useState<ExpiryAlert[]>([]);

  useEffect(() => {
    complianceReportsApi.dashboard().then(setDashboard).catch(() => {});
    complianceReportsApi.expiryAlerts(90).then(r => setAlerts(r.alerts.slice(0, 8))).catch(() => {});
  }, []);

  const kpis = dashboard ? [
    { label: 'Active Contracts', value: dashboard.activeContracts, icon: FileText, color: 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20', tab: 'contracts' as Tab },
    { label: 'Visas Expiring ≤30d', value: dashboard.visasExpiring30, icon: AlertTriangle, color: dashboard.visasExpiring30 > 0 ? 'bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400' : 'bg-emerald-50 text-emerald-600', tab: 'visa' as Tab },
    { label: 'Passports Expiring ≤90d', value: dashboard.passportsExpiring90, icon: Globe, color: dashboard.passportsExpiring90 > 5 ? 'bg-amber-100 text-amber-600' : 'bg-emerald-50 text-emerald-600', tab: 'passports' as Tab },
    { label: 'Pending Renewals', value: dashboard.pendingRenewals, icon: RefreshCw, color: 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-400', tab: 'renewals' as Tab },
    { label: 'Expired Visas', value: dashboard.expiredVisas, icon: XCircle, color: dashboard.expiredVisas > 0 ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400' : 'bg-slate-50 text-slate-400', tab: 'visa' as Tab },
    { label: 'Passports w/ Company', value: dashboard.passportsHeldByCompany, icon: Shield, color: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400', tab: 'passports' as Tab },
  ] : [];

  return (
    <div className="space-y-5">
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
        {kpis.map(k => (
          <button key={k.label} type="button" onClick={() => onNavigate(k.tab)}
            className="surface flex flex-col gap-2 p-4 text-left hover:ring-2 hover:ring-sapphire/20 transition">
            <div className={`h-8 w-8 shrink-0 rounded-lg grid place-items-center ${k.color}`}>
              <k.icon className="h-4 w-4" />
            </div>
            <p className="text-2xl font-bold text-slate-900 dark:text-white">{k.value ?? '—'}</p>
            <p className="text-xs text-slate-500 dark:text-slate-400 leading-tight">{k.label}</p>
          </button>
        ))}
      </div>

      {(dashboard?.expiredVisas ?? 0) > 0 && (
        <div className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/5 p-4">
          <p className="text-sm font-semibold text-rose-700 dark:text-rose-400">
            ⚠ {dashboard?.expiredVisas} active visa record(s) are past expiry date. Immediate action required.
          </p>
        </div>
      )}

      <div className="surface p-5">
        <div className="mb-4 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Upcoming Expirations (90 days)</h3>
          <button type="button" onClick={() => onNavigate('expiry')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
        </div>
        {alerts.length === 0 ? (
          <p className="text-sm text-slate-400 dark:text-slate-500 py-4 text-center">No upcoming expirations in 90 days.</p>
        ) : (
          <div className="space-y-2">
            {alerts.map((a, i) => (
              <div key={i} className="flex items-center justify-between gap-4">
                <div className="min-w-0">
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-200 truncate">{a.employeeName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{a.type} · {a.subType}</p>
                </div>
                <div className="shrink-0 text-right">
                  <ExpiryBadge daysLeft={a.daysLeft} />
                  <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">{a.expiryDate}</p>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// ── Contracts Tab ─────────────────────────────────────────────────────────────

function ContractsTab() {
  const [contracts, setContracts] = useState<EmployeeContract[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState({ employeeId: '', contractType: 'Employment', startDate: '', basicSalary: '', currencyCode: 'USD', language: 'en' });
  const [saving, setSaving] = useState(false);

  const load = async () => {
    setLoading(true);
    try { const r = await complianceContractsApi.list({ status: statusFilter || undefined }); setContracts(r.items); } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [statusFilter]);

  const save = async () => {
    if (!form.employeeId || !form.startDate || !form.basicSalary) return;
    setSaving(true);
    try {
      await complianceContractsApi.create({ ...form, basicSalary: Number(form.basicSalary) });
      setShowCreate(false); load();
    } catch (e) { notifyApiError(e); } finally { setSaving(false); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    Active: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    PendingApproval: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
    Expired: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    Terminated: 'bg-slate-200 text-slate-600',
    Superseded: 'bg-slate-100 text-slate-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter by contract status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Draft', 'PendingApproval', 'Active', 'Expired', 'Terminated', 'Superseded'].map(s => <option key={s}>{s}</option>)}
        </select>
        <button type="button" onClick={() => setShowCreate(v => !v)}
          className="ml-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90">
          <Plus className="h-3.5 w-3.5" /> New Contract
        </button>
      </div>

      {showCreate && (
        <div className="surface p-4 space-y-3">
          <h4 className="text-sm font-semibold text-slate-800 dark:text-white">New Employee Contract</h4>
          <div className="grid grid-cols-2 gap-3">
            {[['employeeId', 'Employee ID (UUID)', 'text'], ['startDate', 'Start Date', 'date'], ['basicSalary', 'Basic Salary', 'number']].map(([k, l, t]) => (
              <div key={k}>
                <label htmlFor={`contract-${k}`} className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{l}</label>
                <input id={`contract-${k}`} type={t} title={l} value={(form as Record<string, string>)[k]} onChange={e => setForm(f => ({ ...f, [k]: e.target.value }))}
                  className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white" />
              </div>
            ))}
            <div>
              <label htmlFor="contract-type" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">Contract Type</label>
              <select id="contract-type" title="Contract Type" value={form.contractType} onChange={e => setForm(f => ({ ...f, contractType: e.target.value }))}
                className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white">
                {['Employment', 'Freelance', 'Internship', 'Contractor'].map(t => <option key={t}>{t}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="contract-currency" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">Currency</label>
              <select id="contract-currency" title="Currency" value={form.currencyCode} onChange={e => setForm(f => ({ ...f, currencyCode: e.target.value }))}
                className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white">
                {['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD'].map(c => <option key={c}>{c}</option>)}
              </select>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={save} disabled={saving} className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50">{saving ? 'Saving…' : 'Create Contract'}</button>
            <button type="button" onClick={() => setShowCreate(false)} className="text-xs text-slate-500 dark:text-slate-400">Cancel</button>
          </div>
        </div>
      )}

      {loading ? <p className="py-8 text-center text-sm text-slate-400">Loading…</p> : contracts.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No contracts found.</p>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Contract #', 'Employee', 'Type', 'Start', 'End', 'Salary', 'Version', 'Status'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {contracts.map(c => (
                <tr key={c.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                  <td className="p-3 font-mono text-xs text-sapphire dark:text-cyanAccent">{c.contractNumber}</td>
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{c.employeeName}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{c.contractType}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{c.startDate}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{c.endDate ?? 'Indefinite'}</td>
                  <td className="p-3 font-semibold text-slate-900 dark:text-white">{c.currencyCode} {c.basicSalary.toLocaleString()}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">v{c.version}</td>
                  <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[c.status] ?? ''}`}>{c.status}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Visa & Passport Tab ───────────────────────────────────────────────────────

function VisaPassportTab() {
  const [visas, setVisas] = useState<VisaRecord[]>([]);
  const [passports, setPassports] = useState<PassportRecord[]>([]);
  const [permits, setPermits] = useState<WorkPermitRecord[]>([]);
  const [subTab, setSubTab] = useState<'visa' | 'passport' | 'permit'>('visa');
  const [loading, setLoading] = useState(true);
  const [expiringFilter, setExpiringFilter] = useState('');

  const load = async () => {
    setLoading(true);
    try {
      const days = expiringFilter ? Number(expiringFilter) : undefined;
      if (subTab === 'visa') { const r = await complianceVisaApi.list({ expiringInDays: days }); setVisas(r.items); }
      if (subTab === 'passport') { const r = await compliancePassportsApi.list({ expiringInDays: days }); setPassports(r.items); }
      if (subTab === 'permit') { const r = await complianceWorkPermitsApi.list({ expiringInDays: days }); setPermits(r.items); }
    } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [subTab, expiringFilter]);

  const STATUS_COLORS: Record<string, string> = {
    Active: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Expired: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    Cancelled: 'bg-slate-100 text-slate-500',
    UnderRenewal: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2 flex-wrap">
        {(['visa', 'passport', 'permit'] as const).map(s => (
          <button key={s} type="button" onClick={() => setSubTab(s)}
            className={`rounded-lg px-3 py-1.5 text-xs font-medium transition ${
              subTab === s ? 'bg-sapphire text-white' : 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-400 hover:bg-slate-200 dark:hover:bg-white/20'
            }`}>
            {s === 'visa' ? 'Visas / Iqama' : s === 'passport' ? 'Passports' : 'Work Permits'}
          </button>
        ))}
        <select title="Filter by expiry" value={expiringFilter} onChange={e => setExpiringFilter(e.target.value)}
          className="ml-auto rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-xs text-slate-800 dark:text-slate-200">
          <option value="">All Records</option>
          <option value="30">Expiring ≤ 30 days</option>
          <option value="60">Expiring ≤ 60 days</option>
          <option value="90">Expiring ≤ 90 days</option>
        </select>
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">Loading…</p> : (
        <div className="surface overflow-x-auto">
          {subTab === 'visa' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Type', 'Visa #', 'Country', 'Issue', 'Expiry', 'Status'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {visas.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">No visa records.</td></tr> : visas.map(v => (
                  <tr key={v.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{v.employeeName}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{v.visaType}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{v.iqamaNumber || v.emiratesIdNumber || v.visaNumber || '—'}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{v.countryCode}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{v.issueDate}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(v.expiryDate + 'T00:00:00Z')} />
                      <span className="ml-1.5 text-slate-400">{v.expiryDate}</span>
                    </td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[v.status] ?? ''}`}>{v.status}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {subTab === 'passport' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Passport #', 'Nationality', 'Issue', 'Expiry', 'Held by Co.', 'Status'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {passports.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">No passport records.</td></tr> : passports.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{p.passportNumber}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{p.nationality}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{p.issueDate}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(p.expiryDate + 'T00:00:00Z')} />
                      <span className="ml-1.5 text-slate-400">{p.expiryDate}</span>
                    </td>
                    <td className="p-3">{p.isHeldByCompany ? <span className="text-xs font-medium text-amber-600 dark:text-amber-400">⚠ Yes</span> : <span className="text-xs text-slate-400">No</span>}</td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[p.status] ?? ''}`}>{p.status}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {subTab === 'permit' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Permit #', 'Type', 'Country', 'Issue', 'Expiry', 'Status'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {permits.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">No work permit records.</td></tr> : permits.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{p.permitNumber}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{p.permitType}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{p.countryCode}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{p.issueDate}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(p.expiryDate + 'T00:00:00Z')} />
                      <span className="ml-1.5 text-slate-400">{p.expiryDate}</span>
                    </td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[p.status] ?? ''}`}>{p.status}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  );
}

// ── Renewals Tab ──────────────────────────────────────────────────────────────

function RenewalsTab() {
  const [renewals, setRenewals] = useState<ComplianceRenewal[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');

  const load = async () => {
    setLoading(true);
    try { const r = await complianceRenewalsApi.list({ status: statusFilter || undefined }); setRenewals(r.items); } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [statusFilter]);

  const advance = async (id: string, status: string) => {
    try { await complianceRenewalsApi.updateStatus(id, status); load(); } catch (e) { notifyApiError(e); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Pending: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    InProgress: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Renewed: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Overdue: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    Exempted: 'bg-slate-100 text-slate-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter by renewal status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Pending', 'InProgress', 'Renewed', 'Overdue', 'Exempted'].map(s => <option key={s}>{s}</option>)}
        </select>
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">Loading…</p> : renewals.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No renewal records found.</p>
      ) : (
        <div className="space-y-2">
          {renewals.map(r => (
            <div key={r.id} className="surface p-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{r.documentType} · {r.documentNumber || 'No ref'}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                    Expires: {r.expiryDate}
                    {r.assignedToName && ` · Assigned: ${r.assignedToName}`}
                  </p>
                  {r.notes && <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5 italic">{r.notes}</p>}
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  <span className={`rounded-full px-2.5 py-1 text-xs font-medium ${STATUS_COLORS[r.status] ?? ''}`}>{r.status}</span>
                  {r.status === 'Pending' && (
                    <button type="button" onClick={() => advance(r.id, 'InProgress')}
                      className="rounded-lg border border-slate-200 dark:border-white/10 px-2 py-1 text-xs text-slate-600 dark:text-slate-400 hover:bg-slate-50 dark:hover:bg-white/5">
                      Start
                    </button>
                  )}
                  {r.status === 'InProgress' && (
                    <button type="button" onClick={() => advance(r.id, 'Renewed')}
                      className="rounded-lg bg-emerald-600 px-2 py-1 text-xs text-white hover:bg-emerald-700">
                      Mark Renewed
                    </button>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Expiry Alerts Tab ─────────────────────────────────────────────────────────

function ExpiryAlertsTab() {
  const [alerts, setAlerts] = useState<ExpiryAlert[]>([]);
  const [loading, setLoading] = useState(true);
  const [withinDays, setWithinDays] = useState(90);

  const load = async () => {
    setLoading(true);
    try { const r = await complianceReportsApi.expiryAlerts(withinDays); setAlerts(r.alerts); } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [withinDays]);

  const TYPE_ICONS: Record<string, string> = { Visa: '🪪', Passport: '📘', WorkPermit: '🏷', Contract: '📄' };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter expiry window" value={withinDays} onChange={e => setWithinDays(Number(e.target.value))}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value={30}>Expiring within 30 days</option>
          <option value={60}>Expiring within 60 days</option>
          <option value={90}>Expiring within 90 days</option>
          <option value={180}>Expiring within 180 days</option>
        </select>
        <span className="text-sm text-slate-500 dark:text-slate-400">{alerts.length} alert(s)</span>
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">Loading…</p> : alerts.length === 0 ? (
        <div className="py-12 text-center">
          <CheckCircle className="mx-auto h-10 w-10 text-emerald-400 mb-3" />
          <p className="text-sm font-medium text-slate-800 dark:text-slate-200">No expirations within {withinDays} days</p>
          <p className="text-xs text-slate-400 mt-1">All documents are up to date.</p>
        </div>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Document Type', 'Sub-type', 'Expiry Date', 'Days Left'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {alerts.map((a, i) => (
                <tr key={i} className={`hover:bg-slate-50 dark:hover:bg-white/[0.02] ${a.daysLeft <= 0 ? 'bg-rose-50/50 dark:bg-rose-500/5' : ''}`}>
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{a.employeeName}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{TYPE_ICONS[a.type] ?? '📋'} {a.type}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{a.subType || '—'}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{a.expiryDate}</td>
                  <td className="p-3"><ExpiryBadge daysLeft={a.daysLeft} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Compliance AI Tab ─────────────────────────────────────────────────────────

function ComplianceAITab() {
  const [insights, setInsights] = useState<ComplianceAIInsight[]>([]);
  const [loading, setLoading] = useState(true);
  const [genTime, setGenTime] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const r = await complianceReportsApi.aiInsights();
      setInsights(r.insights);
      setGenTime(r.generatedAt);
    } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  const SEV_COLORS: Record<string, string> = {
    Critical: 'border-l-rose-600 bg-rose-50 dark:bg-rose-500/5',
    High: 'border-l-rose-500 bg-rose-50 dark:bg-rose-500/5',
    Medium: 'border-l-amber-500 bg-amber-50 dark:bg-amber-500/5',
    Low: 'border-l-blue-400 bg-blue-50 dark:bg-blue-500/5',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5 px-4 py-3 text-xs text-amber-700 dark:text-amber-400 flex-1 mr-3">
          <Lightbulb className="inline h-3.5 w-3.5 mr-1.5" />
          All compliance insights are <strong>advisory only</strong> and must not automatically approve compliance, reject employees, or make legal decisions.
          {genTime && <span className="ml-2 opacity-60">Generated: {new Date(genTime).toLocaleString()}</span>}
        </div>
        <button type="button" onClick={load} className="shrink-0 flex items-center gap-1.5 rounded-lg border border-slate-200 dark:border-white/10 px-3 py-1.5 text-xs text-slate-600 dark:text-slate-400 hover:bg-slate-50 dark:hover:bg-white/5">
          <RefreshCw className="h-3.5 w-3.5" /> Refresh
        </button>
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">Generating compliance insights…</p> : insights.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No insights available.</p>
      ) : (
        <div className="space-y-3">
          {insights.map((ins, i) => (
            <div key={i} className={`rounded-xl border-l-4 p-4 ${SEV_COLORS[ins.severity] ?? 'border-l-slate-400 bg-slate-50'}`}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 mb-1">
                    <p className="text-sm font-semibold text-slate-900 dark:text-slate-100">{ins.title}</p>
                    <span className="text-xs font-medium text-amber-600 dark:text-amber-400 border border-amber-300 dark:border-amber-600 rounded px-1.5 py-0.5">Advisory</span>
                  </div>
                  <p className="text-xs text-slate-600 dark:text-slate-400">{ins.description}</p>
                </div>
                <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${
                  ins.severity === 'Critical' || ins.severity === 'High' ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400' :
                  ins.severity === 'Medium' ? 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400' :
                  'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-400'
                }`}>{ins.severity}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'contracts' | 'visa' | 'passports' | 'renewals' | 'expiry' | 'ai';

export default function CompliancePage() {
  const [tab, setTab] = useState<Tab>('dashboard');

  const tabs: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
    { id: 'dashboard', label: 'Dashboard', icon: BarChart3 },
    { id: 'contracts', label: 'Contracts', icon: FileText },
    { id: 'visa', label: 'Visa & ID', icon: Globe },
    { id: 'renewals', label: 'Renewals', icon: RefreshCw },
    { id: 'expiry', label: 'Expiry Alerts', icon: AlertTriangle },
    { id: 'ai', label: 'Insights', icon: Lightbulb },
  ];

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Document & Compliance Management</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          Manage contracts, visas, passports, work permits, and GCC compliance documentation.
        </p>
      </div>

      <div className="flex gap-1 flex-wrap rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]" style={{ width: 'fit-content', maxWidth: '100%' }}>
        {tabs.map(t => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={`flex items-center gap-1.5 rounded-lg px-4 py-1.5 text-sm font-medium transition ${
              tab === t.id
                ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent'
                : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
            }`}
          >
            <t.icon className="h-3.5 w-3.5" />
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'dashboard' && <DashboardTab onNavigate={setTab} />}
      {tab === 'contracts' && <ContractsTab />}
      {tab === 'visa' && <VisaPassportTab />}
      {tab === 'renewals' && <RenewalsTab />}
      {tab === 'expiry' && <ExpiryAlertsTab />}
      {tab === 'ai' && <ComplianceAITab />}
    </div>
  );
}
