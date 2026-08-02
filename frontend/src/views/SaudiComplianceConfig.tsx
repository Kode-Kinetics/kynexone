'use client';

import { useCallback, useEffect, useState } from 'react';
import {
  CheckCircle, XCircle, RefreshCw, Save, Wifi, WifiOff, Eye, EyeOff,
  ShieldCheck, Banknote, Users, Clock, FileText,
} from 'lucide-react';
import client from '../api/client';
import { companiesApi } from '../api/organization';
import { gccSettingsApi } from '../api/setup';
import type { CompanyDto, CompanyRequest } from '../api/organization';
import type { GCCComplianceSetting } from '../api/setup';

function toRequest(c: CompanyDto, patch: Partial<CompanyRequest>): CompanyRequest {
  return {
    legalNameEn: c.legalNameEn,
    legalNameAr: c.legalNameAr,
    tradeName: c.tradeName,
    countryCode: c.countryCode,
    registrationNumber: c.registrationNumber,
    taxNumber: c.taxNumber,
    wpsEmployerId: c.wpsEmployerId,
    gosiEmployerId: c.gosiEmployerId,
    qiwaEstablishmentId: c.qiwaEstablishmentId,
    defaultCurrency: c.defaultCurrency,
    emailDomain: c.emailDomain,
    workEmailPattern: c.workEmailPattern,
    isActive: c.isActive,
    ...patch,
  };
}

// ── Qiwa API ──────────────────────────────────────────────────────────────────

interface QiwaConnection {
  id?: string;
  establishmentId: string;
  establishmentName: string;
  unifiedOrganisationNumber: string;
  environment: string;
  status: string;
  lastConnectedAtUtc?: string;
  lastCheckedAtUtc?: string;
  configured: boolean;
  hasError: boolean;
  lastErrorMessage?: string;
}

const qiwaApi = {
  getConnection: () =>
    client.get<QiwaConnection>('/api/qiwa/connection').then(r => r.data),

  upsertConnection: (body: { establishmentId: string; environment: string; unifiedOrganisationNumber?: string }) =>
    client.put<{ id: string; establishmentId: string; environment: string; status: string }>('/api/qiwa/connection', body).then(r => r.data),

  saveCredentials: (body: { clientId: string; clientSecret: string; environment: string }) =>
    client.put<{ configured: boolean; environment: string; message: string }>('/api/qiwa/credentials', body).then(r => r.data),

  getReadinessSummary: () =>
    client.get<{ totalEmployees: number; readyForSync: number; blockedFromSync: number; readinessPercent: number }>('/api/qiwa/readiness-summary').then(r => r.data),
};

// ── Helpers ───────────────────────────────────────────────────────────────────

function Section({ title, icon: Icon, badge, children }: {
  title: string;
  icon: React.ElementType;
  badge?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center gap-3 border-b border-slate-100 px-5 py-4 dark:border-white/[0.07]">
        <span className="grid h-8 w-8 place-items-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
          <Icon className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        </span>
        <div className="flex-1">
          <h3 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h3>
        </div>
        {badge}
      </div>
      <div className="p-5">{children}</div>
    </div>
  );
}

