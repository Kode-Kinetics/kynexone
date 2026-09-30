'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, CheckCircle, XCircle, AlertTriangle, Circle, Database, Server, Activity, Send } from 'lucide-react';
import { platformApi, type PlatformDiagnostics, type PlatformHealthStatus } from '@/src/api/platform';
import { deliveryAttentionSummary, emailModeLabel, qiwaModeLabel } from '@/src/lib/integrationDeliveryState';

type VersionData = { version: string; environment: string; deployedAt?: string; migrations?: number };

// ── Status dot ────────────────────────────────────────────────────────────────

function StatusDot({ ok, warn }: { ok: boolean; warn?: boolean }) {
  if (warn) return <span className="h-2 w-2 rounded-full bg-amber-400 shrink-0 inline-block" />;
  return <span className={`h-2 w-2 rounded-full shrink-0 inline-block ${ok ? 'bg-emerald-400' : 'bg-rose-500'}`} />;
}

function StatusIcon({ ok, warn }: { ok: boolean; warn?: boolean }) {
  if (warn) return <AlertTriangle className="h-4 w-4 text-amber-400" />;
  if (ok)   return <CheckCircle   className="h-4 w-4 text-emerald-400" />;
  return        <XCircle       className="h-4 w-4 text-rose-400" />;
}

// ── Info row ──────────────────────────────────────────────────────────────────

function InfoRow({ label, value, mono = false }: { label: string; value: React.ReactNode; mono?: boolean }) {
  return (
    <div className="flex items-center justify-between py-2.5 border-b border-white/[0.04] last:border-0">
      <span className="text-xs text-slate-500">{label}</span>
      <span className={`text-xs text-slate-300 ${mono ? 'font-mono' : ''}`}>{value}</span>
    </div>
  );
}

// ── Card ──────────────────────────────────────────────────────────────────────

