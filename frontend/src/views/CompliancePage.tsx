'use client';

import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import { notifyApiError } from '../api/client';
import { useBusyKeys } from '../hooks/useBusyKeys';
import { notifyRefusal } from '../lib/refusal';
import {
  FileText, Shield, Globe, AlertTriangle, BarChart3, Lightbulb,
  Plus, CheckCircle, Clock, XCircle, RefreshCw, FileWarning,
} from 'lucide-react';
import { employeesApi } from '../api/employees';
import type { EmployeeExpiringDocument, EmployeeMissingDocumentsReport } from '../api/employees';
import {
  complianceContractsApi, complianceVisaApi, compliancePassportsApi,
  complianceWorkPermitsApi, complianceRenewalsApi, complianceReportsApi,
} from '../api/compliance';
import type {
  EmployeeContract, VisaRecord, PassportRecord, WorkPermitRecord,
  ComplianceRenewal, ComplianceDashboard, ExpiryAlert, ComplianceAIInsight,
} from '../api/compliance';
import { EmployeeSearchSelect } from '../components/EmployeeSearchSelect';
import type { EmployeeSelection } from '../components/EmployeeSearchSelect';
import { useFullList } from '../hooks/useFullList';
import { describeApiError } from '../lib/apiError';
import { JawazatPanel } from '../components/compliance/JawazatPanel';
import { BenefitsAwaitingNotice } from '../components/entitlements/BenefitsAwaitingNotice';
import { packageApi, type ContractPackageStatus } from '../api/package';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { enumLabel } from '../i18n/enumLabel';
import { useReleaseA } from '../lib/releaseA';
import { fill } from '../lib/renewalRadar';
import { useAuth } from '../contexts/AuthContext';
import { employeeWorkspaceHref } from '../lib/peopleRoutes';
import { DocumentsToVerifyQueue } from '../components/employees/DocumentsToVerifyQueue';

// ── Helpers ───────────────────────────────────────────────────────────────────

function daysUntil(dateStr: string): number {
  return Math.ceil((new Date(dateStr).getTime() - Date.now()) / 86400000);
}

/** A document or record type from the API ("Iqama", "WorkPermit") in the viewer's language; an unknown one reads as plain English. */
function docLabel(t: (key: string) => string, value: string | null | undefined): string {
  return enumLabel(t, 'ComplianceDoc', value);
}

function ExpiryBadge({ daysLeft }: { daysLeft: number }) {
  const { t } = useLocale();
  const cls = daysLeft < 0
    ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400'
    : daysLeft <= 30
    ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400'
    : daysLeft <= 60
    ? 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400'
    : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400';
  const label = daysLeft < 0 ? t('{days}d overdue', { days: Math.abs(daysLeft) }) : t('{days}d left', { days: daysLeft });
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{label}</span>;
}

/** A list that failed to load says so; it never renders as an empty (or shorter) register. */
function ListLoadError({ what, error, onRetry }: { what: string; error: unknown; onRetry: () => void }) {
  const { t } = useLocale();
  return (
    <div role="alert" className="surface flex flex-wrap items-center gap-3 border-s-4 border-s-rose-500 p-4 text-sm text-rose-600 dark:text-rose-400">
      <span>{t('{what} could not be loaded.', { what: t(what) })} {describeApiError(error, t)}</span>
      <button type="button" onClick={onRetry} className="ms-auto rounded-lg border border-slate-200 px-3 py-1 text-xs text-slate-600 dark:border-white/10 dark:text-slate-300">{t('Retry')}</button>
    </div>
  );
}

type RecordKind = 'contract' | 'renewal' | 'visa' | 'passport' | 'permit';

function RecordCount({ n, kind }: { n: number; kind: RecordKind }) {
  const { t } = useLocale();
  const text = {
    contract: t('{count, plural, one {# contract} other {# contracts}}', { count: n }),
    renewal: t('{count, plural, one {# renewal} other {# renewals}}', { count: n }),
    visa: t('{count, plural, one {# visa record} other {# visa records}}', { count: n }),
    passport: t('{count, plural, one {# passport} other {# passports}}', { count: n }),
    permit: t('{count, plural, one {# work permit} other {# work permits}}', { count: n }),
  }[kind];
  return <span className="text-sm text-slate-500 dark:text-slate-400">{text}</span>;
}

