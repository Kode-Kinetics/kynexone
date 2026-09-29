'use client';

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, Send, CheckCircle, AlertTriangle, X, Activity, ShieldAlert, Wrench, Info, ExternalLink, Mail } from 'lucide-react';
import {
  platformApi,
  type PlatformSettings,
  type PlatformDiagnostics,
  type EmailProviderPreset,
  type SmtpTestResult,
} from '@/src/api/platform';

/** Shape of the SMTP form. `provider` drives the auto-configuration. */
type SmtpForm = {
  provider: string;
  host: string;
  port: number;
  username: string;
  password: string;
  fromEmail: string;
  fromName: string;
  useSsl: boolean;
};

const EMPTY_SMTP: SmtpForm = {
  provider: '', host: '', port: 587, username: '', password: '', fromEmail: '', fromName: '', useSsl: true,
};

/**
 * What each provider expects in the Username field. The backend catalog carries the pattern key;
 * the wording lives here because it is UI copy, not configuration.
 */
const USERNAME_HINTS: Record<string, { label: string; placeholder: string; passwordPlaceholder: string }> = {
  'full-email':        { label: 'Your full mailbox address, including the @domain', placeholder: 'you@yourdomain.com', passwordPlaceholder: 'Mailbox password' },
  'literal-apikey':    { label: 'The literal word "apikey" — not your account email', placeholder: 'apikey', passwordPlaceholder: 'SG.xxxxxxxx API key' },
  'literal-resend':    { label: 'The literal word "resend"', placeholder: 'resend', passwordPlaceholder: 're_xxxxxxxx API key' },
  'ses-credentials':   { label: 'The SES SMTP username from the AWS console (not an AWS access key)', placeholder: 'AKIA…', passwordPlaceholder: 'SES SMTP password' },
  'domain-postmaster': { label: 'The domain postmaster address from Mailgun', placeholder: 'postmaster@mg.yourdomain.com', passwordPlaceholder: 'Mailgun SMTP password' },
  'token-both':        { label: 'Your Server API Token — paste the same value into the password', placeholder: 'Server API Token', passwordPlaceholder: 'Same Server API Token' },
  'api-key-pair':      { label: 'Your Mailjet API Key', placeholder: 'API Key', passwordPlaceholder: 'Secret Key' },
};

const DEFAULT_HINT = USERNAME_HINTS['full-email'];

