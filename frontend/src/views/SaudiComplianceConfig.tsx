'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  CheckCircle, XCircle, RefreshCw, Save, Wifi, WifiOff, Eye, EyeOff,
  ShieldCheck, Banknote, Users, Clock, FileText,
} from 'lucide-react';
import client from '../api/client';
import { companiesApi } from '../api/organization';
import { gccSettingsApi } from '../api/setup';
import { financeRatesApi, type StatutoryRateRow } from '../api/financeRates';
import type { CompanyDto, CompanyRequest } from '../api/organization';
import type { GCCComplianceSetting } from '../api/setup';
import { isSaudiCompany, selectSaudiCompany } from '../lib/saudiCompany';
import { requestFailureReason } from '../lib/requestFailure';
import { QIWA_SIMULATED_LABEL, qiwaConnectionLabel } from '../lib/integrationDeliveryState';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';

/** Splices React nodes into a translated sentence: translate with `mark('name')` as the value, then pass the nodes. */
const mark = (name: string) => `\u0001${name}\u0001`;
function rich(text: string, nodes: Record<string, React.ReactNode>): React.ReactNode[] {
  return text.split(/\u0001(\w+)\u0001/).map((part, i) => (i % 2 === 1 ? <span key={i}>{nodes[part]}</span> : part));
}

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
  /**
   * What the RUNNING PROCESS will actually do — not what the stored `environment`
   * column says. The two used to disagree silently: a tenant row could read
   * "production" while the server had only ever run the sandbox simulator.
   */
  runtimeAdapter?: string;
  isLiveIntegration?: boolean;
  filesWithQiwa?: boolean;
  simulationNotice?: string | null;
  /** Set when a stored "production" setting cannot be honoured by this deployment. */
  configurationIgnored?: string | null;
  /** Platform-operator flag, default OFF. Without it the OAuth credential form is not rendered. */
  credentialFormEnabled?: boolean;
  /** QIWA_USE_LIVE_ADAPTER is set but no partner agreement is on file, so live calls are refused. */
  liveAdapterRefused?: boolean;
}