function Card({ title, icon, children, status }: {
  title: string;
  icon: React.ReactNode;
  children: React.ReactNode;
  status?: 'ok' | 'error' | 'warn';
}) {
  const borderCls = status === 'ok'
    ? 'border-emerald-500/20'
    : status === 'error'
      ? 'border-rose-500/20'
      : 'border-white/[0.07]';

  return (
    <div className={`bg-[#161b22] border ${borderCls} rounded-xl px-5 py-4`}>
      <div className="flex items-center gap-2 mb-3">
        <span className="text-slate-500">{icon}</span>
        <p className="text-[10px] font-semibold text-slate-500 uppercase tracking-widest">{title}</p>
      </div>
      {children}
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function SystemHealthPage() {
  const router = useRouter();
  const [diag, setDiag]       = useState<PlatformDiagnostics | null>(null);
  const [ver, setVer]         = useState<VersionData | null>(null);
  const [health, setHealth]   = useState<PlatformHealthStatus | null>(null);
  const [healthFailed, setHealthFailed] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState('');
  const [lastChecked, setLastChecked] = useState<Date | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    refresh();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const refresh = useCallback(async () => {
    setLoading(true); setError('');
    try {
      const [d, v] = await Promise.all([
        platformApi.getDiagnostics(),
        platformApi.getVersion(),
      ]);
      setDiag(d);
      setVer(v);
      setLastChecked(new Date());
    } catch {
      setError('Failed to reach diagnostics endpoints. The API may be down.');
    } finally { setLoading(false); }
    // Separate call and separate failure: an unreachable health endpoint must read as
    // "unavailable", never as "no undelivered messages".
    try {
      setHealth(await platformApi.getHealth());
      setHealthFailed(false);
    } catch {
      setHealth(null);
      setHealthFailed(true);
    }
  }, []);

  const email = health?.components.email;
  const qiwa = health?.components.qiwa;
  const deliveries = health?.components.deliveries;

  const overallOk = diag?.databaseOk !== false && !error;

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">System Health</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            {lastChecked
              ? `Last checked ${lastChecked.toLocaleTimeString()}`
              : loading ? 'Checking…' : 'Not checked yet'}
          </p>
        </div>
        <button
          type="button"
          onClick={refresh}
          disabled={loading}
          title="Refresh"
          className="flex items-center gap-1.5 text-xs text-slate-500 hover:text-slate-300 border border-white/10 rounded-lg px-3 py-1.5 transition-colors disabled:opacity-40"
        >
          <RefreshCw className={`h-3 w-3 ${loading ? 'animate-spin' : ''}`} />
          Refresh
        </button>
      </div>

      {/* Error banner */}
      {error && (
        <div className="px-4 py-3 bg-rose-500/10 border border-rose-500/20 rounded-xl text-sm text-rose-400">
          {error}
        </div>
      )}

      {/* Overall status banner */}
      {(diag || error) && (
        <div className={`flex items-center gap-3 px-5 py-4 rounded-xl border ${
          overallOk
            ? 'bg-emerald-500/5 border-emerald-500/20'
            : 'bg-rose-500/5 border-rose-500/20'
        }`}>
          <StatusIcon ok={overallOk} />
          <div>
            <p className={`text-sm font-semibold ${overallOk ? 'text-emerald-300' : 'text-rose-300'}`}>
              {overallOk ? 'All Systems Operational' : 'System Degraded — check details below'}
            </p>
            {diag && (
              <p className="text-xs text-slate-500 mt-0.5">
                {diag.activeTenants} active tenant{diag.activeTenants !== 1 ? 's' : ''} ·{' '}
                {diag.employeeCount.toLocaleString()} employees ·{' '}
                {diag.maintenance ? 'Maintenance mode ON' : 'Maintenance mode OFF'}
              </p>
            )}
          </div>
        </div>
      )}

      {loading && !diag ? (
        <div className="flex items-center justify-center py-20">
          <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">

          {/* Version card */}
          {ver && (
            <Card title="Application" icon={<Activity className="h-3.5 w-3.5" />} status="ok">
              <InfoRow label="Version"     value={ver.version}     mono />
              <InfoRow label="Environment" value={ver.environment} mono />
              {ver.deployedAt && (
                <InfoRow
                  label="Deployed At"
                  value={new Date(ver.deployedAt).toLocaleString('en-GB')}
                />
              )}
              {ver.migrations !== undefined && (
                <InfoRow label="Migrations Applied" value={ver.migrations} mono />
              )}
            </Card>
          )}

          {/* Database card */}
          {diag && (
            <Card
              title="Database"
              icon={<Database className="h-3.5 w-3.5" />}
              status={diag.databaseOk ? 'ok' : 'error'}
            >
              <div className="flex items-center gap-2 mb-3">
                <StatusDot ok={diag.databaseOk} />
                <span className={`text-sm font-medium ${diag.databaseOk ? 'text-emerald-400' : 'text-rose-400'}`}>
                  {diag.databaseOk ? 'Connected' : 'Unreachable'}
                </span>
              </div>
              <InfoRow label="Tenants"        value={diag.tenantCount}  mono />
              <InfoRow label="Active Tenants" value={diag.activeTenants} mono />
              <InfoRow label="Employees"      value={diag.employeeCount.toLocaleString()} mono />
            </Card>
          )}

          {/* AI / Services card */}
          {diag && (
            <Card
              title="Services"
              icon={<Server className="h-3.5 w-3.5" />}
              status={diag.aiConfigured ? 'ok' : 'warn'}
            >
              <div className="space-y-3">
                {/* AI Provider */}
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <StatusDot ok={diag.aiConfigured} warn={!diag.aiConfigured} />
                    <span className="text-xs text-slate-300">AI Provider</span>
                  </div>
                  <span className="text-xs text-slate-500 font-mono">{diag.aiProvider || '—'}</span>
                </div>
                {/* Maintenance */}
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <StatusDot ok={!diag.maintenance} warn={diag.maintenance} />
                    <span className="text-xs text-slate-300">Maintenance Mode</span>
                  </div>
                  <span className={`text-xs font-mono ${diag.maintenance ? 'text-amber-400' : 'text-slate-600'}`}>
                    {diag.maintenance ? 'ON' : 'OFF'}
                  </span>
                </div>
                {diag.maintenance && diag.maintenanceMsg && (
                  <p className="text-[11px] text-amber-400/70 bg-amber-500/5 border border-amber-500/10 rounded-lg px-3 py-2">
                    {diag.maintenanceMsg}
                  </p>
                )}
                {/* Server time */}
                <div className="pt-1 border-t border-white/[0.04]">
                  <InfoRow
                    label="Server Time (UTC)"
                    value={diag.serverTimeUtc ? new Date(diag.serverTimeUtc).toLocaleTimeString('en-GB') : '—'}
                    mono
                  />
                </div>
              </div>
            </Card>
          )}

          {/* Outbound integrations (F09). A healthy worker is not proof anything reached anyone:
              this card says how email and Qiwa are wired and what did NOT get delivered. */}
          <Card
            title="Outbound delivery"
            icon={<Send className="h-3.5 w-3.5" />}
            status={healthFailed ? 'error' : deliveries?.status === 'attention' || email?.simulated || qiwa?.simulated ? 'warn' : health ? 'ok' : undefined}
          >
            {healthFailed ? (
              <p className="text-xs text-rose-400" data-testid="delivery-health-unavailable">
                Delivery status is unavailable: the health endpoint did not answer. This does not mean nothing failed.
              </p>
            ) : !health ? (
              <p className="text-xs text-slate-500">Checking…</p>
            ) : (
              <div data-testid="delivery-health">
                <InfoRow
                  label="Email"
                  value={<span className={email?.simulated || !email?.configured ? 'text-amber-400' : ''}>{emailModeLabel(email?.status)}</span>}
                />
                <InfoRow
                  label="Qiwa"
                  value={<span className={qiwa?.simulated ? 'text-amber-400' : ''}>{qiwa ? qiwaModeLabel(!qiwa.simulated && qiwa.configured) : 'Not reported'}</span>}
                />
                {deliveries && deliveries.status !== 'unknown' && (
                  <>
                    <InfoRow label="Queued / retrying" value={`${deliveries.queued ?? 0} / ${deliveries.retrying ?? 0}`} mono />
                    <InfoRow label="Gave up after retries" value={deliveries.deadLetter ?? 0} mono />
                    <InfoRow label="Failed" value={deliveries.failed ?? 0} mono />
                    <InfoRow label="Not sent (not set up)" value={deliveries.notConfigured ?? 0} mono />
                    <InfoRow label="Captured by test mode" value={deliveries.captured ?? 0} mono />
                  </>
                )}
                <p className={`mt-2 text-[11px] ${deliveries?.status === 'attention' ? 'text-amber-400' : 'text-slate-500'}`}>
                  {deliveries ? deliveryAttentionSummary(deliveries) : 'This server does not report delivery counts yet.'}
                </p>
                {(email?.detail || qiwa?.detail) && (
                  <p className="mt-1 text-[11px] text-slate-500">{[email?.detail, qiwa?.detail].filter(Boolean).join(' ')}</p>
                )}
              </div>
            )}
          </Card>

          {/* Planned / future services — greyed out placeholders */}
          {[
            { key: 'redis',   label: 'Cache (Redis)' },
            { key: 'jobs',    label: 'Background Jobs' },
          ].map(c => (
            <div key={c.key}
              className="bg-[#161b22] border border-white/[0.04] rounded-xl px-5 py-4 opacity-35">
              <div className="flex items-center gap-2 mb-3">
                <Circle className="h-3.5 w-3.5 text-slate-700" />
                <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{c.label}</p>
              </div>
              <div className="flex items-center gap-2 py-1">
                <Circle className="h-3 w-3 text-slate-700" />
                <span className="text-xs text-slate-700">Not monitored yet</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