function Field({ label, hint, required, children }: { label: string; hint?: string; required?: boolean; children: React.ReactNode }) {
  return (
    <div>
      <label className="mb-1.5 block text-sm font-semibold text-slate-700 dark:text-slate-300">
        {label}{required && <span className="ml-0.5 text-red-500">*</span>}
      </label>
      {children}
      {hint && <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const map: Record<string, string> = {
    Connected: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    Disconnected: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
    NotConfigured: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
    Error: 'bg-red-100 text-red-700 dark:bg-red-500/20 dark:text-red-400',
    ApiError: 'bg-red-100 text-red-700 dark:bg-red-500/20 dark:text-red-400',
    ConfigurationError: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
  };
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-semibold ${map[status] ?? 'bg-slate-100 text-slate-500'}`}>
      {status === 'Connected' ? <CheckCircle className="h-3 w-3" /> : <WifiOff className="h-3 w-3" />}
      {status}
    </span>
  );
}

function SaveBanner({ message, isError }: { message: string; isError?: boolean }) {
  if (!message) return null;
  return (
    <div className={`flex items-center gap-2 rounded-lg px-3 py-2.5 text-sm ${isError ? 'bg-red-50 text-red-600 dark:bg-red-500/10 dark:text-red-400' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400'}`}>
      {isError ? <XCircle className="h-4 w-4 shrink-0" /> : <CheckCircle className="h-4 w-4 shrink-0" />}
      {message}
    </div>
  );
}

// ── QIWA Config Panel ─────────────────────────────────────────────────────────

function QiwaPanel() {
  const [conn, setConn] = useState<QiwaConnection | null>(null);
  const [readiness, setReadiness] = useState<{ totalEmployees: number; readyForSync: number; blockedFromSync: number; readinessPercent: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [connForm, setConnForm] = useState({ establishmentId: '', unifiedOrganisationNumber: '', environment: 'sandbox' });
  const [credForm, setCredForm] = useState({ clientId: '', clientSecret: '', environment: 'sandbox' });
  const [showSecret, setShowSecret] = useState(false);
  const [savingConn, setSavingConn] = useState(false);
  const [savingCred, setSavingCred] = useState(false);
  const [connMsg, setConnMsg] = useState('');
  const [credMsg, setCredMsg] = useState('');
  const [connErr, setConnErr] = useState(false);
  const [credErr, setCredErr] = useState(false);

  useEffect(() => {
    Promise.all([qiwaApi.getConnection(), qiwaApi.getReadinessSummary()])
      .then(([c, r]) => {
        setConn(c);
        setConnForm({ establishmentId: c.establishmentId ?? '', unifiedOrganisationNumber: c.unifiedOrganisationNumber ?? '', environment: c.environment ?? 'sandbox' });
        setCredForm(f => ({ ...f, environment: c.environment ?? 'sandbox' }));
        setReadiness(r);
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const saveConn = async () => {
    if (!connForm.establishmentId.trim()) { setConnMsg('Establishment ID is required.'); setConnErr(true); return; }
    setSavingConn(true); setConnMsg(''); setConnErr(false);
    try {
      await qiwaApi.upsertConnection(connForm);
      setConnMsg('Connection settings saved successfully.');
      const c = await qiwaApi.getConnection(); setConn(c);
    } catch (e: any) {
      setConnMsg(e?.response?.data?.message ?? 'Failed to save connection settings.');
      setConnErr(true);
    } finally { setSavingConn(false); }
  };

  const saveCred = async () => {
    if (!credForm.clientId.trim() || !credForm.clientSecret.trim()) { setCredMsg('Client ID and Client Secret are required.'); setCredErr(true); return; }
    setSavingCred(true); setCredMsg(''); setCredErr(false);
    try {
      await qiwaApi.saveCredentials(credForm);
      setCredMsg('OAuth2 credentials saved and encrypted. Secret is not stored in plaintext.');
      setCredForm(f => ({ ...f, clientSecret: '' }));
    } catch (e: any) {
      setCredMsg(e?.response?.data?.message ?? 'Failed to save credentials.');
      setCredErr(true);
    } finally { setSavingCred(false); }
  };

  const envOptions = ['sandbox', 'production'];

  if (loading) {
    return <Section title="QIWA Integration" icon={Wifi}><div className="h-32 animate-pulse rounded-lg bg-slate-100 dark:bg-white/[0.05]" /></Section>;
  }

  return (
    <Section
      title="QIWA Integration"
      icon={Wifi}
      badge={conn ? <StatusBadge status={conn.status} /> : <StatusBadge status="NotConfigured" />}
    >
      <div className="space-y-6">
        {/* Readiness summary */}
        {readiness && (
          <div className="grid grid-cols-3 gap-3">
            <div className="rounded-lg border border-slate-100 p-3 dark:border-white/[0.07]">
              <p className="text-xs text-slate-400">Total Employees</p>
              <p className="mt-0.5 text-lg font-bold text-slate-900 dark:text-white">{readiness.totalEmployees}</p>
            </div>
            <div className="rounded-lg border border-emerald-100 p-3 dark:border-emerald-900/30">
              <p className="text-xs text-slate-400">Ready for Sync</p>
              <p className="mt-0.5 text-lg font-bold text-emerald-600 dark:text-emerald-400">{readiness.readyForSync}</p>
            </div>
            <div className="rounded-lg border border-red-100 p-3 dark:border-red-900/30">
              <p className="text-xs text-slate-400">Blocked</p>
              <p className="mt-0.5 text-lg font-bold text-red-600 dark:text-red-400">{readiness.blockedFromSync}</p>
            </div>
          </div>
        )}

        {/* Establishment Settings */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">Establishment Settings</h4>
          <div className="grid grid-cols-2 gap-4">
            <Field label="Establishment ID" required hint="MOL-issued establishment number (e.g. 1234567890)">
              <input
                value={connForm.establishmentId}
                onChange={e => setConnForm(x => ({ ...x, establishmentId: e.target.value }))}
                className="input w-full font-mono"
                placeholder="1234567890"
                title="Establishment ID"
              />
            </Field>
            <Field label="Unified Organisation Number" hint="Optional — fill from Qiwa portal if available">
              <input
                value={connForm.unifiedOrganisationNumber}
                onChange={e => setConnForm(x => ({ ...x, unifiedOrganisationNumber: e.target.value }))}
                className="input w-full font-mono"
                placeholder="7001234567"
                title="Unified Organisation Number"
              />
            </Field>
            <Field label="API Environment">
              <div className="flex gap-2">
                {envOptions.map(opt => (
                  <button
                    key={opt}
                    type="button"
                    onClick={() => setConnForm(x => ({ ...x, environment: opt }))}
                    className={`flex-1 rounded-lg border px-3 py-2 text-sm font-semibold capitalize transition ${connForm.environment === opt ? (opt === 'production' ? 'border-emerald-500 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400' : 'border-amber-400 bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400') : 'border-slate-200 text-slate-500 dark:border-white/10'}`}
                  >
                    {opt === 'production' ? '🟢 Production' : '🧪 Sandbox'}
                  </button>
                ))}
              </div>
            </Field>
            {conn?.lastConnectedAtUtc && (
              <Field label="Last Connected">
                <div className="input w-full bg-slate-50 text-slate-500 dark:bg-white/[0.04]">{new Date(conn.lastConnectedAtUtc).toLocaleString()}</div>
              </Field>
            )}
          </div>
          {conn?.hasError && conn.lastErrorMessage && (
            <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2.5 text-xs text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-400">
              <span className="font-semibold">Last error:</span> {conn.lastErrorMessage}
            </div>
          )}
          <div className="mt-3 flex items-center gap-3">
            <SaveBanner message={connMsg} isError={connErr} />
            <button type="button" onClick={saveConn} disabled={savingConn} className="ml-auto btn-primary disabled:opacity-60">
              {savingConn ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><Save className="h-3.5 w-3.5" /> Save Connection</>}
            </button>
          </div>
        </div>

        {/* OAuth2 Credentials */}
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/10">
          <h4 className="mb-1 flex items-center gap-2 text-xs font-bold uppercase tracking-wide text-amber-700 dark:text-amber-400">
            <ShieldCheck className="h-3.5 w-3.5" /> OAuth2 API Credentials
          </h4>
          <p className="mb-3 text-xs text-amber-600 dark:text-amber-300">
            Client Secret is encrypted at rest and never returned in API responses. Obtained from the Qiwa Developer Portal.
          </p>
          <div className="grid grid-cols-2 gap-4">
            <Field label="Client ID" required hint="From Qiwa Developer Portal → My Apps → Client ID">
              <input
                value={credForm.clientId}
                onChange={e => setCredForm(x => ({ ...x, clientId: e.target.value }))}
                className="input w-full font-mono"
                placeholder="qiwa-client-xxxx"
                title="Qiwa Client ID"
              />
            </Field>
            <Field label="Client Secret" required hint="Leave blank to keep existing secret">
              <div className="relative">
                <input
                  type={showSecret ? 'text' : 'password'}
                  value={credForm.clientSecret}
                  onChange={e => setCredForm(x => ({ ...x, clientSecret: e.target.value }))}
                  className="input w-full pr-10 font-mono"
                  placeholder="Enter new secret to update"
                  title="Qiwa Client Secret"
                />
                <button type="button" onClick={() => setShowSecret(s => !s)} className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 dark:hover:text-slate-300" title={showSecret ? 'Hide' : 'Show'}>
                  {showSecret ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
              </div>
            </Field>
            <Field label="Credential Environment">
              <div className="flex gap-2">
                {envOptions.map(opt => (
                  <button
                    key={opt}
                    type="button"
                    onClick={() => setCredForm(x => ({ ...x, environment: opt }))}
                    className={`flex-1 rounded-lg border px-3 py-2 text-sm font-semibold capitalize transition ${credForm.environment === opt ? (opt === 'production' ? 'border-emerald-500 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400' : 'border-amber-400 bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400') : 'border-slate-200 text-slate-500 dark:border-white/10'}`}
                  >
                    {opt === 'production' ? '🟢 Production' : '🧪 Sandbox'}
                  </button>
                ))}
              </div>
            </Field>
          </div>
          <div className="mt-3 flex items-center gap-3">
            <SaveBanner message={credMsg} isError={credErr} />
            <button type="button" onClick={saveCred} disabled={savingCred || !credForm.clientSecret} className="ml-auto btn-primary disabled:opacity-60">
              {savingCred ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><ShieldCheck className="h-3.5 w-3.5" /> Save Credentials</>}
            </button>
          </div>
        </div>
      </div>
    </Section>
  );
}

// ── GOSI Config Panel ─────────────────────────────────────────────────────────

function GosiPanel({ company, onCompanyUpdate }: { company: CompanyDto | null; onCompanyUpdate: (c: CompanyDto) => void }) {
  const [form, setForm] = useState({ gosiEmployerId: '' });
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');
  const [isErr, setIsErr] = useState(false);

  useEffect(() => {
    if (company) setForm({ gosiEmployerId: company.gosiEmployerId ?? '' });
  }, [company]);

  const save = async () => {
    if (!company) return;
    setSaving(true); setMsg(''); setIsErr(false);
    try {
      const updated = await companiesApi.update(company.id, toRequest(company, { gosiEmployerId: form.gosiEmployerId }));
      onCompanyUpdate(updated);
      setMsg('GOSI employer ID saved.');
    } catch {
      setMsg('Failed to save GOSI settings.'); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title="GOSI (General Organisation for Social Insurance)" icon={Users}>
      <div className="space-y-4">
        <div className="rounded-lg border border-blue-100 bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:border-blue-900/30 dark:bg-blue-900/10 dark:text-blue-300">
          <p className="font-semibold mb-1">Saudi GOSI Contribution Rates (Illustrative)</p>
          <div className="grid grid-cols-2 gap-x-6 gap-y-0.5 mt-1">
            <span>Saudi Employee: 10% of salary (employee share)</span>
            <span>Saudi Employer: 12% of salary (employer share)</span>
            <span>Non-Saudi Employee: 0%</span>
            <span>Non-Saudi Employer: 2% (occupational hazard)</span>
          </div>
          <p className="mt-1.5 text-blue-500 dark:text-blue-400">Always verify current rates at portal.gosi.gov.sa before payroll processing.</p>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label="GOSI Employer ID" hint="Issued by GOSI when registering your establishment (e.g. 10000000001)">
            <input
              value={form.gosiEmployerId}
              onChange={e => setForm({ gosiEmployerId: e.target.value })}
              className="input w-full font-mono"
              placeholder="10000000001"
              title="GOSI Employer ID"
            />
          </Field>
          <Field label="Company Legal Name">
            <div className="input w-full bg-slate-50 text-slate-500 dark:bg-white/[0.04]">{company?.legalNameEn ?? '—'}</div>
          </Field>
        </div>

        <div className="rounded-lg border border-slate-100 p-3 dark:border-white/[0.07]">
          <p className="text-xs font-semibold text-slate-600 dark:text-slate-300 mb-1">Per-Employee GOSI Reference</p>
          <p className="text-xs text-slate-400">
            Each employee needs a personal GOSI reference number (National ID / Iqama number).
            Set it from <strong>People → Employee Profile → Compliance Fields</strong>.
            Missing references are flagged in the GOSI section of the Compliance Dashboard.
          </p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving || !company} className="ml-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><Save className="h-3.5 w-3.5" /> Save GOSI Settings</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── WPS Config Panel ──────────────────────────────────────────────────────────

function WpsPanel({ company, onCompanyUpdate, gcc, onGccUpdate }: {
  company: CompanyDto | null;
  onCompanyUpdate: (c: CompanyDto) => void;
  gcc: GCCComplianceSetting | null;
  onGccUpdate: (g: GCCComplianceSetting) => void;
}) {
  const [form, setForm] = useState({
    wpsEmployerId: '',
    wpsEnabled: true,
    wpsAgentId: '',
    wpsMolCode: '',
    sifEnabled: true,
  });
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');
  const [isErr, setIsErr] = useState(false);

  useEffect(() => {
    setForm({
      wpsEmployerId: company?.wpsEmployerId ?? '',
      wpsEnabled: gcc?.wpsEnabled ?? true,
      wpsAgentId: gcc?.wpsAgentId ?? '',
      wpsMolCode: gcc?.wpsMolCode ?? '',
      sifEnabled: gcc?.sifEnabled ?? true,
    });
  }, [company, gcc]);

  const save = async () => {
    if (!company) return;
    setSaving(true); setMsg(''); setIsErr(false);
    try {
      const [updatedCompany, updatedGcc] = await Promise.all([
        companiesApi.update(company.id, toRequest(company, { wpsEmployerId: form.wpsEmployerId })),
        gccSettingsApi.upsert({
          countryCode: 'SA',
          wpsEnabled: form.wpsEnabled,
          wpsAgentId: form.wpsAgentId,
          wpsMolCode: form.wpsMolCode,
          sifEnabled: form.sifEnabled,
          eosbEnabled: gcc?.eosbEnabled ?? false,
          eosbYears1To5Rate: gcc?.eosbYears1To5Rate ?? 21,
          eosbYearsAbove5Rate: gcc?.eosbYearsAbove5Rate ?? 30,
          eosbMinYears: gcc?.eosbMinYears ?? 1,
          workWeek: gcc?.workWeek ?? 'SunThu',
          weekendDays: gcc?.weekendDays ?? 'FriSat',
          visaTrackingEnabled: gcc?.visaTrackingEnabled ?? true,
          visaAlertDays: gcc?.visaAlertDays ?? 60,
          iqamaRequired: gcc?.iqamaRequired ?? true,
          iqamaAlertDays: gcc?.iqamaAlertDays ?? 30,
          emiratesIdRequired: gcc?.emiratesIdRequired ?? false,
          ramadanHoursEnabled: gcc?.ramadanHoursEnabled ?? false,
          ramadanReducedHoursPerDay: gcc?.ramadanReducedHoursPerDay ?? 2,
        }),
      ]);
      onCompanyUpdate(updatedCompany);
      onGccUpdate(updatedGcc);
      setMsg('WPS settings saved successfully.');
    } catch {
      setMsg('Failed to save WPS settings.'); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title="WPS (Wage Protection System)" icon={Banknote}>
      <div className="space-y-4">
        <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300">
          <p className="font-semibold">Saudi WPS Requirements (SAMA)</p>
          <ul className="mt-1 list-disc list-inside space-y-0.5 text-slate-500 dark:text-slate-400">
            <li>All wages must be paid by the 10th of the following month</li>
            <li>Salaries ≥ SAR 500/month must be paid through WPS-registered banks</li>
            <li>SIF (Salary Information File) must be submitted before each payroll run</li>
            <li>MOL Code (Ministry of Labour) identifies your establishment with SAMA</li>
          </ul>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label="WPS Employer ID" hint="Your company's WPS-registered bank employer identifier">
            <input
              value={form.wpsEmployerId}
              onChange={e => setForm(x => ({ ...x, wpsEmployerId: e.target.value }))}
              className="input w-full font-mono"
              placeholder="SA1234567890"
              title="WPS Employer ID"
            />
          </Field>
          <Field label="WPS Agent ID" hint="10-character agent ID assigned by your WPS bank (zero-padded)">
            <input
              value={form.wpsAgentId}
              onChange={e => setForm(x => ({ ...x, wpsAgentId: e.target.value }))}
              className="input w-full font-mono"
              placeholder="0000000000"
              maxLength={10}
              title="WPS Agent ID"
            />
          </Field>
          <Field label="MOL Code" hint="7-character Ministry of Labour establishment code">
            <input
              value={form.wpsMolCode}
              onChange={e => setForm(x => ({ ...x, wpsMolCode: e.target.value }))}
              className="input w-full font-mono"
              placeholder="0000000"
              maxLength={7}
              title="MOL Code"
            />
          </Field>
        </div>

        <div className="flex flex-wrap gap-6">
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.wpsEnabled} onChange={e => setForm(x => ({ ...x, wpsEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Enable WPS" />
            Enable WPS enforcement
          </label>
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.sifEnabled} onChange={e => setForm(x => ({ ...x, sifEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Enable SIF Export" />
            Enable SIF (Salary Information File) export
          </label>
        </div>

        <div className="rounded-lg border border-blue-100 bg-blue-50 px-4 py-3 text-xs dark:border-blue-900/30 dark:bg-blue-900/10">
          <p className="font-semibold text-blue-700 dark:text-blue-300">Employee IBAN Requirements</p>
          <p className="text-blue-600 dark:text-blue-400 mt-0.5">
            Each employee must have a valid Saudi IBAN (SA + 22 digits) in their payroll profile.
            Missing or invalid IBANs are automatically flagged in the WPS compliance section.
            Set from <strong>People → Employee → Payroll Profile → Payment Details</strong>.
          </p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving || !company} className="ml-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><Save className="h-3.5 w-3.5" /> Save WPS Settings</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Labor & EOSB Config Panel ─────────────────────────────────────────────────

function LaborPanel({ gcc, onGccUpdate }: { gcc: GCCComplianceSetting | null; onGccUpdate: (g: GCCComplianceSetting) => void }) {
  const [form, setForm] = useState({
    workWeek: 'SunThu',
    weekendDays: 'FriSat',
    eosbEnabled: true,
    eosbYears1To5Rate: 21,
    eosbYearsAbove5Rate: 30,
    eosbMinYears: 1,
    ramadanHoursEnabled: false,
    ramadanReducedHoursPerDay: 2,
  });
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');
  const [isErr, setIsErr] = useState(false);

  useEffect(() => {
    if (gcc) setForm({
      workWeek: gcc.workWeek ?? 'SunThu',
      weekendDays: gcc.weekendDays ?? 'FriSat',
      eosbEnabled: gcc.eosbEnabled,
      eosbYears1To5Rate: gcc.eosbYears1To5Rate ?? 21,
      eosbYearsAbove5Rate: gcc.eosbYearsAbove5Rate ?? 30,
      eosbMinYears: gcc.eosbMinYears ?? 1,
      ramadanHoursEnabled: gcc.ramadanHoursEnabled,
      ramadanReducedHoursPerDay: gcc.ramadanReducedHoursPerDay ?? 2,
    });
  }, [gcc]);

  const save = async () => {
    setSaving(true); setMsg(''); setIsErr(false);
    try {
      const updated = await gccSettingsApi.upsert({
        countryCode: 'SA',
        ...form,
        wpsEnabled: gcc?.wpsEnabled ?? true,
        wpsAgentId: gcc?.wpsAgentId ?? '',
        wpsMolCode: gcc?.wpsMolCode ?? '',
        sifEnabled: gcc?.sifEnabled ?? true,
        visaTrackingEnabled: gcc?.visaTrackingEnabled ?? true,
        visaAlertDays: gcc?.visaAlertDays ?? 60,
        iqamaRequired: gcc?.iqamaRequired ?? true,
        iqamaAlertDays: gcc?.iqamaAlertDays ?? 30,
        emiratesIdRequired: gcc?.emiratesIdRequired ?? false,
      });
      onGccUpdate(updated);
      setMsg('Labor and EOSB settings saved.');
    } catch {
      setMsg('Failed to save settings.'); setIsErr(true);
    } finally { setSaving(false); }
  };

  const workWeekOptions = ['SunThu', 'MonFri', 'SatWed', 'MonSat'];
  const weekendOptions = ['FriSat', 'SatSun', 'Fri', 'Sat'];

  return (
    <Section title="Labor Law & EOSB Settings" icon={Clock}>
      <div className="space-y-5">
        {/* Work week */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">Work Schedule</h4>
          <div className="grid grid-cols-2 gap-4">
            <Field label="Work Week" hint="Saudi Labor Law: standard work week is Sun–Thu">
              <select value={form.workWeek} onChange={e => setForm(x => ({ ...x, workWeek: e.target.value }))} className="select w-full" title="Work Week">
                {workWeekOptions.map(o => <option key={o} value={o}>{o.replace(/([A-Z])/g, ' $1').trim()}</option>)}
              </select>
            </Field>
            <Field label="Weekend Days" hint="Saudi: Friday + Saturday">
              <select value={form.weekendDays} onChange={e => setForm(x => ({ ...x, weekendDays: e.target.value }))} className="select w-full" title="Weekend Days">
                {weekendOptions.map(o => <option key={o} value={o}>{o.replace(/([A-Z])/g, ' $1').trim()}</option>)}
              </select>
            </Field>
          </div>
        </div>

        {/* EOSB */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">EOSB — End of Service Benefits</h4>
          <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300 mb-3">
            <p className="font-semibold">Saudi Labor Law Art. 84 — EOSB Formula</p>
            <ul className="mt-1 list-disc list-inside space-y-0.5 text-slate-500 dark:text-slate-400">
              <li>Years 1–5: ½ month salary per year</li>
              <li>Years 5+: 1 month salary per year</li>
              <li>Rates expressed in working days per year of service</li>
            </ul>
          </div>

          <label className="mb-3 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.eosbEnabled} onChange={e => setForm(x => ({ ...x, eosbEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Enable EOSB" />
            Enable EOSB calculation in payroll
          </label>

          {form.eosbEnabled && (
            <div className="grid grid-cols-3 gap-4">
              <Field label="Min Service Years" hint="Minimum tenure before EOSB is payable">
                <input type="number" min={0} value={form.eosbMinYears} onChange={e => setForm(x => ({ ...x, eosbMinYears: Number(e.target.value) }))} className="input w-full" title="Min Service Years" />
              </Field>
              <Field label="Rate: Years 1–5 (days/yr)" hint="Default: 21 days = ½ month">
                <input type="number" min={0} step={0.5} value={form.eosbYears1To5Rate} onChange={e => setForm(x => ({ ...x, eosbYears1To5Rate: Number(e.target.value) }))} className="input w-full" title="EOSB Rate Years 1 to 5" />
              </Field>
              <Field label="Rate: Years 5+ (days/yr)" hint="Default: 30 days = 1 month">
                <input type="number" min={0} step={0.5} value={form.eosbYearsAbove5Rate} onChange={e => setForm(x => ({ ...x, eosbYearsAbove5Rate: Number(e.target.value) }))} className="input w-full" title="EOSB Rate Years Above 5" />
              </Field>
            </div>
          )}
        </div>

        {/* Ramadan */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">Ramadan Working Hours</h4>
          <label className="mb-3 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.ramadanHoursEnabled} onChange={e => setForm(x => ({ ...x, ramadanHoursEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Enable Ramadan Hours" />
            Reduce daily working hours during Ramadan
          </label>
          {form.ramadanHoursEnabled && (
            <Field label="Reduction per day (hours)" hint="Saudi Labor Law: working hours reduced by 2h during Ramadan for Muslim employees">
              <input type="number" min={1} max={4} value={form.ramadanReducedHoursPerDay} onChange={e => setForm(x => ({ ...x, ramadanReducedHoursPerDay: Number(e.target.value) }))} className="input w-48" title="Ramadan Reduced Hours Per Day" />
            </Field>
          )}
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving} className="ml-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><Save className="h-3.5 w-3.5" /> Save Labor Settings</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Document Tracking Config Panel ────────────────────────────────────────────

function DocumentTrackingPanel({ gcc, onGccUpdate }: { gcc: GCCComplianceSetting | null; onGccUpdate: (g: GCCComplianceSetting) => void }) {
  const [form, setForm] = useState({
    visaTrackingEnabled: true,
    visaAlertDays: 60,
    iqamaRequired: true,
    iqamaAlertDays: 30,
    emiratesIdRequired: false,
  });
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');
  const [isErr, setIsErr] = useState(false);

  useEffect(() => {
    if (gcc) setForm({
      visaTrackingEnabled: gcc.visaTrackingEnabled,
      visaAlertDays: gcc.visaAlertDays ?? 60,
      iqamaRequired: gcc.iqamaRequired,
      iqamaAlertDays: gcc.iqamaAlertDays ?? 30,
      emiratesIdRequired: gcc.emiratesIdRequired,
    });
  }, [gcc]);

  const save = async () => {
    setSaving(true); setMsg(''); setIsErr(false);
    try {
      const updated = await gccSettingsApi.upsert({
        countryCode: 'SA',
        ...form,
        wpsEnabled: gcc?.wpsEnabled ?? true,
        wpsAgentId: gcc?.wpsAgentId ?? '',
        wpsMolCode: gcc?.wpsMolCode ?? '',
        sifEnabled: gcc?.sifEnabled ?? true,
        eosbEnabled: gcc?.eosbEnabled ?? true,
        eosbYears1To5Rate: gcc?.eosbYears1To5Rate ?? 21,
        eosbYearsAbove5Rate: gcc?.eosbYearsAbove5Rate ?? 30,
        eosbMinYears: gcc?.eosbMinYears ?? 1,
        workWeek: gcc?.workWeek ?? 'SunThu',
        weekendDays: gcc?.weekendDays ?? 'FriSat',
        ramadanHoursEnabled: gcc?.ramadanHoursEnabled ?? false,
        ramadanReducedHoursPerDay: gcc?.ramadanReducedHoursPerDay ?? 2,
      });
      onGccUpdate(updated);
      setMsg('Document tracking settings saved.');
    } catch {
      setMsg('Failed to save settings.'); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title="Document Tracking & Expiry Alerts" icon={FileText}>
      <div className="space-y-5">
        {/* Visa / Iqama */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">Residency Documents</h4>
          <div className="grid grid-cols-2 gap-4">
            <div className="rounded-lg border border-slate-100 p-4 dark:border-white/[0.07] space-y-3">
              <label className="flex items-center gap-2 text-sm font-semibold text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={form.visaTrackingEnabled} onChange={e => setForm(x => ({ ...x, visaTrackingEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Enable Visa Tracking" />
                Visa / Iqama Tracking
              </label>
              {form.visaTrackingEnabled && (
                <Field label="Alert Days Before Expiry" hint="Trigger renewal reminder this many days before expiry">
                  <input type="number" min={7} max={365} value={form.visaAlertDays} onChange={e => setForm(x => ({ ...x, visaAlertDays: Number(e.target.value) }))} className="input w-full" title="Visa Alert Days" />
                </Field>
              )}
            </div>

            <div className="rounded-lg border border-slate-100 p-4 dark:border-white/[0.07] space-y-3">
              <label className="flex items-center gap-2 text-sm font-semibold text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={form.iqamaRequired} onChange={e => setForm(x => ({ ...x, iqamaRequired: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Iqama Required" />
                Iqama Required for Non-Saudi
              </label>
              {form.iqamaRequired && (
                <Field label="Alert Days Before Expiry" hint="Alert HR when Iqama is expiring soon">
                  <input type="number" min={7} max={180} value={form.iqamaAlertDays} onChange={e => setForm(x => ({ ...x, iqamaAlertDays: Number(e.target.value) }))} className="input w-full" title="Iqama Alert Days" />
                </Field>
              )}
            </div>
          </div>
        </div>

        {/* GCC Docs */}
        <div>
          <h4 className="mb-3 text-xs font-bold uppercase tracking-wide text-slate-400">GCC Documents</h4>
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.emiratesIdRequired} onChange={e => setForm(x => ({ ...x, emiratesIdRequired: e.target.checked }))} className="h-4 w-4 accent-sapphire" title="Emirates ID Required" />
            Emirates ID required (for employees also working in UAE)
          </label>
        </div>

        <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300">
          <p className="font-semibold">Expiry Alerts are sent via the Notification system.</p>
          <p className="text-slate-400 mt-0.5">Configure notification channels (email, in-app) from Settings → Notifications. Alerts appear in the Compliance Dashboard and employee profile.</p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving} className="ml-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" /> Saving…</> : <><Save className="h-3.5 w-3.5" /> Save Tracking Settings</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Main SaudiComplianceConfig ────────────────────────────────────────────────

type ConfigSection = 'qiwa' | 'gosi' | 'wps' | 'labor' | 'documents';

const navItems: { id: ConfigSection; label: string; icon: React.ElementType; desc: string }[] = [
  { id: 'qiwa',      label: 'QIWA',             icon: Wifi,        desc: 'Establishment ID, OAuth2 credentials, sync environment' },
  { id: 'gosi',      label: 'GOSI',             icon: Users,       desc: 'Employer ID, contribution rates reference' },
  { id: 'wps',       label: 'WPS',              icon: Banknote,    desc: 'Wage Protection System, SIF export, MOL code' },
  { id: 'labor',     label: 'Labor & EOSB',     icon: Clock,       desc: 'Work week, EOSB rates, Ramadan hours' },
  { id: 'documents', label: 'Document Tracking', icon: FileText,   desc: 'Visa, Iqama, Emirates ID expiry alerts' },
];

export function SaudiComplianceConfig() {
  const [active, setActive] = useState<ConfigSection>('qiwa');
  const [company, setCompany] = useState<CompanyDto | null>(null);
  const [gcc, setGcc] = useState<GCCComplianceSetting | null>(null);

  const load = useCallback(async () => {
    try {
      const [compResult, gccResult] = await Promise.all([
        companiesApi.list(1, 1),
        gccSettingsApi.list('SA'),
      ]);
      if (compResult.items.length > 0) setCompany(compResult.items[0]);
      if (gccResult.length > 0) setGcc(gccResult[0]);
    } catch { /**/ }
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <div className="flex gap-5">
      {/* Left nav */}
      <nav className="w-52 shrink-0 space-y-1">
        {navItems.map(({ id, label, icon: Icon, desc }) => (
          <button
            key={id}
            type="button"
            onClick={() => setActive(id)}
            className={`w-full rounded-xl px-3 py-2.5 text-left transition ${active === id ? 'bg-sapphire/[0.08] dark:bg-cyanAccent/[0.08]' : 'hover:bg-slate-50 dark:hover:bg-white/[0.04]'}`}
          >
            <div className="flex items-center gap-2">
              <Icon className={`h-4 w-4 shrink-0 ${active === id ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-400'}`} />
              <span className={`text-sm font-semibold ${active === id ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}>{label}</span>
            </div>
            <p className="mt-0.5 pl-6 text-[11px] text-slate-400 leading-tight">{desc}</p>
          </button>
        ))}

        <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 dark:border-amber-800 dark:bg-amber-900/20 mt-4">
          <p className="text-[11px] font-semibold text-amber-700 dark:text-amber-400">Ministry of HR</p>
          <p className="text-[10px] text-amber-600 dark:text-amber-300 mt-0.5 leading-relaxed">
            All Saudi regulatory integrations must comply with HRSD, SAMA, and GOSI requirements. Verify credentials with the respective portals.
          </p>
        </div>
      </nav>

      {/* Panel */}
      <div className="min-w-0 flex-1">
        {active === 'qiwa'      && <QiwaPanel />}
        {active === 'gosi'      && <GosiPanel company={company} onCompanyUpdate={setCompany} />}
        {active === 'wps'       && <WpsPanel company={company} onCompanyUpdate={setCompany} gcc={gcc} onGccUpdate={setGcc} />}
        {active === 'labor'     && <LaborPanel gcc={gcc} onGccUpdate={setGcc} />}
        {active === 'documents' && <DocumentTrackingPanel gcc={gcc} onGccUpdate={setGcc} />}
      </div>
    </div>
  );
}