/** A format example, not a sentence: the client ID shape Qiwa issues. */
const QIWA_CLIENT_ID_EXAMPLE = 'qiwa-client-xxxx';

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
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h3>
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
        {label}{required && <span className="ms-0.5 text-red-500">*</span>}
      </label>
      {children}
      {hint && <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function StatusBadge({ status, live }: { status: string; live?: boolean | null }) {
  const t = useT();
  const map: Record<string, string> = {
    Connected: live === true ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400' : 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
    Simulated: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
    Disconnected: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
    NotConfigured: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
    Error: 'bg-red-100 text-red-700 dark:bg-red-500/20 dark:text-red-400',
    ApiError: 'bg-red-100 text-red-700 dark:bg-red-500/20 dark:text-red-400',
    ConfigurationError: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
  };
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-semibold ${map[status] ?? 'bg-slate-100 text-slate-500'}`}>
      {status === 'Connected' && live === true ? <CheckCircle className="h-3 w-3" /> : <WifiOff className="h-3 w-3" />}
      {t(qiwaConnectionLabel(status, live))}
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
  const t = useT();
  const fx = useFormat();
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
    if (!connForm.establishmentId.trim()) { setConnMsg(t('Establishment ID is required.')); setConnErr(true); return; }
    setSavingConn(true); setConnMsg(''); setConnErr(false);
    try {
      // Without the operator flag there is no environment choice: the data check is always 'sandbox'.
      await qiwaApi.upsertConnection({ ...connForm, environment: conn?.credentialFormEnabled === true ? connForm.environment : 'sandbox' });
      setConnMsg(t('Establishment settings saved.'));
      const c = await qiwaApi.getConnection(); setConn(c);
    } catch (e: any) {
      setConnMsg(e?.response?.data?.message ?? t('Failed to save connection settings.'));
      setConnErr(true);
    } finally { setSavingConn(false); }
  };

  const saveCred = async () => {
    if (!credForm.clientId.trim() || !credForm.clientSecret.trim()) { setCredMsg(t('Client ID and Client Secret are required.')); setCredErr(true); return; }
    setSavingCred(true); setCredMsg(''); setCredErr(false);
    try {
      await qiwaApi.saveCredentials(credForm);
      setCredMsg(t('OAuth2 credentials saved and encrypted. Secret is not stored in plaintext.'));
      setCredForm(f => ({ ...f, clientSecret: '' }));
    } catch (e: any) {
      setCredMsg(e?.response?.data?.message ?? t('Failed to save credentials.'));
      setCredErr(true);
    } finally { setSavingCred(false); }
  };

  const envOptions = ['sandbox', 'production'];

  if (loading) {
    return <Section title={t('Qiwa data check')} icon={Wifi}><div className="h-32 animate-pulse rounded-lg bg-slate-100 dark:bg-white/[0.05]" /></Section>;
  }

  // Operator flag, default OFF: no OAuth form, no production switch, no hunt for client secrets
  // until a Qiwa partner agreement exists and the platform operator turns this on.
  const credentialFormEnabled = conn?.credentialFormEnabled === true;

  return (
    <Section
      title={t('Qiwa data check')}
      icon={Wifi}
      badge={conn ? <StatusBadge status={conn.status} live={conn.isLiveIntegration} /> : <StatusBadge status="NotConfigured" />}
    >
      <div className="space-y-6">
        {/* ── What this deployment will actually do ──────────────────────────
            A mock that announces itself is defensible; one that passes for the
            real thing is not. Before this, a sandbox sync marked employees
            "Synced" and the connection "Connected" with nothing filed. */}
        {conn?.isLiveIntegration === false && (
          <div className="rounded-lg border border-amber-300 bg-amber-50 px-3.5 py-3 dark:border-amber-500/30 dark:bg-amber-500/[0.08]">
            <p className="text-sm font-semibold text-amber-900 dark:text-amber-200">
              {t(QIWA_SIMULATED_LABEL)}
            </p>
            <p className="mt-1 text-xs leading-relaxed text-amber-900/90 dark:text-amber-100/90">
              {conn.simulationNotice
                ?? t('This checks your employee records against what Qiwa requires. Nothing is sent to Qiwa or MHRSD, and no employee record is filed. Record changes in Qiwa itself.')}
            </p>
          </div>
        )}

        {conn?.configurationIgnored && (
          <div className="rounded-lg border border-red-300 bg-red-50 px-3.5 py-3 dark:border-red-700 dark:bg-red-900/20">
            <p className="text-sm font-semibold text-red-800 dark:text-red-300">
              {t('This connection is saved as “production” but cannot be honoured')}
            </p>
            <p className="mt-1 text-xs leading-relaxed text-red-800/90 dark:text-red-300/90">
              {conn.configurationIgnored}
            </p>
          </div>
        )}

        {/* Readiness summary */}
        {readiness && (
          <div className="grid grid-cols-3 gap-3">
            <div className="rounded-lg border border-slate-100 p-3 dark:border-white/[0.07]">
              <p className="text-xs text-slate-400">{t('Total Employees')}</p>
              <p className="mt-0.5 text-lg font-semibold text-slate-900 dark:text-white">{fx.integer(readiness.totalEmployees)}</p>
            </div>
            <div className="rounded-lg border border-emerald-100 p-3 dark:border-emerald-900/30">
              <p className="text-xs text-slate-400">{t('Passing the data check')}</p>
              <p className="mt-0.5 text-lg font-semibold text-emerald-600 dark:text-emerald-400">{fx.integer(readiness.readyForSync)}</p>
            </div>
            <div className="rounded-lg border border-red-100 p-3 dark:border-red-900/30">
              <p className="text-xs text-slate-400">{t('Missing Qiwa details')}</p>
              <p className="mt-0.5 text-lg font-semibold text-red-600 dark:text-red-400">{fx.integer(readiness.blockedFromSync)}</p>
            </div>
          </div>
        )}

        {/* Establishment Settings */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Establishment Settings')}</h4>
          <div className="grid grid-cols-2 gap-4">
            <Field label={t('Establishment ID')} required hint={t('MOL-issued establishment number (e.g. 1234567890)')}>
              <input
                value={connForm.establishmentId}
                onChange={e => setConnForm(x => ({ ...x, establishmentId: e.target.value }))}
                className="input w-full font-mono"
                placeholder="1234567890"
                title={t('Establishment ID')}
              />
            </Field>
            <Field label={t('Unified Organisation Number')} hint={t('Optional — fill from Qiwa portal if available')}>
              <input
                value={connForm.unifiedOrganisationNumber}
                onChange={e => setConnForm(x => ({ ...x, unifiedOrganisationNumber: e.target.value }))}
                className="input w-full font-mono"
                placeholder="7001234567"
                title={t('Unified Organisation Number')}
              />
            </Field>
            {credentialFormEnabled && <Field label={t('API Environment')}>
              <div className="flex gap-2">
                {envOptions.map(opt => (
                  <button
                    key={opt}
                    type="button"
                    onClick={() => setConnForm(x => ({ ...x, environment: opt }))}
                    className={`flex-1 rounded-lg border px-3 py-2 text-sm font-semibold capitalize transition ${connForm.environment === opt ? (opt === 'production' ? 'border-emerald-500 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400' : 'border-amber-400 bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400') : 'border-slate-200 text-slate-500 dark:border-white/10'}`}
                  >
                    {opt === 'production' ? t('Production') : t('Sandbox')}
                  </button>
                ))}
              </div>
            </Field>}
            {conn?.isLiveIntegration === true && conn?.lastConnectedAtUtc && (
              <Field label={t('Last partner API response')}>
                <div className="input w-full bg-slate-50 text-slate-500 dark:bg-white/[0.04]">{fx.dateTime(conn.lastConnectedAtUtc)}</div>
              </Field>
            )}
          </div>
          {conn?.hasError && conn.lastErrorMessage && (
            <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2.5 text-xs text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-400">
              <span className="font-semibold">{t('Last error')}:</span> {conn.lastErrorMessage}
            </div>
          )}
          <div className="mt-3 flex items-center gap-3">
            <SaveBanner message={connMsg} isError={connErr} />
            <button type="button" onClick={saveConn} disabled={savingConn} className="ms-auto btn-primary disabled:opacity-60">
              {savingConn ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><Save className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save establishment settings')}</>}
            </button>
          </div>
        </div>

        {/* OAuth2 Credentials — rendered only when the platform operator has switched the form on
            (server flag Qiwa:ShowCredentialForm, default off). */}
        {credentialFormEnabled && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/10">
          <h4 className="mb-1 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-amber-700 dark:text-amber-400">
            <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" /> {t('OAuth2 API Credentials')}
          </h4>
          <p className="mb-3 text-xs text-amber-600 dark:text-amber-300">
            {t('Only for a signed Qiwa partner integration. The client secret is encrypted at rest and never returned.')}
          </p>
          <div className="grid grid-cols-2 gap-4">
            <Field label={t('Client ID')} required>
              <input
                value={credForm.clientId}
                onChange={e => setCredForm(x => ({ ...x, clientId: e.target.value }))}
                className="input w-full font-mono"
                placeholder={QIWA_CLIENT_ID_EXAMPLE}
                title={t('Qiwa Client ID')}
              />
            </Field>
            <Field label={t('Client Secret')} required hint={t('Leave blank to keep existing secret')}>
              <div className="relative">
                <input
                  type={showSecret ? 'text' : 'password'}
                  value={credForm.clientSecret}
                  onChange={e => setCredForm(x => ({ ...x, clientSecret: e.target.value }))}
                  className="input w-full pe-10 font-mono"
                  placeholder={t('Enter new secret to update')}
                  title={t('Qiwa Client Secret')}
                />
                <button type="button" onClick={() => setShowSecret(s => !s)} className="absolute end-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 dark:hover:text-slate-300" title={showSecret ? t('Hide client secret') : t('Show client secret')} aria-label={showSecret ? t('Hide client secret') : t('Show client secret')}>
                  {showSecret ? <EyeOff className="h-4 w-4" aria-hidden="true" /> : <Eye className="h-4 w-4" aria-hidden="true" />}
                </button>
              </div>
            </Field>
            <Field label={t('Credential Environment')}>
              <div className="flex gap-2">
                {envOptions.map(opt => (
                  <button
                    key={opt}
                    type="button"
                    onClick={() => setCredForm(x => ({ ...x, environment: opt }))}
                    className={`flex-1 rounded-lg border px-3 py-2 text-sm font-semibold capitalize transition ${credForm.environment === opt ? (opt === 'production' ? 'border-emerald-500 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400' : 'border-amber-400 bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400') : 'border-slate-200 text-slate-500 dark:border-white/10'}`}
                  >
                    {opt === 'production' ? t('Production') : t('Sandbox')}
                  </button>
                ))}
              </div>
            </Field>
          </div>
          <div className="mt-3 flex items-center gap-3">
            <SaveBanner message={credMsg} isError={credErr} />
            <button type="button" onClick={saveCred} disabled={savingCred || !credForm.clientSecret} className="ms-auto btn-primary disabled:opacity-60">
              {savingCred ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save Credentials')}</>}
            </button>
          </div>
        </div>
        )}
      </div>
    </Section>
  );
}

// ── GOSI Config Panel ─────────────────────────────────────────────────────────

// The GOSI rates this screen shows are the ones payroll will actually apply. They are read from
// the effective-dated statutory rules engine — the same rows, through the same resolver, that
// KsaDeductionCalculator multiplies into the covered wage on the payslip.
//
// This panel used to print "Saudi Employee: 10% / Saudi Employer: 12%" as literals. Nothing in
// the product held those numbers: the seeded rules are 9% annuities + 0.75% SANED each side, and
// 2% occupational hazard. A third answer on a third screen is how a customer's finance team ends
// up reconciling to a figure no payslip ever used.
const GOSI_RATE_RULE_KEYS = [
  'gosi.saudi_employee_rate', 'gosi.saudi_employer_rate', 'gosi.saned_rate', 'gosi.expat_occupational_hazard_rate',
];

const GOSI_CEILING_KEY = 'gosi.covered_wage_ceiling_sar';

// Rates are stored, and calculated with, as decimal FRACTIONS of the contributory wage
// (0.09 = 9%). Percent exists only here, for reading. See StatutoryValueUnits.cs.
function asPercent(fraction: number | null | undefined, fx: ReturnType<typeof useFormat>, notConfigured: string): string {
  if (fraction === null || fraction === undefined) return notConfigured;
  return `${fx.number(fraction * 100, { maximumFractionDigits: 4 })}%`;
}

function GosiPanel({ company, onCompanyUpdate }: { company: CompanyDto | null; onCompanyUpdate: (c: CompanyDto) => void }) {
  const t = useT();
  const fx = useFormat();
  const rateRows = [
    { ruleKey: GOSI_RATE_RULE_KEYS[0], label: t('Saudi employee — Annuities'), note: t('Deducted from the employee') },
    { ruleKey: GOSI_RATE_RULE_KEYS[1], label: t('Saudi employer — Annuities'), note: t('Paid by the company') },
    { ruleKey: GOSI_RATE_RULE_KEYS[2], label: t('Saudi SANED — each side'), note: t('Charged to employee and employer alike') },
    { ruleKey: GOSI_RATE_RULE_KEYS[3], label: t('Occupational hazard — employer'), note: t('Applies to every employee, Saudi and non-Saudi') },
  ];
  const pct = (v: number | null | undefined) => asPercent(v, fx, t('Not configured'));
  const [form, setForm] = useState({ gosiEmployerId: '' });
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');
  const [isErr, setIsErr] = useState(false);
  const [rates, setRates] = useState<StatutoryRateRow[] | null>(null);
  const [ratesError, setRatesError] = useState('');

  useEffect(() => {
    if (company) setForm({ gosiEmployerId: company.gosiEmployerId ?? '' });
  }, [company]);

  useEffect(() => {
    let cancelled = false;
    if (!company) { setRates(null); return; }
    setRatesError('');
    financeRatesApi
      .listStatutory(company.id, 'SAU', 'KSA-mainland')
      .then(rows => { if (!cancelled) setRates(rows); })
      .catch(() => {
        if (cancelled) return;
        setRates([]);
        // No invented fallback. If the configured rates cannot be read, this screen says so
        // rather than showing a number payroll would not use.
        setRatesError(t('The configured GOSI rates could not be read. They are managed under Finance → Rates (statutory), and viewing them needs the payroll rates permission.'));
      });
    return () => { cancelled = true; };
  }, [company, t]);

  const rateFor = (ruleKey: string) => rates?.find(r => r.ruleKey === ruleKey) ?? null;
  const ceiling = rateFor(GOSI_CEILING_KEY);
  const employeeTotal = (() => {
    const ann = rateFor('gosi.saudi_employee_rate')?.resolvedValue;
    const saned = rateFor('gosi.saned_rate')?.resolvedValue;
    return ann !== null && ann !== undefined && saned !== null && saned !== undefined ? ann + saned : null;
  })();
  const employerTotal = (() => {
    const ann = rateFor('gosi.saudi_employer_rate')?.resolvedValue;
    const saned = rateFor('gosi.saned_rate')?.resolvedValue;
    const oh = rateFor('gosi.expat_occupational_hazard_rate')?.resolvedValue;
    return [ann, saned, oh].every(v => v !== null && v !== undefined)
      ? (ann as number) + (saned as number) + (oh as number)
      : null;
  })();

  const save = async () => {
    if (!company) return;
    setSaving(true); setMsg(''); setIsErr(false);
    try {
      const updated = await companiesApi.update(company.id, toRequest(company, { gosiEmployerId: form.gosiEmployerId }));
      onCompanyUpdate(updated);
      setMsg(t('GOSI employer ID saved.'));
    } catch {
      setMsg(t('Failed to save GOSI settings.')); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title={t('GOSI (General Organisation for Social Insurance)')} icon={Users}>
      <div className="space-y-4">
        <div className="rounded-lg border border-blue-100 bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:border-blue-900/30 dark:bg-blue-900/10 dark:text-blue-300">
          <p className="font-semibold mb-1">{t('GOSI contribution rates payroll will apply')}</p>
          <p className="mb-2 text-blue-500 dark:text-blue-400">
            {rich(t('Read from the effective-dated statutory rules — the same values the payslip and the GOSI filing use. Change them under {path}; this screen never holds its own copy.', { path: mark('path') }),
              { path: <strong>{t('Finance → Rates → Statutory')}</strong> })}
          </p>

          {ratesError ? (
            <p className="text-amber-700 dark:text-amber-400">{ratesError}</p>
          ) : rates === null ? (
            <p className="text-blue-500 dark:text-blue-400">{t('Loading configured rates…')}</p>
          ) : (
            <>
              <table className="w-full text-xs">
                <tbody>
                  {rateRows.map(({ ruleKey, label, note }) => {
                    const row = rateFor(ruleKey);
                    return (
                      <tr key={ruleKey} className="align-top">
                        <td className="py-0.5 pe-3">
                          <span className="font-medium">{label}</span>
                          <span className="ms-2 font-mono text-caption text-blue-400 dark:text-blue-500">{ruleKey}</span>
                        </td>
                        <td className="py-0.5 pe-3 text-end tabular-nums font-semibold">
                          {pct(row?.resolvedValue)}
                        </td>
                        <td className="py-0.5 text-blue-500 dark:text-blue-400">
                          {row?.isOverride ? t('Overridden for this company') : note}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
              <p className="mt-2 border-t border-blue-100 pt-1.5 dark:border-blue-900/30">
                {rich(t('Saudi employee total {rate}', { rate: mark('rate') }), { rate: <strong className="tabular-nums">{pct(employeeTotal)}</strong> })}
                {' · '}{rich(t('Saudi employer total {rate}', { rate: mark('rate') }), { rate: <strong className="tabular-nums">{pct(employerTotal)}</strong> })}
                {' · '}{t('Non-Saudi employee 0%')}
                {ceiling?.resolvedValue != null && (
                  <> {' · '}{rich(t('Contributory wage capped at {amount}', { amount: mark('amount') }), { amount: <strong className="tabular-nums">{t('SAR {amount}', { amount: fx.number(ceiling.resolvedValue) })}</strong> })}</>
                )}
              </p>
            </>
          )}

          <p className="mt-1.5 text-blue-500 dark:text-blue-400">{t('Always verify current rates at portal.gosi.gov.sa before payroll processing.')}</p>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label={t('GOSI Employer ID')} hint={t('Issued by GOSI when registering your establishment (e.g. 10000000001)')}>
            <input
              value={form.gosiEmployerId}
              onChange={e => setForm({ gosiEmployerId: e.target.value })}
              className="input w-full font-mono"
              placeholder="10000000001"
              title={t('GOSI Employer ID')}
            />
          </Field>
          <Field label={t('Company Legal Name')}>
            <div className="input w-full bg-slate-50 text-slate-500 dark:bg-white/[0.04]">{company?.legalNameEn ?? '—'}</div>
          </Field>
        </div>

        <div className="rounded-lg border border-slate-100 p-3 dark:border-white/[0.07]">
          <p className="text-xs font-semibold text-slate-600 dark:text-slate-300 mb-1">{t('Per-Employee GOSI Reference')}</p>
          <p className="text-xs text-slate-400">
            {rich(t('Each employee needs a personal GOSI reference number (National ID / Iqama number). Set it from {path}. Missing references are flagged in the GOSI section of the Compliance Dashboard.', { path: mark('path') }),
              { path: <strong>{t('People → Employee Profile → Compliance Fields')}</strong> })}
          </p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving || !company} className="ms-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><Save className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save GOSI Settings')}</>}
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
  const t = useT();
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
      setMsg(t('WPS settings saved successfully.'));
    } catch {
      setMsg(t('Failed to save WPS settings.')); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title={t('WPS (Wage Protection System)')} icon={Banknote}>
      <div className="space-y-4">
        <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300">
          <p className="font-semibold">{t('Saudi WPS Requirements (SAMA)')}</p>
          <ul className="mt-1 list-disc list-inside space-y-0.5 text-slate-500 dark:text-slate-400">
            <li>{t('All wages must be paid by the 10th of the following month')}</li>
            <li>{t('Salaries ≥ SAR 500/month must be paid through WPS-registered banks')}</li>
            <li>{t('SIF (Salary Information File) must be submitted before each payroll run')}</li>
            <li>{t('MOL Code (Ministry of Labour) identifies your establishment with SAMA')}</li>
          </ul>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label={t('WPS Employer ID')} hint={t('Your company\'s WPS-registered bank employer identifier')}>
            <input
              value={form.wpsEmployerId}
              onChange={e => setForm(x => ({ ...x, wpsEmployerId: e.target.value }))}
              className="input w-full font-mono"
              placeholder="SA1234567890"
              title={t('WPS Employer ID')}
            />
          </Field>
          <Field label={t('WPS Agent ID')} hint={t('10-character agent ID assigned by your WPS bank (zero-padded)')}>
            <input
              value={form.wpsAgentId}
              onChange={e => setForm(x => ({ ...x, wpsAgentId: e.target.value }))}
              className="input w-full font-mono"
              placeholder={t('From your WPS bank')}
              maxLength={10}
              title={t('WPS Agent ID')}
            />
          </Field>
          <Field label={t('MOL Code')} hint={t('7-character Ministry of Labour establishment code')}>
            <input
              value={form.wpsMolCode}
              onChange={e => setForm(x => ({ ...x, wpsMolCode: e.target.value }))}
              className="input w-full font-mono"
              placeholder="0000000"
              maxLength={7}
              title={t('MOL Code')}
            />
          </Field>
        </div>

        <div className="flex flex-wrap gap-6">
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.wpsEnabled} onChange={e => setForm(x => ({ ...x, wpsEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Enable WPS')} />
            {t('Enable WPS enforcement')}
          </label>
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.sifEnabled} onChange={e => setForm(x => ({ ...x, sifEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Enable SIF Export')} />
            {t('Enable SIF (Salary Information File) export')}
          </label>
        </div>

        <div className="rounded-lg border border-blue-100 bg-blue-50 px-4 py-3 text-xs dark:border-blue-900/30 dark:bg-blue-900/10">
          <p className="font-semibold text-blue-700 dark:text-blue-300">{t('Employee IBAN Requirements')}</p>
          <p className="text-blue-600 dark:text-blue-400 mt-0.5">
            {rich(t('Each employee must have a valid Saudi IBAN (SA + 22 digits) in their payroll profile. Missing or invalid IBANs are automatically flagged in the WPS compliance section. Set from {path}.', { path: mark('path') }),
              { path: <strong>{t('People → Employee → Payroll Profile → Payment Details')}</strong> })}
          </p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving || !company} className="ms-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><Save className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save WPS Settings')}</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Labor & EOSB Config Panel ─────────────────────────────────────────────────

function LaborPanel({ gcc, onGccUpdate }: { gcc: GCCComplianceSetting | null; onGccUpdate: (g: GCCComplianceSetting) => void }) {
  const t = useT();
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
      setMsg(t('Labor and EOSB settings saved.'));
    } catch {
      setMsg(t('Failed to save settings.')); setIsErr(true);
    } finally { setSaving(false); }
  };

  const workWeekOptions = ['SunThu', 'MonFri', 'SatWed', 'MonSat'];
  const weekendOptions = ['FriSat', 'SatSun', 'Fri', 'Sat'];
  const workWeekLabel = (o: string) => ({
    SunThu: t('Sun–Thu'), MonFri: t('Mon–Fri'), SatWed: t('Sat–Wed'), MonSat: t('Mon–Sat'),
  } as Record<string, string>)[o] ?? o;
  const weekendLabel = (o: string) => ({
    FriSat: t('Fri + Sat'), SatSun: t('Sat + Sun'), Fri: t('Fri only'), Sat: t('Sat only'),
  } as Record<string, string>)[o] ?? o;

  return (
    <Section title={t('Labor Law & EOSB Settings')} icon={Clock}>
      <div className="space-y-5">
        {/* Work week */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Work Schedule')}</h4>
          <div className="grid grid-cols-2 gap-4">
            <Field label={t('Work Week')} hint={t('Saudi Labor Law: standard work week is Sun–Thu')}>
              <select value={form.workWeek} onChange={e => setForm(x => ({ ...x, workWeek: e.target.value }))} className="select w-full" title={t('Work Week')}>
                {workWeekOptions.map(o => <option key={o} value={o}>{workWeekLabel(o)}</option>)}
              </select>
            </Field>
            <Field label={t('Weekend Days')} hint={t('Saudi: Friday + Saturday')}>
              <select value={form.weekendDays} onChange={e => setForm(x => ({ ...x, weekendDays: e.target.value }))} className="select w-full" title={t('Weekend Days')}>
                {weekendOptions.map(o => <option key={o} value={o}>{weekendLabel(o)}</option>)}
              </select>
            </Field>
          </div>
        </div>

        {/* EOSB */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('EOSB — End of Service Benefits')}</h4>
          <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300 mb-3">
            <p className="font-semibold">{t('Saudi Labor Law Art. 84 — EOSB Formula')}</p>
            <ul className="mt-1 list-disc list-inside space-y-0.5 text-slate-500 dark:text-slate-400">
              <li>{t('Years 1–5: ½ month salary per year')}</li>
              <li>{t('Years 5+: 1 month salary per year')}</li>
              <li>{t('Rates expressed in working days per year of service')}</li>
            </ul>
          </div>

          <label className="mb-3 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.eosbEnabled} onChange={e => setForm(x => ({ ...x, eosbEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Enable EOSB')} />
            {t('Enable EOSB calculation in payroll')}
          </label>

          {form.eosbEnabled && (
            <div className="grid grid-cols-3 gap-4">
              <Field label={t('Min Service Years')} hint={t('Minimum tenure before EOSB is payable')}>
                <input type="number" min={0} value={form.eosbMinYears} onChange={e => setForm(x => ({ ...x, eosbMinYears: Number(e.target.value) }))} className="input w-full" title={t('Min Service Years')} />
              </Field>
              <Field label={t('Rate: Years 1–5 (days/yr)')} hint={t('Default: 21 days = ½ month')}>
                <input type="number" min={0} step={0.5} value={form.eosbYears1To5Rate} onChange={e => setForm(x => ({ ...x, eosbYears1To5Rate: Number(e.target.value) }))} className="input w-full" title={t('EOSB Rate Years 1 to 5')} />
              </Field>
              <Field label={t('Rate: Years 5+ (days/yr)')} hint={t('Default: 30 days = 1 month')}>
                <input type="number" min={0} step={0.5} value={form.eosbYearsAbove5Rate} onChange={e => setForm(x => ({ ...x, eosbYearsAbove5Rate: Number(e.target.value) }))} className="input w-full" title={t('EOSB Rate Years Above 5')} />
              </Field>
            </div>
          )}
        </div>

        {/* Ramadan */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Ramadan Working Hours')}</h4>
          <label className="mb-3 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.ramadanHoursEnabled} onChange={e => setForm(x => ({ ...x, ramadanHoursEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Enable Ramadan Hours')} />
            {t('Reduce daily working hours during Ramadan')}
          </label>
          {form.ramadanHoursEnabled && (
            <Field label={t('Reduction per day (hours)')} hint={t('Saudi Labor Law: working hours reduced by 2h during Ramadan for Muslim employees')}>
              <input type="number" min={1} max={4} value={form.ramadanReducedHoursPerDay} onChange={e => setForm(x => ({ ...x, ramadanReducedHoursPerDay: Number(e.target.value) }))} className="input w-48" title={t('Ramadan Reduced Hours Per Day')} />
            </Field>
          )}
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving} className="ms-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><Save className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save Labor Settings')}</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Document Tracking Config Panel ────────────────────────────────────────────

function DocumentTrackingPanel({ gcc, onGccUpdate }: { gcc: GCCComplianceSetting | null; onGccUpdate: (g: GCCComplianceSetting) => void }) {
  const t = useT();
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
      setMsg(t('Document tracking settings saved.'));
    } catch {
      setMsg(t('Failed to save settings.')); setIsErr(true);
    } finally { setSaving(false); }
  };

  return (
    <Section title={t('Document Tracking & Expiry Alerts')} icon={FileText}>
      <div className="space-y-5">
        {/* Visa / Iqama */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Residency Documents')}</h4>
          <div className="grid grid-cols-2 gap-4">
            <div className="rounded-lg border border-slate-100 p-4 dark:border-white/[0.07] space-y-3">
              <label className="flex items-center gap-2 text-sm font-semibold text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={form.visaTrackingEnabled} onChange={e => setForm(x => ({ ...x, visaTrackingEnabled: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Enable Visa Tracking')} />
                {t('Visa / Iqama Tracking')}
              </label>
              {form.visaTrackingEnabled && (
                <Field label={t('Alert Days Before Expiry')} hint={t('Trigger renewal reminder this many days before expiry')}>
                  <input type="number" min={7} max={365} value={form.visaAlertDays} onChange={e => setForm(x => ({ ...x, visaAlertDays: Number(e.target.value) }))} className="input w-full" title={t('Visa Alert Days')} />
                </Field>
              )}
            </div>

            <div className="rounded-lg border border-slate-100 p-4 dark:border-white/[0.07] space-y-3">
              <label className="flex items-center gap-2 text-sm font-semibold text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={form.iqamaRequired} onChange={e => setForm(x => ({ ...x, iqamaRequired: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Iqama Required')} />
                {t('Iqama Required for Non-Saudi')}
              </label>
              {form.iqamaRequired && (
                <Field label={t('Alert Days Before Expiry')} hint={t('Alert HR when Iqama is expiring soon')}>
                  <input type="number" min={7} max={180} value={form.iqamaAlertDays} onChange={e => setForm(x => ({ ...x, iqamaAlertDays: Number(e.target.value) }))} className="input w-full" title={t('Iqama Alert Days')} />
                </Field>
              )}
            </div>
          </div>
        </div>

        {/* GCC Docs */}
        <div>
          <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('GCC Documents')}</h4>
          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
            <input type="checkbox" checked={form.emiratesIdRequired} onChange={e => setForm(x => ({ ...x, emiratesIdRequired: e.target.checked }))} className="h-4 w-4 accent-sapphire" title={t('Emirates ID Required')} />
            {t('Emirates ID required (for employees also working in UAE)')}
          </label>
        </div>

        <div className="rounded-lg border border-slate-100 bg-slate-50 px-4 py-3 text-xs text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300">
          <p className="font-semibold">{t('Expiry Alerts are sent via the Notification system.')}</p>
          <p className="text-slate-400 mt-0.5">{t('Configure notification channels (email, in-app) from Settings → Notifications. Alerts appear in the Compliance Dashboard and employee profile.')}</p>
        </div>

        <div className="flex items-center gap-3">
          <SaveBanner message={msg} isError={isErr} />
          <button type="button" onClick={save} disabled={saving} className="ms-auto btn-primary disabled:opacity-60">
            {saving ? <><RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> {t('Saving…')}</> : <><Save className="h-3.5 w-3.5" aria-hidden="true" /> {t('Save Tracking Settings')}</>}
          </button>
        </div>
      </div>
    </Section>
  );
}

// ── Main SaudiComplianceConfig ────────────────────────────────────────────────

type ConfigSection = 'qiwa' | 'gosi' | 'wps' | 'labor' | 'documents';

const NAV_ICONS: { id: ConfigSection; icon: React.ElementType }[] = [
  { id: 'qiwa', icon: Wifi }, { id: 'gosi', icon: Users }, { id: 'wps', icon: Banknote },
  { id: 'labor', icon: Clock }, { id: 'documents', icon: FileText },
];

function buildNavItems(t: (k: string) => string): { id: ConfigSection; label: string; icon: React.ElementType; desc: string }[] {
  const text: Record<ConfigSection, { label: string; desc: string }> = {
    qiwa: { label: t('QIWA'), desc: t('Establishment ID and the Qiwa data check') },
    gosi: { label: t('GOSI'), desc: t('Employer ID, contribution rates reference') },
    wps: { label: t('WPS'), desc: t('Wage Protection System, SIF export, MOL code') },
    labor: { label: t('Labor & EOSB'), desc: t('Work week, EOSB rates, Ramadan hours') },
    documents: { label: t('Document Tracking'), desc: t('Visa, Iqama, Emirates ID expiry alerts') },
  };
  return NAV_ICONS.map(({ id, icon }) => ({ id, icon, ...text[id] }));
}

// GOSI and WPS employer IDs are saved on a company. This screen used to ask for one company (page 1,
// one row) and edit whichever came back first, so in a multi-company tenant it could show, and
// overwrite, a UAE company's IDs while every Saudi entity past the first was unreachable. It now
// reads every company, keeps the Saudi ones, and names the one being edited.
function CompanyScope({ companies, allCount, companyId, onChange, error }: {
  companies: CompanyDto[] | null;
  allCount: number;
  companyId: string;
  onChange: (id: string) => void;
  error: unknown;
}) {
  const t = useT();
  if (error != null) {
    return (
      <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300">
        {t('Companies could not be loaded, so there is no company to save these settings on.')} {requestFailureReason(error)}
      </p>
    );
  }
  if (companies === null) return <p className="text-sm text-slate-400">{t('Loading companies…')}</p>;
  if (companies.length === 0) {
    return (
      <div role="status" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
        <p className="font-semibold">{t('No Saudi company')}</p>
        <p className="mt-0.5 text-xs">
          {t('GOSI and WPS employer IDs belong to a company registered in Saudi Arabia.')}
          {allCount > 0 ? ` ${t('{count, plural, one {None of your # company has the country set to SA.} other {None of your # companies have the country set to SA.}}', { count: allCount })}` : ''}
          {' '}{t('Set the country on the company under Companies, then return here.')}
        </p>
      </div>
    );
  }
  const name = (c: CompanyDto) => c.tradeName || c.legalNameEn;
  if (companies.length === 1) {
    return <p className="text-sm text-slate-500 dark:text-slate-400">{rich(t('Settings for {name}, your Saudi company.', { name: mark('name') }), { name: <strong className="text-slate-800 dark:text-slate-100">{name(companies[0])}</strong> })}</p>;
  }
  return (
    <label className="flex flex-wrap items-center gap-2 text-sm">
      <span className="font-semibold text-slate-700 dark:text-slate-300">{t('Company')}</span>
      <select
        value={companyId}
        onChange={(e) => onChange(e.target.value)}
        className="input w-72"
        title={t('Saudi company whose settings are shown')}
      >
        {companies.map((c) => <option key={c.id} value={c.id}>{name(c)}</option>)}
      </select>
      <span className="text-xs text-slate-400">{t('{count} Saudi companies. Employer IDs are saved on the company selected here.', { count: companies.length })}</span>
    </label>
  );
}

/** The part of the configuration a link asked for (`?section=qiwa|gosi|wps|labor|documents`), or undefined. */
export function configSectionFrom(value: string | null | undefined): ConfigSection | undefined {
  return NAV_ICONS.find(item => item.id === value)?.id;
}

/**
 * The Saudi compliance settings (Setup → Saudi compliance). `initialSection` opens one part of it, and moves focus
 * there: the compliance action centre sends people straight to the Qiwa or GOSI details that need attention.
 */
export function SaudiComplianceConfig({ initialSection }: { initialSection?: ConfigSection } = {}) {
  const t = useT();
  const navItems = buildNavItems(t);
  const [active, setActive] = useState<ConfigSection>(initialSection ?? 'qiwa');
  const panelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!initialSection) return;
    setActive(initialSection);
    panelRef.current?.focus({ preventScroll: true });
    panelRef.current?.scrollIntoView?.({ block: 'start' });
  }, [initialSection]);
  const [companies, setCompanies] = useState<CompanyDto[] | null>(null);
  const [allCompanyCount, setAllCompanyCount] = useState(0);
  const [companiesError, setCompaniesError] = useState<unknown>(null);
  const [companyId, setCompanyId] = useState('');
  const [gcc, setGcc] = useState<GCCComplianceSetting | null>(null);
  const company = companies?.find(c => c.id === companyId) ?? null;

  const load = useCallback(async () => {
    const [compResult, gccResult] = await Promise.allSettled([
      companiesApi.listAll(),
      gccSettingsApi.list('SA'),
    ]);
    if (compResult.status === 'fulfilled') {
      const saudi = compResult.value.filter(isSaudiCompany);
      setAllCompanyCount(compResult.value.length);
      setCompanies(saudi);
      setCompaniesError(null);
      setCompanyId(prev => selectSaudiCompany(saudi, prev));
    } else {
      setCompanies([]);
      setCompaniesError(compResult.reason);
    }
    const liveGcc = gccResult.status === 'fulfilled' ? gccResult.value.filter(row => row.isActive !== false) : [];
    if (liveGcc.length > 0) setGcc(liveGcc[0]);
  }, []);

  useEffect(() => { load(); }, [load]);

  const updateCompany = useCallback((updated: CompanyDto) => {
    setCompanies(list => list?.map(c => (c.id === updated.id ? updated : c)) ?? list);
  }, []);

  return (
    <div className="flex gap-5">
      {/* Left nav */}
      <nav className="w-52 shrink-0 space-y-1">
        {navItems.map(({ id, label, icon: Icon, desc }) => (
          <button
            key={id}
            type="button"
            onClick={() => setActive(id)}
            aria-current={active === id ? 'true' : undefined}
            className={`w-full rounded-xl px-3 py-2.5 text-start transition ${active === id ? 'bg-sapphire/[0.08] dark:bg-cyanAccent/[0.08]' : 'hover:bg-slate-50 dark:hover:bg-white/[0.04]'}`}
          >
            <div className="flex items-center gap-2">
              <Icon className={`h-4 w-4 shrink-0 ${active === id ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-400'}`} />
              <span className={`text-sm font-semibold ${active === id ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}>{label}</span>
            </div>
            <p className="mt-0.5 ps-6 text-caption text-slate-400 leading-tight">{desc}</p>
          </button>
        ))}

        <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 dark:border-amber-800 dark:bg-amber-900/20 mt-4">
          <p className="text-caption font-semibold text-amber-700 dark:text-amber-400">{t('Ministry of HR')}</p>
          <p className="text-caption text-amber-600 dark:text-amber-300 mt-0.5 leading-relaxed">
            {t('All Saudi regulatory integrations must comply with HRSD, SAMA, and GOSI requirements. Verify credentials with the respective portals.')}
          </p>
        </div>
      </nav>

      {/* Panel */}
      <div ref={panelRef} tabIndex={-1} data-config-section={active} aria-label={navItems.find(item => item.id === active)?.label} role="region" className="min-w-0 flex-1 space-y-4 outline-none">
        {(active === 'gosi' || active === 'wps') && (
          <CompanyScope companies={companies} allCount={allCompanyCount} companyId={companyId} onChange={setCompanyId} error={companiesError} />
        )}
        {active === 'qiwa'      && <QiwaPanel />}
        {active === 'gosi'      && <GosiPanel company={company} onCompanyUpdate={updateCompany} />}
        {active === 'wps'       && <WpsPanel company={company} onCompanyUpdate={updateCompany} gcc={gcc} onGccUpdate={setGcc} />}
        {active === 'labor'     && <LaborPanel gcc={gcc} onGccUpdate={setGcc} />}
        {active === 'documents' && <DocumentTrackingPanel gcc={gcc} onGccUpdate={setGcc} />}
      </div>
    </div>
  );
}