export default function PlatformSettingsPage() {
  const router = useRouter();
  const [settings, setSettings] = useState<PlatformSettings | null>(null);
  const [loading, setLoading]   = useState(true);
  const [loadErr, setLoadErr]   = useState('');
  const [msg, setMsg]           = useState<{ text: string; ok: boolean } | null>(null);

  // SMTP form state
  const [smtp, setSmtp] = useState<SmtpForm>(EMPTY_SMTP);
  const [providers, setProviders] = useState<EmailProviderPreset[]>([]);
  const [savingSmtp, setSavingSmtp] = useState(false);

  // Test email — the admin chooses the destination so they can confirm real delivery.
  const [testTo, setTestTo] = useState('');
  const [testingSmtp, setTestingSmtp] = useState(false);
  const [testResult, setTestResult] = useState<SmtpTestResult | null>(null);

  // Diagnostics
  const [diagnostics, setDiagnostics] = useState<PlatformDiagnostics | null>(null);
  const [loadingDiag, setLoadingDiag] = useState(false);

  // Maintenance mode
  const [maintenance, setMaintenance] = useState(false);
  const [maintenanceMsg, setMaintenanceMsg] = useState('');
  const [savingMaintenance, setSavingMaintenance] = useState(false);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
    loadDiagnostics();
    loadProviders();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);  // load/loadDiagnostics/loadProviders are stable (useCallback with no deps)

  const loadProviders = useCallback(async () => {
    try { setProviders(await platformApi.getEmailProviders()); }
    catch { /* the form still works with manual entry */ }
  }, []);

  const load = useCallback(async () => {
    setLoading(true); setLoadErr('');
    try {
      const s = await platformApi.getSettings();
      setSettings(s);
      if (s.smtp) {
        setSmtp({
          provider: s.smtp.provider ?? '',
          host: s.smtp.host ?? '',
          port: s.smtp.port ?? 587,
          username: s.smtp.username ?? '',
          password: '',
          fromEmail: s.smtp.fromEmail ?? '',
          fromName: s.smtp.fromName ?? '',
          useSsl: s.smtp.useSsl ?? true,
        });
        // Default the test recipient to the From address — the one inbox we know exists.
        setTestTo(prev => prev || s.smtp.fromEmail || '');
      }
    } catch {
      setLoadErr('Failed to load settings. This endpoint may not be implemented yet.');
    } finally { setLoading(false); }
  }, []);

  const loadDiagnostics = useCallback(async () => {
    setLoadingDiag(true);
    try {
      const d = await platformApi.getDiagnostics();
      setDiagnostics(d);
      setMaintenance(d.maintenance);
      setMaintenanceMsg(d.maintenanceMsg ?? '');
    } catch { /* diagnostics are non-critical */ }
    finally { setLoadingDiag(false); }
  }, []);

  async function saveSmtp(e: React.FormEvent) {
    e.preventDefault();
    setSavingSmtp(true); setTestResult(null);
    try {
      await platformApi.updateSmtpSettings(smtp);
      setMsg({ text: 'SMTP settings saved. Send a test email to confirm delivery.', ok: true });
      await load();
    } catch (err) {
      setMsg({ text: apiMessage(err, 'Save failed.'), ok: false });
    } finally { setSavingSmtp(false); }
  }

  async function saveMaintenance() {
    setSavingMaintenance(true);
    try {
      await platformApi.setMaintenanceMode(maintenance, maintenanceMsg);
      setMsg({ text: `Maintenance mode ${maintenance ? 'enabled' : 'disabled'}.`, ok: true });
      await loadDiagnostics();
    } catch { setMsg({ text: 'Could not update maintenance mode.', ok: false }); }
    finally { setSavingMaintenance(false); }
  }

  async function testSmtp() {
    setTestingSmtp(true); setTestResult(null); setMsg(null);
    try {
      setTestResult(await platformApi.testSmtp(testTo.trim() || undefined));
    } catch (err) {
      setTestResult({ sent: false, message: apiMessage(err, 'Test failed. Check the SMTP settings and try again.') });
    } finally { setTestingSmtp(false); }
  }

  const activePreset = useMemo(
    () => providers.find(p => p.key === smtp.provider) ?? null,
    [providers, smtp.provider],
  );

  /** Applying a preset fills connection details only — credentials stay as the admin typed them. */
  function applyPreset(key: string) {
    const preset = providers.find(p => p.key === key);
    setSmtp(f => {
      if (!preset || !preset.host) return { ...f, provider: key };
      return { ...f, provider: key, host: preset.host, port: preset.port, useSsl: preset.useSsl };
    });
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[60vh]">
        <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  const smtpForm = (
    <SmtpForm
      smtp={smtp}
      setSmtp={setSmtp}
      providers={providers}
      preset={activePreset}
      onPreset={applyPreset}
      onSave={saveSmtp}
      saving={savingSmtp}
      testTo={testTo}
      setTestTo={setTestTo}
      onTest={testSmtp}
      testing={testingSmtp}
      testResult={testResult}
      clearTestResult={() => setTestResult(null)}
    />
  );

  if (loadErr) {
    return (
      <div className="space-y-5">
        <h1 className="text-lg font-bold text-white">Platform Settings</h1>
        <div className="px-5 py-6 bg-amber-500/5 border border-amber-500/20 rounded-xl">
          <p className="text-sm text-amber-400">{loadErr}</p>
          <p className="text-xs text-slate-500 mt-2">SMTP and maintenance settings can still be configured below.</p>
        </div>
        {/* Show SMTP form anyway for configuration */}
        {smtpForm}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-bold text-white">Platform Settings</h1>
        <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors">
          <RefreshCw className="h-3.5 w-3.5" />
        </button>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" aria-label="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* SMTP status banner */}
      {settings && (
        <div className={`flex items-start gap-3 px-5 py-3.5 rounded-xl border ${
          settings.smtp?.isConfigured
            ? 'bg-emerald-500/5 border-emerald-500/20'
            : 'bg-amber-500/5 border-amber-500/20'
        }`}>
          {settings.smtp?.isConfigured
            ? <CheckCircle className="h-4 w-4 text-emerald-400 shrink-0 mt-0.5" />
            : <AlertTriangle className="h-4 w-4 text-amber-400 shrink-0 mt-0.5" />}
          <div>
            <p className={`text-sm font-medium ${settings.smtp?.isConfigured ? 'text-emerald-300' : 'text-amber-300'}`}>
              {settings.smtp?.isConfigured
                ? `Email is configured — sending as ${settings.smtp.fromEmail} via ${settings.smtp.host}:${settings.smtp.port}`
                : 'Email is not configured — invoice emails and password reset emails will not be delivered'}
            </p>
            <p className="text-xs text-slate-500 mt-0.5">
              {settings.smtp?.isConfigured
                ? `${settings.smtp.providerLabel ?? 'Custom SMTP'} · ${settings.smtp.source === 'environment' ? 'from environment variables' : 'saved in Platform Settings'}${settings.smtp.hasPassword ? '' : ' · no password stored'}`
                : 'Choose your email provider below, enter the mailbox credentials, save, then send a test email.'}
            </p>
          </div>
        </div>
      )}

      {smtpForm}

      {/* AI Provider status */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">AI Provider</p>
          <Activity className="h-3.5 w-3.5 text-slate-600" />
        </div>
        <div className="px-5 py-4 flex items-center gap-3">
          {diagnostics ? (
            <>
              <span className={`h-2.5 w-2.5 rounded-full shrink-0 ${diagnostics.aiConfigured ? 'bg-emerald-400' : 'bg-amber-400'}`} />
              <div>
                <p className="text-sm text-white font-medium capitalize">{diagnostics.aiProvider}</p>
                <p className={`text-xs ${diagnostics.aiConfigured ? 'text-emerald-400' : 'text-amber-400'}`}>
                  {diagnostics.aiConfigured ? 'Connected — AI features available' : 'Not configured — AI features disabled'}
                </p>
              </div>
            </>
          ) : (
            <p className="text-sm text-slate-500">{loadingDiag ? 'Loading…' : 'Unavailable'}</p>
          )}
        </div>
      </div>

      {/* Environment diagnostics */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Environment Diagnostics</p>
          <button
            type="button"
            onClick={loadDiagnostics}
            disabled={loadingDiag}
            className="text-xs text-slate-500 hover:text-white flex items-center gap-1"
          >
            <RefreshCw className={`h-3 w-3 ${loadingDiag ? 'animate-spin' : ''}`} /> Refresh
          </button>
        </div>
        {diagnostics ? (
          <div className="divide-y divide-white/[0.04]">
            {[
              { label: 'Database', value: diagnostics.databaseOk ? 'Connected' : 'Error', ok: diagnostics.databaseOk },
              { label: 'Total tenants', value: String(diagnostics.tenantCount) },
              { label: 'Active tenants', value: String(diagnostics.activeTenants) },
              { label: 'Active employees', value: String(diagnostics.employeeCount) },
              { label: 'Server time (UTC)', value: new Date(diagnostics.serverTimeUtc).toLocaleString() },
            ].map(row => (
              <div key={row.label} className="flex items-center justify-between px-5 py-3">
                <span className="text-sm text-slate-400">{row.label}</span>
                <span className={`text-sm font-medium ${'ok' in row ? (row.ok ? 'text-emerald-400' : 'text-red-400') : 'text-white'}`}>{row.value}</span>
              </div>
            ))}
          </div>
        ) : (
          <div className="px-5 py-4 text-sm text-slate-500">{loadingDiag ? 'Loading diagnostics…' : 'Could not load diagnostics.'}</div>
        )}
      </div>

      {/* Maintenance mode */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="flex items-center gap-2 px-5 py-3 border-b border-white/[0.06]">
          <Wrench className="h-3.5 w-3.5 text-slate-600" />
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Maintenance Mode</p>
        </div>
        <div className="px-5 py-5 space-y-4">
          {maintenance && (
            <div className="flex items-center gap-2 rounded-lg border border-amber-500/20 bg-amber-500/5 px-4 py-2.5">
              <ShieldAlert className="h-4 w-4 text-amber-400 shrink-0" />
              <p className="text-sm text-amber-300">Maintenance mode is currently <strong>enabled</strong>. Tenants may see a downtime notice.</p>
            </div>
          )}
          <label className="flex items-center gap-3 cursor-pointer">
            <button
              type="button"
              role="switch"
              aria-checked={maintenance ? 'true' : 'false'}
              onClick={() => setMaintenance(m => !m)}
              className={`relative h-6 w-11 rounded-full transition-colors ${maintenance ? 'bg-amber-500' : 'bg-slate-700'}`}
            >
              <span className={`absolute top-0.5 start-0.5 h-5 w-5 rounded-full bg-white shadow transition-transform ${maintenance ? 'translate-x-5 rtl:-translate-x-5' : ''}`} />
            </button>
            <span className="text-sm text-slate-300">{maintenance ? 'Maintenance mode ON' : 'Maintenance mode OFF'}</span>
          </label>
          <label className="block">
            <span className="text-xs text-slate-400">Message shown to users (optional)</span>
            <input
              value={maintenanceMsg}
              onChange={e => setMaintenanceMsg(e.target.value)}
              placeholder="We'll be back shortly. Scheduled maintenance window."
              className="mt-1 w-full rounded-lg border border-white/[0.08] bg-white/[0.04] px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-blue-500/60"
            />
          </label>
          <button
            type="button"
            onClick={saveMaintenance}
            disabled={savingMaintenance}
            className="rounded-lg bg-blue-600 px-5 py-2 text-sm font-medium text-white hover:bg-blue-500 disabled:opacity-50"
          >
            {savingMaintenance ? 'Saving…' : 'Save Maintenance Settings'}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Pulls the API's own message out of an axios error so the relay's wording reaches the admin. */
function apiMessage(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: { message?: string } } })?.response?.data;
  return data?.message || fallback;
}

const INPUT = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600';

function SmtpForm({
  smtp, setSmtp, providers, preset, onPreset, onSave, saving,
  testTo, setTestTo, onTest, testing, testResult, clearTestResult,
}: {
  smtp: SmtpForm;
  setSmtp: (fn: (prev: SmtpForm) => SmtpForm) => void;
  providers: EmailProviderPreset[];
  preset: EmailProviderPreset | null;
  onPreset: (key: string) => void;
  onSave: (e: React.FormEvent) => void;
  saving: boolean;
  testTo: string;
  setTestTo: (v: string) => void;
  onTest: () => void;
  testing: boolean;
  testResult: SmtpTestResult | null;
  clearTestResult: () => void;
}) {
  const hint = USERNAME_HINTS[preset?.usernamePattern ?? 'full-email'] ?? DEFAULT_HINT;

  // Ports the chosen provider accepts, plus whatever is currently set, so a hand-typed port is
  // never silently dropped by the select.
  const portOptions = useMemo(() => {
    const ports = new Set<number>([587, 465, 25, 2525]);
    if (preset) { ports.add(preset.port); preset.alternatePorts.forEach(p => ports.add(p)); }
    ports.add(smtp.port);
    return [...ports].sort((a, b) => a - b);
  }, [preset, smtp.port]);

  // Group the catalog so business mailboxes and transactional relays are not one flat list.
  const grouped = useMemo(() => {
    const order = ['Business', 'Transactional', 'Consumer', 'Other'];
    const map = new Map<string, EmailProviderPreset[]>();
    providers.forEach(p => map.set(p.category, [...(map.get(p.category) ?? []), p]));
    return order.filter(c => map.has(c)).map(c => [c, map.get(c)!] as const);
  }, [providers]);

  return (
    <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/[0.06]">
        <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Email / SMTP Configuration</p>
      </div>
      <form onSubmit={onSave} className="px-5 py-5 space-y-4">
        {/* Provider auto-configuration */}
        <div>
          <label htmlFor="smtp-provider" className="block text-xs text-slate-400 mb-1">Email provider</label>
          <select
            id="smtp-provider"
            value={smtp.provider}
            onChange={e => onPreset(e.target.value)}
            className={INPUT}
          >
            <option value="">Select a provider to auto-fill the server settings…</option>
            {grouped.map(([category, items]) => (
              <optgroup key={category} label={category}>
                {items.map(p => (
                  <option key={p.key} value={p.key}>
                    {p.label}{p.host ? ` — ${p.host}:${p.port}` : ''}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
          <p className="mt-1 text-[11px] text-slate-500">
            Picking a provider fills in the host, port and encryption. Your username and password are never changed by it.
          </p>
        </div>

        {preset && (
          <div className="flex items-start gap-2.5 rounded-lg border border-sky-500/20 bg-sky-500/5 px-4 py-3">
            <Info className="h-3.5 w-3.5 text-sky-400 shrink-0 mt-0.5" />
            <div className="space-y-1">
              <p className="text-xs text-sky-200 leading-relaxed">{preset.guidance}</p>
              {preset.docsUrl && (
                <a href={preset.docsUrl} target="_blank" rel="noopener noreferrer"
                  className="inline-flex items-center gap-1 text-[11px] text-sky-400 hover:text-sky-300">
                  {preset.label} SMTP documentation <ExternalLink className="h-3 w-3" />
                </a>
              )}
            </div>
          </div>
        )}

        <div className="grid grid-cols-2 gap-4">
          <div>
            <label htmlFor="smtp-host" className="block text-xs text-slate-400 mb-1">SMTP Host</label>
            <input id="smtp-host" value={smtp.host}
              onChange={e => setSmtp(f => ({ ...f, host: e.target.value }))}
              className={INPUT} placeholder="smtpout.secureserver.net" />
          </div>
          <div>
            <label htmlFor="smtp-port" className="block text-xs text-slate-400 mb-1">Port</label>
            <select id="smtp-port" value={smtp.port}
              onChange={e => setSmtp(f => ({ ...f, port: parseInt(e.target.value, 10) || 587 }))}
              className={INPUT}>
              {portOptions.map(p => (
                <option key={p} value={p}>
                  {p}{p === 587 ? ' — STARTTLS (recommended)' : p === 465 ? ' — implicit TLS' : p === 25 ? ' — unencrypted relay' : ''}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <div>
            <label htmlFor="smtp-username" className="block text-xs text-slate-400 mb-1">Username</label>
            <input id="smtp-username" value={smtp.username}
              onChange={e => setSmtp(f => ({ ...f, username: e.target.value }))}
              className={INPUT} placeholder={hint.placeholder} />
            <p className="mt-1 text-[11px] text-slate-500">{hint.label}</p>
          </div>
          <div>
            <label htmlFor="smtp-password" className="block text-xs text-slate-400 mb-1">Password</label>
            <input id="smtp-password" type="password" value={smtp.password}
              onChange={e => setSmtp(f => ({ ...f, password: e.target.value }))}
              className={INPUT} placeholder="Leave blank to keep the current password" autoComplete="new-password" />
            <p className="mt-1 text-[11px] text-slate-500">{hint.passwordPlaceholder}. Stored encrypted; never shown again.</p>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <div>
            <label htmlFor="smtp-from-email" className="block text-xs text-slate-400 mb-1">From Email</label>
            <input id="smtp-from-email" type="email" value={smtp.fromEmail}
              onChange={e => setSmtp(f => ({ ...f, fromEmail: e.target.value }))}
              className={INPUT} placeholder="noreply@yourdomain.com" required />
            <p className="mt-1 text-[11px] text-slate-500">The address recipients see. Most providers require it to match the mailbox.</p>
          </div>
          <div>
            <label htmlFor="smtp-from-name" className="block text-xs text-slate-400 mb-1">From Name</label>
            <input id="smtp-from-name" value={smtp.fromName}
              onChange={e => setSmtp(f => ({ ...f, fromName: e.target.value }))}
              className={INPUT} placeholder="KynexOne" />
          </div>
        </div>

        <label className="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" checked={smtp.useSsl}
            onChange={e => setSmtp(f => ({ ...f, useSsl: e.target.checked }))}
            disabled={smtp.port === 465}
            className="h-4 w-4 rounded accent-sapphire disabled:opacity-50" />
          <span className="text-sm text-slate-400">
            {smtp.port === 465 ? 'TLS is implicit on port 465 — always encrypted' : 'Use SSL/TLS (STARTTLS)'}
          </span>
        </label>

        <div className="pt-1">
          <button type="submit" disabled={saving}
            className="bg-sapphire hover:bg-blue-500 text-white px-6 py-2 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save SMTP Settings'}
          </button>
        </div>
      </form>

      {/* Test delivery — separate from the form so submitting the form never fires a send. */}
      <div className="border-t border-white/[0.06] px-5 py-5 space-y-3">
        <div className="flex items-center gap-2">
          <Mail className="h-3.5 w-3.5 text-slate-600" />
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Send a test email</p>
        </div>
        <p className="text-xs text-slate-500">
          Sends a real message through the <strong className="text-slate-400">saved</strong> settings above. Save first if you just changed something.
        </p>
        <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <div className="flex-1">
            <label htmlFor="smtp-test-to" className="block text-xs text-slate-400 mb-1">Deliver the test to</label>
            <input
              id="smtp-test-to"
              type="email"
              value={testTo}
              onChange={e => { setTestTo(e.target.value); clearTestResult(); }}
              className={INPUT}
              placeholder="you@yourdomain.com"
            />
            <p className="mt-1 text-[11px] text-slate-500">
              Use an inbox you can open right now. Leave blank to send to your own platform-admin address.
            </p>
          </div>
          <button type="button" onClick={onTest} disabled={testing || !smtp.host}
            className="flex items-center justify-center gap-1.5 text-sm text-slate-300 border border-white/10 hover:border-white/25 px-4 py-2 rounded-lg transition-colors disabled:opacity-40 shrink-0">
            <Send className="h-3.5 w-3.5" />
            {testing ? 'Sending…' : 'Send test email'}
          </button>
        </div>

        {testResult && (
          <div className={`rounded-lg border px-4 py-3 ${testResult.sent ? 'border-emerald-500/20 bg-emerald-500/5' : 'border-rose-500/20 bg-rose-500/5'}`}>
            <div className="flex items-start gap-2">
              {testResult.sent
                ? <CheckCircle className="h-4 w-4 text-emerald-400 shrink-0 mt-0.5" />
                : <AlertTriangle className="h-4 w-4 text-rose-400 shrink-0 mt-0.5" />}
              <div className="space-y-1">
                <p className={`text-sm ${testResult.sent ? 'text-emerald-300' : 'text-rose-300'}`}>{testResult.message}</p>
                {testResult.sent && testResult.host && (
                  <p className="text-[11px] text-slate-500">
                    Relayed by {testResult.host}:{testResult.port}
                    {testResult.sentAtUtc ? ` at ${new Date(testResult.sentAtUtc).toLocaleTimeString()}` : ''}.
                    Delivery to the inbox is the provider&apos;s job from here — check the spam folder if it does not arrive.
                  </p>
                )}
                {!testResult.sent && testResult.error && (
                  <p className="text-[11px] text-slate-500 font-mono break-all">{testResult.error}</p>
                )}
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