// ── Dashboard Tab ─────────────────────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [dashboard, setDashboard] = useState<ComplianceDashboard | null>(null);
  const [alerts, setAlerts] = useState<ExpiryAlert[]>([]);
  const [alertsTotal, setAlertsTotal] = useState<number | null>(null);

  useEffect(() => {
    complianceReportsApi.dashboard().then(setDashboard).catch(() => {});
    // The first eight are a preview; the header says how many there are, and "View all" lists them.
    complianceReportsApi.expiryAlerts(90).then(r => { setAlerts(r.alerts.slice(0, 8)); setAlertsTotal(r.total ?? r.alerts.length); }).catch(() => {});
  }, []);
  const alertsWindow = alertsTotal != null && alerts.length < alertsTotal ? t('Showing {shown} of {total} expirations', { shown: fx.number(alerts.length), total: fx.number(alertsTotal) }) : null;

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
            className="surface flex flex-col gap-2 p-4 text-start hover:ring-2 hover:ring-sapphire/20 transition">
            <div className={`h-8 w-8 shrink-0 rounded-lg grid place-items-center ${k.color}`}>
              <k.icon className="h-4 w-4" />
            </div>
            <p className="text-2xl font-semibold text-slate-900 dark:text-white">{k.value ?? '—'}</p>
            <p className="text-xs text-slate-500 dark:text-slate-400 leading-tight">{t(k.label)}</p>
          </button>
        ))}
      </div>

      {(dashboard?.expiredVisas ?? 0) > 0 && (
        <div className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/5 p-4">
          <p className="text-sm font-semibold text-rose-700 dark:text-rose-400">
            ⚠ {t('{count, plural, one {# active visa record is past its expiry date. Immediate action required.} other {# active visa records are past their expiry date. Immediate action required.}}', { count: dashboard?.expiredVisas ?? 0 })}
          </p>
        </div>
      )}

      <div className="surface p-5">
        <div className="mb-4 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Upcoming Expirations (90 days)')}</h3>
          <div className="flex items-center gap-3">
            {alertsWindow && <span className="text-xs text-slate-500 dark:text-slate-400">{alertsWindow}</span>}
            <button type="button" onClick={() => onNavigate('expiry')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">{t('View all')}</button>
          </div>
        </div>
        {alerts.length === 0 ? (
          <p className="text-sm text-slate-400 dark:text-slate-500 py-4 text-center">{t('No upcoming expirations in 90 days.')}</p>
        ) : (
          <div className="space-y-2">
            {alerts.map((a, i) => (
              <div key={i} className="flex items-center justify-between gap-4">
                <div className="min-w-0">
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-200 truncate">{a.employeeName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{docLabel(t, a.type)} · {a.subType ? docLabel(t, a.subType) : ''}</p>
                </div>
                <div className="shrink-0 text-end">
                  <ExpiryBadge daysLeft={a.daysLeft} />
                  <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">{fx.plain.date(a.expiryDate)}</p>
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
  const fx = useFormat();
  // Creating a contract is `compliance.write` on the API (ContractsController.Create); a read-only
  // compliance user (Auditor) was offered "New Contract" and only ever got a 403.
  const { hasPermission } = useAuth();
  const canCreate = hasPermission('compliance.write');
  const [statusFilter, setStatusFilter] = useState('');
  // Release A (R4): the renewal number and a link to the contract's history, for release_a tenants only.
  const releaseA = useReleaseA();
  const { t } = useLocale();
  // Every contract, not the server's first 20: an active contract past its end date is acted on
  // from this register ("Mark expired"), so one on a later page would silently never be.
  const list = useFullList<EmployeeContract>(() => complianceContractsApi.listAll({ status: statusFilter || undefined }));
  const { items: contracts, loading } = list;
  const [showCreate, setShowCreate] = useState(false);
  const [selectedEmployee, setSelectedEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({ employeeId: '', contractType: 'Employment', startDate: '', basicSalary: '', currencyCode: 'USD', language: 'en' });
  const [saving, setSaving] = useState(false);
  const [transitioning, setTransitioning] = useState<string | null>(null);
  const [hrSignatories, setHrSignatories] = useState<Record<string, string>>({});

  const load = () => list.reload();

  useEffect(() => { void list.reload(); }, [statusFilter, list.reload]);

  // Release A: an active contract whose benefits wait for a second person gets one next action on its row.
  const [packageStatus, setPackageStatus] = useState<Record<string, ContractPackageStatus>>({});
  const [proposing, setProposing] = useState<string | null>(null);
  const activeIds = contracts.filter((c) => c.status === 'Active').map((c) => c.id).join(',');
  const loadPackageStatus = useCallback(async () => {
    if (!releaseA || !hasPermission('entitlements.read') || !activeIds) { setPackageStatus({}); return; }
    try {
      const rows = await packageApi.contractStatus(activeIds.split(',').slice(0, 200));
      setPackageStatus(Object.fromEntries(rows.map((r) => [r.contractId, r])));
    } catch { setPackageStatus({}); }
  }, [releaseA, hasPermission, activeIds]);
  useEffect(() => { void loadPackageStatus(); }, [loadPackageStatus]);
  const proposeBenefits = async (status: ContractPackageStatus) => {
    setProposing(status.contractId);
    try { await packageApi.propose(status.employeeId, status.contractId); await loadPackageStatus(); }
    catch (e) { notifyApiError(e); }
    finally { setProposing(null); }
  };

  const save = async () => {
    if (!form.employeeId || !form.startDate || !form.basicSalary) return;
    setSaving(true);
    try {
      await complianceContractsApi.create({ ...form, basicSalary: Number(form.basicSalary) });
      setShowCreate(false); load();
    } catch (e) { notifyApiError(e); } finally { setSaving(false); }
  };

  const transition = async (contract: EmployeeContract, status: string) => {
    const signedByHrName = hrSignatories[contract.id]?.trim();
    if (status === 'Active' && !signedByHrName) {
      notifyApiError({ response: { data: { message: t('Enter the HR signatory name before activating the contract.') } } });
      return;
    }
    setTransitioning(contract.id);
    try {
      await complianceContractsApi.updateStatus(contract.id, status, signedByHrName || undefined);
      await load();
    } catch (e) { notifyApiError(e); }
    finally { setTransitioning(null); }
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
        <select title={t('Filter by contract status')} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">{t('All Statuses')}</option>
          {['Draft', 'PendingApproval', 'Active', 'Expired', 'Terminated', 'Superseded'].map(s => <option key={s} value={s}>{enumLabel(t, 'Status', s)}</option>)}
        </select>
        {!loading && list.error == null && <RecordCount n={contracts.length} kind="contract" />}
        {canCreate && <button type="button" onClick={() => setShowCreate(v => !v)}
          className="ms-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90">
          <Plus className="h-3.5 w-3.5" /> {t('New Contract')}
        </button>}
      </div>

      {canCreate && showCreate && (
        <div className="surface p-4 space-y-3">
          <h4 className="text-sm font-semibold text-slate-800 dark:text-white">{t('New Employee Contract')}</h4>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <label className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{t('Employee')}</label>
              <EmployeeSearchSelect
                value={selectedEmployee}
                onChange={(employee) => {
                  setSelectedEmployee(employee);
                  setForm(current => ({ ...current, employeeId: employee?.publicId ?? '' }));
                }}
                required
              />
            </div>
            {[['startDate', 'Start Date', 'date'], ['basicSalary', 'Basic Salary', 'number']].map(([k, l, inputType]) => (
              <div key={k}>
                <label htmlFor={`contract-${k}`} className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{t(l)}</label>
                <input id={`contract-${k}`} type={inputType} title={t(l)} value={(form as Record<string, string>)[k]} onChange={e => setForm(f => ({ ...f, [k]: e.target.value }))}
                  className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white" />
              </div>
            ))}
            <div>
              <label htmlFor="contract-type" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{t('Contract Type')}</label>
              <select id="contract-type" title={t('Contract Type')} value={form.contractType} onChange={e => setForm(f => ({ ...f, contractType: e.target.value }))}
                className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white">
                {['Employment', 'Freelance', 'Internship', 'Contractor'].map(kind => <option key={kind} value={kind}>{t(kind)}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="contract-currency" className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{t('Currency')}</label>
              <select id="contract-currency" title={t('Currency')} value={form.currencyCode} onChange={e => setForm(f => ({ ...f, currencyCode: e.target.value }))}
                className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white">
                {['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD'].map(c => <option key={c} value={c}>{fx.currencyLabel(c)}</option>)}
              </select>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={save} disabled={saving} className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50">{saving ? t('Saving…') : t('Create Contract')}</button>
            <button type="button" onClick={() => setShowCreate(false)} className="text-xs text-slate-500 dark:text-slate-400">{t('Cancel')}</button>
          </div>
        </div>
      )}

      {loading ? <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : list.error != null ? (
        <ListLoadError what="Contracts" error={list.error} onRetry={load} />
      ) : contracts.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">{t('No contracts found.')}</p>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Contract #', 'Employee', 'Type', 'Start', 'End', 'Salary', 'Version', ...(releaseA ? [t('Renewal')] : []), 'Status', 'Actions'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {contracts.map(c => (
                <Fragment key={c.id}>
                <tr className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                  <td className="p-3 font-mono text-xs text-sapphire dark:text-cyanAccent">{c.contractNumber}</td>
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{c.employeeName}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{t(c.contractType)}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fx.plain.date(c.startDate)}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{c.endDate ? fx.plain.date(c.endDate) : t('Indefinite')}</td>
                  <td className="p-3 font-semibold text-slate-900 dark:text-white">{fx.plain.money(c.basicSalary, c.currencyCode)}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">v{c.version}</td>
                  {releaseA && (
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">
                      {c.status === 'Draft' || c.status === 'PendingApproval' ? '—'
                        : c.renewalNumber != null ? fill(t('Renewal #{n}'), { n: c.renewalNumber }) : t('History not confirmed')}
                      <a href={`/contract-renewals?contract=${c.id}`} className="block font-medium text-sapphire underline dark:text-cyanAccent">{t('Contract history')}</a>
                    </td>
                  )}
                  <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[c.status] ?? ''}`}>{enumLabel(t, 'Status', c.status)}</span></td>
                  <td className="p-3">
                    <div className="flex min-w-[230px] flex-wrap items-center gap-2">
                      {c.status === 'Draft' && <button type="button" disabled={transitioning === c.id} onClick={() => transition(c, 'PendingApproval')} className="rounded bg-sapphire px-2 py-1 text-xs text-white disabled:opacity-50">{t('Submit')}</button>}
                      {c.status === 'PendingApproval' && <>
                        <input className="min-w-[150px] rounded border border-slate-200 px-2 py-1 text-xs dark:border-white/10 dark:bg-white/5" aria-label={t('HR signatory for {number}', { number: c.contractNumber })} placeholder={t('HR signatory name')} value={hrSignatories[c.id] ?? ''} onChange={e => setHrSignatories(x => ({ ...x, [c.id]: e.target.value }))} />
                        <button type="button" disabled={transitioning === c.id} onClick={() => transition(c, 'Active')} className="rounded bg-emerald-600 px-2 py-1 text-xs text-white disabled:opacity-50">{t('Activate')}</button>
                        <button type="button" disabled={transitioning === c.id} onClick={() => transition(c, 'Draft')} className="rounded bg-slate-200 px-2 py-1 text-xs text-slate-700 disabled:opacity-50">{t('Send back')}</button>
                      </>}
                      {c.status === 'Active' && <>
                        {c.endDate && c.endDate <= new Date().toISOString().slice(0, 10) && <button type="button" disabled={transitioning === c.id} onClick={() => transition(c, 'Expired')} className="rounded bg-amber-600 px-2 py-1 text-xs text-white disabled:opacity-50">{t('Mark expired')}</button>}
                        <button type="button" disabled={transitioning === c.id} onClick={() => transition(c, 'Terminated')} className="rounded bg-rose-600 px-2 py-1 text-xs text-white disabled:opacity-50">{t('Terminate')}</button>
                      </>}
                      {['Expired', 'Terminated', 'Superseded'].includes(c.status) && <span className="text-xs text-slate-400">{t('Terminal')}</span>}
                    </div>
                  </td>
                </tr>
                {packageStatus[c.id] && (
                  <tr data-testid={`contract-benefits-${c.id}`}>
                    <td colSpan={9} className="px-3 pb-3">
                      <BenefitsAwaitingNotice action={packageStatus[c.id].nextAction} busy={proposing === c.id}
                        onPropose={hasPermission('entitlements.manage') ? () => void proposeBenefits(packageStatus[c.id]) : undefined}
                        reviewHref={employeeWorkspaceHref(packageStatus[c.id].employeeId, { section: 'pay-benefits', panel: 'package' })} />
                    </td>
                  </tr>
                )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Visa & Passport Tab ───────────────────────────────────────────────────────

function VisaPassportTab({ initialSubTab = 'visa' }: { initialSubTab?: 'visa' | 'passport' | 'permit' }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [subTab, setSubTab] = useState<'visa' | 'passport' | 'permit'>(initialSubTab);
  const [expiringFilter, setExpiringFilter] = useState('');
  const days = expiringFilter ? Number(expiringFilter) : undefined;

  // Expiry registers are read in full, soonest expiry first. They used to show the server's first
  // 20 rows with nothing to say more existed, so the 21st iqama due to lapse was simply not there.
  const visaList = useFullList<VisaRecord>(() => complianceVisaApi.listAll({ expiringInDays: days }));
  const passportList = useFullList<PassportRecord>(() => compliancePassportsApi.listAll({ expiringInDays: days }));
  const permitList = useFullList<WorkPermitRecord>(() => complianceWorkPermitsApi.listAll({ expiringInDays: days }));
  const active = subTab === 'visa' ? visaList : subTab === 'passport' ? passportList : permitList;
  const { items: visas } = visaList;
  const { items: passports } = passportList;
  const { items: permits } = permitList;
  const loading = active.loading;
  const load = () => active.reload();
  const kind: RecordKind = subTab === 'visa' ? 'visa' : subTab === 'passport' ? 'passport' : 'permit';

  useEffect(() => { void load(); }, [subTab, expiringFilter]);

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
            {s === 'visa' ? t('Visas / Iqama') : s === 'passport' ? t('Passports') : t('Work Permits')}
          </button>
        ))}
        <select title={t('Filter by expiry')} value={expiringFilter} onChange={e => setExpiringFilter(e.target.value)}
          className="ms-auto rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-xs text-slate-800 dark:text-slate-200">
          <option value="">{t('All Records')}</option>
          <option value="30">{t('Expiring ≤ {days} days', { days: 30 })}</option>
          <option value="60">{t('Expiring ≤ {days} days', { days: 60 })}</option>
          <option value="90">{t('Expiring ≤ {days} days', { days: 90 })}</option>
        </select>
        {!loading && active.error == null && <RecordCount n={active.items.length} kind={kind} />}
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : active.error != null ? (
        <ListLoadError what={subTab === 'visa' ? 'Visa and iqama records' : subTab === 'passport' ? 'Passports' : 'Work permits'} error={active.error} onRetry={load} />
      ) : (
        <div className="surface overflow-x-auto">
          {subTab === 'visa' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Type', 'Visa #', 'Country', 'Issue', 'Expiry', 'Status'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {visas.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">{t('No visa records.')}</td></tr> : visas.map(v => (
                  <tr key={v.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{v.employeeName}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{docLabel(t, v.visaType)}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{v.iqamaNumber || v.emiratesIdNumber || v.visaNumber || '—'}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{v.countryCode}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fx.plain.date(v.issueDate)}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(v.expiryDate + 'T00:00:00Z')} />
                      <span className="ms-1.5 text-slate-400">{fx.plain.date(v.expiryDate)}</span>
                    </td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[v.status] ?? ''}`}>{enumLabel(t, 'Status', v.status)}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {subTab === 'passport' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Passport #', 'Nationality', 'Issue', 'Expiry', 'Held by Co.', 'Status'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {passports.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">{t('No passport records.')}</td></tr> : passports.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{p.passportNumber}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{p.nationality}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fx.plain.date(p.issueDate)}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(p.expiryDate + 'T00:00:00Z')} />
                      <span className="ms-1.5 text-slate-400">{fx.plain.date(p.expiryDate)}</span>
                    </td>
                    <td className="p-3">{p.isHeldByCompany ? <span className="text-xs font-medium text-amber-600 dark:text-amber-400">⚠ {t('Yes')}</span> : <span className="text-xs text-slate-400">{t('No')}</span>}</td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[p.status] ?? ''}`}>{enumLabel(t, 'Status', p.status)}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {subTab === 'permit' && (
            <table className="w-full text-sm">
              <thead><tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Permit #', 'Type', 'Country', 'Issue', 'Expiry', 'Status'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {permits.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-sm text-slate-400">{t('No work permit records.')}</td></tr> : permits.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</td>
                    <td className="p-3 font-mono text-xs text-slate-600 dark:text-slate-400">{p.permitNumber}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{docLabel(t, p.permitType)}</td>
                    <td className="p-3 text-slate-500 dark:text-slate-400">{p.countryCode}</td>
                    <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fx.plain.date(p.issueDate)}</td>
                    <td className="p-3 text-xs">
                      <ExpiryBadge daysLeft={daysUntil(p.expiryDate + 'T00:00:00Z')} />
                      <span className="ms-1.5 text-slate-400">{fx.plain.date(p.expiryDate)}</span>
                    </td>
                    <td className="p-3"><span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[p.status] ?? ''}`}>{enumLabel(t, 'Status', p.status)}</span></td>
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
  const { t, locale } = useLocale();
  const fx = useFormat();
  const { run, isBusy } = useBusyKeys();
  const [statusFilter, setStatusFilter] = useState('');
  // Every renewal is a task someone owns; the queue is read in full, not the server's first 20.
  const list = useFullList<ComplianceRenewal>(() => complianceRenewalsApi.listAll({ status: statusFilter || undefined }));
  const { items: renewals, loading } = list;
  const load = () => list.reload();

  useEffect(() => { void list.reload(); }, [statusFilter, list.reload]);

  // One request per row however often it is clicked; a refusal says what did not change and what to do.
  const advance = (id: string, status: string) => run(`row:${id}`, async () => {
    try { await complianceRenewalsApi.updateStatus(id, status); load(); }
    catch (e) { notifyRefusal(e, { what: t('The renewal status was not changed'), fix: t('Reload the list to see its current status.') }, t, locale); }
  });

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
        <select title={t('Filter by renewal status')} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">{t('All Statuses')}</option>
          {['Pending', 'InProgress', 'Renewed', 'Overdue', 'Exempted'].map(s => <option key={s} value={s}>{enumLabel(t, 'Status', s)}</option>)}
        </select>
        {!loading && list.error == null && <RecordCount n={renewals.length} kind="renewal" />}
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : list.error != null ? (
        <ListLoadError what="Renewals" error={list.error} onRetry={load} />
      ) : renewals.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">{t('No renewal records found.')}</p>
      ) : (
        <div className="space-y-2">
          {renewals.map(r => (
            <div key={r.id} className="surface p-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{docLabel(t, r.documentType)} · {r.documentNumber || t('No ref')}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                    {t('Expires: {date}', { date: fx.plain.date(r.expiryDate) })}
                    {r.assignedToName && ` · ${t('Assigned: {name}', { name: r.assignedToName })}`}
                  </p>
                  {r.notes && <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5 italic">{r.notes}</p>}
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  <span className={`rounded-full px-2.5 py-1 text-xs font-medium ${STATUS_COLORS[r.status] ?? ''}`}>{enumLabel(t, 'Status', r.status)}</span>
                  {r.status === 'Pending' && (
                    <button type="button" onClick={() => void advance(r.id, 'InProgress')} disabled={isBusy(`row:${r.id}`)} aria-busy={isBusy(`row:${r.id}`) || undefined}
                      className="rounded-lg border disabled:opacity-50 border-slate-200 dark:border-white/10 px-2 py-1 text-xs text-slate-600 dark:text-slate-400 hover:bg-slate-50 dark:hover:bg-white/5">
                      {t('Start')}
                    </button>
                  )}
                  {r.status === 'InProgress' && (
                    <button type="button" onClick={() => void advance(r.id, 'Renewed')} disabled={isBusy(`row:${r.id}`)} aria-busy={isBusy(`row:${r.id}`) || undefined}
                      className="rounded-lg disabled:opacity-50 bg-emerald-600 px-2 py-1 text-xs text-white hover:bg-emerald-700">
                      {t('Mark Renewed')}
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
  const { t } = useLocale();
  const fx = useFormat();
  const [withinDays, setWithinDays] = useState(90);
  // The endpoint returns every alert in the window. A failed load used to render the green
  // "No expirations" all-clear; it now says the alerts could not be loaded.
  const list = useFullList<ExpiryAlert>(() => complianceReportsApi.expiryAlerts(withinDays).then(r => r.alerts));
  const { items: alerts, loading } = list;
  const load = () => list.reload();

  useEffect(() => { void list.reload(); }, [withinDays, list.reload]);

  const TYPE_ICONS: Record<string, string> = { Visa: '🪪', Passport: '📘', WorkPermit: '🏷', Contract: '📄' };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title={t('Filter expiry window')} value={withinDays} onChange={e => setWithinDays(Number(e.target.value))}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value={30}>{t('Expiring within 30 days')}</option>
          <option value={60}>{t('Expiring within 60 days')}</option>
          <option value={90}>{t('Expiring within 90 days')}</option>
          <option value={180}>{t('Expiring within 180 days')}</option>
        </select>
        {!loading && list.error == null && <span className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# alert} other {# alerts}}', { count: alerts.length })}</span>}
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : list.error != null ? (
        <ListLoadError what="Expiry alerts" error={list.error} onRetry={load} />
      ) : alerts.length === 0 ? (
        <div className="py-12 text-center">
          <CheckCircle className="mx-auto h-10 w-10 text-emerald-400 mb-3" />
          <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{t('No expirations within {days} days', { days: withinDays })}</p>
          <p className="text-xs text-slate-400 mt-1">{t('All documents are up to date.')}</p>
        </div>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Document Type', 'Sub-type', 'Expiry Date', 'Days Left'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {alerts.map((a, i) => (
                <tr key={i} className={`hover:bg-slate-50 dark:hover:bg-white/[0.02] ${a.daysLeft <= 0 ? 'bg-rose-50/50 dark:bg-rose-500/5' : ''}`}>
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{a.employeeName}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{TYPE_ICONS[a.type] ?? '📋'} {docLabel(t, a.type)}</td>
                  <td className="p-3 text-slate-500 dark:text-slate-400">{a.subType ? docLabel(t, a.subType) : '—'}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fx.plain.date(a.expiryDate)}</td>
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

// ── Employee Documents Tab ────────────────────────────────────────────────────
//
// The dashboard has counted expiring and missing EMPLOYEE DOCUMENTS since it was written, and both
// chips linked here — to a page whose every tab reads a DIFFERENT table. `Expiry Alerts` above
// walks the visa / passport / work-permit / contract records; these two counters walk
// `EmployeeDocuments`. The reports themselves have existed at
// `/api/employees/reports/expiring-documents` and `/missing-documents` with no caller, so the
// numbers were real and the click went nowhere. This is where they live.

function EmployeeDocumentsTab() {
  const { t } = useLocale();
  const tRef = useRef(t);
  tRef.current = t;
  const fx = useFormat();
  const { hasPermission } = useAuth();
  const searchParams = useSearchParams();
  // G-004: the queue of documents waiting for HR to verify them. Only a holder of employees.documents can act on one.
  const canVerify = hasPermission('employees.documents');
  const [view, setView] = useState<'expiring' | 'missing' | 'verify'>(canVerify && searchParams?.get('docs') === 'verify' ? 'verify' : 'expiring');
  const [days, setDays] = useState(60);
  const [expiring, setExpiring] = useState<EmployeeExpiringDocument[] | null>(null);
  const [missing, setMissing] = useState<EmployeeMissingDocumentsReport[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    if (view === 'verify') { setLoading(false); setError(''); return () => { cancelled = true; }; }
    setLoading(true); setError('');
    const req = view === 'expiring'
      ? employeesApi.reports.expiringDocuments(days).then(r => { if (!cancelled) setExpiring(r); })
      : employeesApi.reports.missingDocuments().then(r => { if (!cancelled) setMissing(r); });
    req
      .catch((e: unknown) => {
        if (cancelled) return;
        const status = (e as { response?: { status?: number } })?.response?.status;
        setError(status === 403
          ? tRef.current('Your role cannot read employee document reports. These are restricted to Admin, HR Manager, HR Officer, Payroll Officer and Auditor.')
          : tRef.current('The employee document report could not be loaded.'));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [view, days]);

  const rows = view === 'expiring' ? expiring : view === 'missing' ? missing : null;
  const viewOptions: Array<['expiring' | 'missing' | 'verify', string]> = [['expiring', t('Expiring')], ['missing', t('Missing')]];
  if (canVerify) viewOptions.push(['verify', t('To verify')]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex gap-1 rounded-lg border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
          {viewOptions.map(([k, label]) => (
            <button
              key={k}
              type="button"
              onClick={() => setView(k)}
              aria-pressed={view === k}
              className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                view === k
                  ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent'
                  : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
              }`}
            >
              {label}
            </button>
          ))}
        </div>
        {view === 'expiring' && (
          <select
            title={t('Filter expiry window')}
            value={days}
            onChange={e => setDays(Number(e.target.value))}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-800 dark:border-white/10 dark:bg-white/5 dark:text-slate-200"
          >
            <option value={30}>{t('Expiring within 30 days')}</option>
            <option value={60}>{t('Expiring within 60 days')}</option>
            <option value={90}>{t('Expiring within 90 days')}</option>
            <option value={180}>{t('Expiring within 180 days')}</option>
          </select>
        )}
        {rows && <span className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# employee record} other {# employee records}}', { count: rows.length })}</span>}
      </div>

      {error && (
        <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-400/20 dark:bg-rose-500/10 dark:text-rose-400">
          {error}
        </p>
      )}

      {view === 'verify' ? (
        <DocumentsToVerifyQueue />
      ) : loading ? (
        <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p>
      ) : error ? null : view === 'expiring' ? (
        (expiring ?? []).length === 0 ? (
          <div className="py-12 text-center">
            <CheckCircle className="mx-auto mb-3 h-10 w-10 text-emerald-400" />
            <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{t('No employee documents expire within {days} days', { days })}</p>
            <p className="mt-1 text-xs text-slate-400">{t('Every filed document is in date.')}</p>
          </div>
        ) : (
          <div className="surface overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-slate-200 dark:border-white/10">
                  {['Employee', 'Code', 'Document Type', 'Expiry Date', 'Days Left'].map(h => (
                    <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {(expiring ?? []).map((d, i) => {
                  const left = d.expiryDate ? daysUntil(d.expiryDate) : null;
                  return (
                    <tr key={`${d.employeeId}-${d.documentType}-${i}`} className={`hover:bg-slate-50 dark:hover:bg-white/[0.02] ${left !== null && left <= 0 ? 'bg-rose-50/50 dark:bg-rose-500/5' : ''}`}>
                      <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{d.fullName}</td>
                      <td className="p-3 font-mono text-xs text-slate-500 dark:text-slate-400">{d.employeeCode}</td>
                      <td className="p-3 text-slate-500 dark:text-slate-400">{docLabel(t, d.documentType)}</td>
                      <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{d.expiryDate ? fx.plain.date(d.expiryDate) : '—'}</td>
                      <td className="p-3">{left === null ? <span className="text-xs text-slate-400">—</span> : <ExpiryBadge daysLeft={left} />}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )
      ) : (missing ?? []).length === 0 ? (
        <div className="py-12 text-center">
          <CheckCircle className="mx-auto mb-3 h-10 w-10 text-emerald-400" />
          <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{t('No employee is missing a required document')}</p>
          <p className="mt-1 text-xs text-slate-400">{t('Every active employee file is complete.')}</p>
        </div>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Employee', 'Code', 'Missing Documents'].map(h => (
                  <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h === '#' || h === '' ? h : t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {(missing ?? []).map(m => (
                <tr key={m.employeeId} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{m.fullName}</td>
                  <td className="p-3 font-mono text-xs text-slate-500 dark:text-slate-400">{m.employeeCode}</td>
                  <td className="p-3">
                    <div className="flex flex-wrap gap-1">
                      {m.missingDocumentTypes.map(docType => (
                        <span key={docType} className="rounded-full bg-rose-50 px-2 py-0.5 text-xs font-medium text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">{t(docType)}</span>
                      ))}
                    </div>
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

// ── Compliance AI Tab ─────────────────────────────────────────────────────────

function ComplianceAITab() {
  const { t } = useLocale();
  const fx = useFormat();
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
    Critical: 'border-s-rose-600 bg-rose-50 dark:bg-rose-500/5',
    High: 'border-s-rose-500 bg-rose-50 dark:bg-rose-500/5',
    Medium: 'border-s-amber-500 bg-amber-50 dark:bg-amber-500/5',
    Low: 'border-s-blue-400 bg-blue-50 dark:bg-blue-500/5',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5 px-4 py-3 text-xs text-amber-700 dark:text-amber-400 flex-1 me-3">
          <Lightbulb className="inline h-3.5 w-3.5 me-1.5" />
          {t('All compliance insights are advisory only and must not automatically approve compliance, reject employees, or make legal decisions.')}
          {genTime && <span className="ms-2 opacity-60">{t('Generated: {time}', { time: fx.dateTime(genTime) })}</span>}
        </div>
        <button type="button" onClick={load} className="shrink-0 flex items-center gap-1.5 rounded-lg border border-slate-200 dark:border-white/10 px-3 py-1.5 text-xs text-slate-600 dark:text-slate-400 hover:bg-slate-50 dark:hover:bg-white/5">
          <RefreshCw className="h-3.5 w-3.5" /> {t('Refresh')}
        </button>
      </div>

      {loading ? <p className="py-8 text-center text-sm text-slate-400">{t('Generating compliance insights…')}</p> : insights.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">{t('No insights available.')}</p>
      ) : (
        <div className="space-y-3">
          {insights.map((ins, i) => (
            <div key={i} className={`rounded-xl border-s-4 p-4 ${SEV_COLORS[ins.severity] ?? 'border-s-slate-400 bg-slate-50'}`}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 mb-1">
                    <p className="text-sm font-semibold text-slate-900 dark:text-slate-100">{ins.title}</p>
                    <span className="text-xs font-medium text-amber-600 dark:text-amber-400 border border-amber-300 dark:border-amber-600 rounded px-1.5 py-0.5">{t('Advisory')}</span>
                  </div>
                  <p className="text-xs text-slate-600 dark:text-slate-400">{ins.description}</p>
                </div>
                <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${
                  ins.severity === 'Critical' || ins.severity === 'High' ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400' :
                  ins.severity === 'Medium' ? 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400' :
                  'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-400'
                }`}>{enumLabel(t, 'Status', ins.severity)}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'contracts' | 'visa' | 'passports' | 'renewals' | 'expiry' | 'employee-documents' | 'ai' | 'jawazat';

const TAB_KEYS: Tab[] = ['dashboard', 'contracts', 'visa', 'passports', 'renewals', 'expiry', 'employee-documents', 'ai', 'jawazat'];

export default function CompliancePage() {
  const { t } = useLocale();
  const params = useSearchParams();
  const requested = params.get('tab');
  const [tab, setTab] = useState<Tab>(
    requested && (TAB_KEYS as string[]).includes(requested) ? (requested as Tab) : 'dashboard',
  );

  const tabs: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
    { id: 'dashboard', label: 'Dashboard', icon: BarChart3 },
    { id: 'contracts', label: 'Contracts', icon: FileText },
    { id: 'visa', label: 'Visa & ID', icon: Globe },
    { id: 'jawazat', label: 'Jawazat', icon: Globe },
    { id: 'renewals', label: 'Renewals', icon: RefreshCw },
    { id: 'expiry', label: 'Expiry Alerts', icon: AlertTriangle },
    { id: 'employee-documents', label: 'Employee Documents', icon: FileWarning },
    { id: 'ai', label: 'Insights', icon: Lightbulb },
  ];

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div>
        <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Document & Compliance Management')}</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          {t('Manage contracts, visas, passports, work permits, and GCC compliance documentation.')}
        </p>
      </div>

      <div className="flex gap-1 flex-wrap rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]" style={{ width: 'fit-content', maxWidth: '100%' }}>
        {tabs.map(tabItem => (
          <button
            key={tabItem.id}
            type="button"
            onClick={() => setTab(tabItem.id)}
            className={`flex items-center gap-1.5 rounded-lg px-4 py-1.5 text-sm font-medium transition ${
              tab === tabItem.id || (tabItem.id === 'visa' && tab === 'passports')
                ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent'
                : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
            }`}
          >
            <tabItem.icon className="h-3.5 w-3.5" />
            {t(tabItem.label)}
          </button>
        ))}
      </div>

      {tab === 'dashboard' && <DashboardTab onNavigate={setTab} />}
      {tab === 'contracts' && <ContractsTab />}
      {/* The passport KPIs drill down to 'passports', which used to render nothing at all. */}
      {(tab === 'visa' || tab === 'passports') && <VisaPassportTab key={tab} initialSubTab={tab === 'passports' ? 'passport' : 'visa'} />}
      {tab === 'jawazat' && <JawazatPanel />}
      {tab === 'renewals' && <RenewalsTab />}
      {tab === 'expiry' && <ExpiryAlertsTab />}
      {tab === 'employee-documents' && <EmployeeDocumentsTab />}
      {tab === 'ai' && <ComplianceAITab />}
    </div>
  );
}
