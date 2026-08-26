'use client';

import { useEffect, useState } from 'react';
import {
  ShieldCheck, AlertTriangle, CheckCircle, XCircle, RefreshCw, Banknote,
  FileWarning, Settings, Users, Wifi, TrendingUp, ExternalLink, Info,
  AlertCircle, Clock,
} from 'lucide-react';
import client from '../api/client';
import { SaudiComplianceConfig } from './SaudiComplianceConfig';

// ── Types ─────────────────────────────────────────────────────────────────────

interface BlockedEmployee {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  missingFields: string[];
}

interface GosiBlockedEmployee {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  blockingIssueCodes: string[];
}

interface OverallSection {
  complianceScore: number;
  urgentActionCount: number;
  lastEvaluatedAt: string;
  enabledModules: string[];
}

interface QiwaSection {
  featureEnabled: boolean;
  credentialConfigured: boolean;
  connectionStatus: string;
  lastConnectedAt: string | null;
  totalEmployees: number;
  readyForSync: number;
  blockedFromSync: number;
  readinessPercent: number;
  failedSyncCount: number;
  lastSuccessfulSync: string | null;
  blockedEmployees: BlockedEmployee[];
}

interface WpsSection {
  lastRunStatus: string | null;
  lastRunPeriod: string | null;
  pendingApprovals: number;
  missingIbanCount: number;
  exportHistoryCount: number;
  lastExportDate: string | null;
  lastExportStatus: string | null;
  blockingIssues: string[];
}

interface GosiSection {
  employeesMissingGosiRef: number;
  employeesMissingGosiEmployerId: number;
  readyCount: number;
  blockedCount: number;
  warningCount: number;
  readinessPercent: number;
  gccEmployeeCount: number;
  varianceCount: number;
  warnings: string[];
  blockedEmployees: GosiBlockedEmployee[];
}

interface ActionItem {
  id: string;
  severity: 'Critical' | 'High' | 'Medium' | 'Low';
  module: string;
  title: string;
  description: string;
  affectedCount: number;
  recommendedAction: string;
  route: string | null;
  permissionRequired: string;
  canAct: boolean;
  evaluatedAt: string;
}

interface Dashboard {
  overall: OverallSection;
  qiwa: QiwaSection;
  wps: WpsSection;
  gosi: GosiSection;
  actionItems: ActionItem[];
}

// ── Helper components ─────────────────────────────────────────────────────────

function fmtDate(s: string | null): string {
  if (!s) return '—';
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString();
}

function fmtDateTime(s: string | null): string {
  if (!s) return '—';
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

function ConnectionBadge({ status }: { status: string }) {
  const map: Record<string, string> = {
    Connected:          'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    Disconnected:       'bg-slate-100 text-slate-600 dark:bg-slate-500/20 dark:text-slate-300',
    NotConfigured:      'bg-slate-100 text-slate-600 dark:bg-slate-500/20 dark:text-slate-300',
    Error:              'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400',
    ApiError:           'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400',
    ConfigurationError: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
  };
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${map[status] ?? 'bg-slate-100 text-slate-600'}`}>
      {status}
    </span>
  );
}

function SeverityPill({ severity }: { severity: string }) {
  const cls =
    severity === 'Critical' ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400' :
    severity === 'High'     ? 'bg-orange-100 text-orange-700 dark:bg-orange-500/20 dark:text-orange-400' :
    severity === 'Medium'   ? 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400' :
                              'bg-sky-100 text-sky-700 dark:bg-sky-500/20 dark:text-sky-400';
  return <span className={`rounded-full px-2 py-0.5 text-[11px] font-bold uppercase ${cls}`}>{severity}</span>;
}

function ProgressBar({ percent }: { percent: number }) {
  const colour = percent >= 90 ? 'bg-emerald-500' : percent >= 60 ? 'bg-amber-500' : 'bg-rose-500';
  return (
    <div className="h-2 w-full overflow-hidden rounded-full bg-slate-200 dark:bg-slate-700">
      <div className={`h-full transition-all ${colour}`} style={{ width: `${Math.min(100, Math.max(0, percent))}%` }} />
    </div>
  );
}

function ScoreRing({ score }: { score: number }) {
  const colour = score >= 90 ? 'text-emerald-700 dark:text-emerald-400' : score >= 70 ? 'text-amber-800 dark:text-amber-400' : 'text-rose-700 dark:text-rose-400';
  const bgRing = score >= 90 ? 'bg-emerald-50 dark:bg-emerald-900/20' : score >= 70 ? 'bg-amber-50 dark:bg-amber-900/20' : 'bg-rose-50 dark:bg-rose-900/20';
  return (
    <div className={`flex h-20 w-20 flex-col items-center justify-center rounded-full border-4 ${bgRing} ${score >= 90 ? 'border-emerald-200 dark:border-emerald-700' : score >= 70 ? 'border-amber-200 dark:border-amber-700' : 'border-rose-200 dark:border-rose-700'}`}>
      <span className={`text-2xl font-black ${colour}`}>{score}</span>
      <span className="text-[10px] font-semibold text-slate-400">/ 100</span>
    </div>
  );
}

function ModuleStatusChip({ label, enabled, score }: { label: string; enabled: boolean; score?: number }) {
  const ok = enabled && (score === undefined || score >= 90);
  const warn = enabled && score !== undefined && score < 90 && score >= 60;
  const cls = ok    ? 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-400 dark:border-emerald-800' :
              warn  ? 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-400 dark:border-amber-800' :
                      'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-400 dark:border-rose-800';
  const icon = ok ? <CheckCircle className="h-3 w-3" /> : warn ? <AlertTriangle className="h-3 w-3" /> : <XCircle className="h-3 w-3" />;
  return (
    <span className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 text-xs font-semibold ${cls}`}>
      {icon} {label}
    </span>
  );
}

function SectionCard({ icon: Icon, title, badge, children }: {
  icon: React.ElementType; title: string; badge?: React.ReactNode; children: React.ReactNode;
}) {
  return (
    <div className="surface flex flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
        <div className="flex items-center gap-2">
          <span className="grid h-7 w-7 place-items-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
            <Icon className="h-3.5 w-3.5 text-sapphire dark:text-cyanAccent" />
          </span>
          <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{title}</h2>
        </div>
        {badge}
      </div>
      <div className="flex-1 p-4">{children}</div>
    </div>
  );
}

// ── Executive score card ──────────────────────────────────────────────────────

function OverallScoreCard({ overall, qiwa, wps, gosi }: {
  overall: OverallSection; qiwa: QiwaSection; wps: WpsSection; gosi: GosiSection;
}) {
  const label = overall.complianceScore >= 90 ? 'Excellent' :
                overall.complianceScore >= 75 ? 'Good' :
                overall.complianceScore >= 60 ? 'Needs Attention' : 'At Risk';

  const labelCls = overall.complianceScore >= 90 ? 'text-emerald-600 dark:text-emerald-400' :
                   overall.complianceScore >= 75 ? 'text-amber-600 dark:text-amber-400' :
                                                   'text-rose-600 dark:text-rose-400';

  return (
    <div className="surface p-5">
      <div className="flex flex-wrap items-center gap-5">
        <ScoreRing score={overall.complianceScore} />
        <div className="flex-1 min-w-48">
          <div className="flex items-center gap-2">
            <p className="text-lg font-black text-slate-900 dark:text-white">Compliance Score</p>
            <span className={`text-sm font-bold ${labelCls}`}>{label}</span>
          </div>
          <p className="mt-0.5 text-xs text-slate-400">
            Evaluated {fmtDateTime(overall.lastEvaluatedAt)}
          </p>
          {overall.urgentActionCount > 0 && (
            <p className="mt-2 flex items-center gap-1.5 text-xs font-semibold text-rose-600 dark:text-rose-400">
              <AlertCircle className="h-3.5 w-3.5" />
              {overall.urgentActionCount} urgent action{overall.urgentActionCount !== 1 ? 's' : ''} require attention
            </p>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          <ModuleStatusChip
            label="QIWA"
            enabled={qiwa.featureEnabled}
            score={qiwa.featureEnabled ? qiwa.readinessPercent : undefined}
          />
          <ModuleStatusChip
            label="WPS"
            enabled
            score={wps.blockingIssues.length === 0 ? 100 : wps.blockingIssues.length === 1 ? 65 : 30}
          />
          <ModuleStatusChip
            label="GOSI"
            enabled
            score={gosi.readinessPercent}
          />
        </div>
      </div>
    </div>
  );
}

// ── Action center ─────────────────────────────────────────────────────────────

function ActionCenter({ items }: { items: ActionItem[] }) {
  const [expanded, setExpanded] = useState<string | null>(null);

  if (items.length === 0) {
    return (
      <div className="surface p-5">
        <h2 className="mb-3 text-sm font-bold text-slate-800 dark:text-slate-100">Action Center</h2>
        <p className="flex items-center gap-2 text-sm text-emerald-600 dark:text-emerald-400">
          <CheckCircle className="h-4 w-4" /> No outstanding compliance actions.
        </p>
      </div>
    );
  }

  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
        <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">Action Center</h2>
        <span className="rounded-full bg-rose-100 px-2 py-0.5 text-xs font-bold text-rose-700 dark:bg-rose-500/20 dark:text-rose-400">
          {items.length}
        </span>
      </div>
      <ul className="divide-y divide-slate-100 dark:divide-white/[0.05]">
        {items.map(item => (
          <li key={item.id} className="px-4 py-3">
            <div className="flex items-start gap-3">
              <div className="mt-0.5 shrink-0">
                <SeverityPill severity={item.severity} />
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-xs font-semibold text-slate-500 dark:text-slate-400">{item.module}</span>
                  <span className="text-sm font-semibold text-slate-800 dark:text-slate-100">{item.title}</span>
                  {item.affectedCount > 0 && (
                    <span className="rounded bg-slate-100 px-1.5 py-0.5 text-xs font-medium text-slate-600 dark:bg-white/10 dark:text-slate-300">
                      {item.affectedCount} affected
                    </span>
                  )}
                </div>
                {expanded === item.id && (
                  <div className="mt-2 space-y-2">
                    <p className="text-xs text-slate-600 dark:text-slate-300">{item.description}</p>
                    <p className="flex items-start gap-1.5 text-xs text-slate-500 dark:text-slate-400">
                      <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {item.recommendedAction}
                    </p>
                  </div>
                )}
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <button
                  type="button"
                  onClick={() => setExpanded(expanded === item.id ? null : item.id)}
                  className="text-xs text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
                >
                  {expanded === item.id ? 'Less' : 'More'}
                </button>
                {item.route && item.canAct && (
                  <a
                    href={item.route}
                    className="flex items-center gap-1 rounded-lg border border-sapphire/30 px-2.5 py-1 text-xs font-semibold text-sapphire hover:bg-sapphire/5 dark:border-cyanAccent/30 dark:text-cyanAccent dark:hover:bg-cyanAccent/5"
                  >
                    Fix <ExternalLink className="h-3 w-3" />
                  </a>
                )}
              </div>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ── QIWA module card ──────────────────────────────────────────────────────────

function QiwaCard({ qiwa }: { qiwa: QiwaSection }) {
  return (
    <SectionCard
      icon={Wifi}
      title="QIWA"
      badge={<ConnectionBadge status={qiwa.featureEnabled ? qiwa.connectionStatus : 'NotConfigured'} />}
    >
      <div className="space-y-4">
        {!qiwa.featureEnabled ? (
          <p className="text-xs text-slate-400 italic">QIWA module is not enabled for this tenant.</p>
        ) : (
          <>
            <dl className="grid grid-cols-2 gap-2 text-xs">
              <div>
                <dt className="text-slate-400">Credentials</dt>
                <dd className={`font-semibold ${qiwa.credentialConfigured ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>
                  {qiwa.credentialConfigured ? 'Configured' : 'Not Set'}
                </dd>
              </div>
              <div>
                <dt className="text-slate-400">Blocked</dt>
                <dd className={`font-semibold ${qiwa.blockedFromSync > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-slate-800 dark:text-slate-200'}`}>
                  {qiwa.blockedFromSync}
                </dd>
              </div>
              <div>
                <dt className="text-slate-400">Failed syncs</dt>
                <dd className={`font-semibold ${qiwa.failedSyncCount > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-800 dark:text-slate-200'}`}>
                  {qiwa.failedSyncCount}
                </dd>
              </div>
              <div>
                <dt className="text-slate-400">Last sync</dt>
                <dd className="text-slate-600 dark:text-slate-300">{fmtDate(qiwa.lastSuccessfulSync)}</dd>
              </div>
            </dl>

            <div className="space-y-1">
              <div className="flex justify-between text-xs text-slate-500">
                <span>Readiness</span>
                <span>{qiwa.readinessPercent}% ({qiwa.readyForSync}/{qiwa.totalEmployees})</span>
              </div>
              <ProgressBar percent={qiwa.readinessPercent} />
            </div>

            {qiwa.blockedEmployees.length > 0 && (
              <div className="max-h-40 overflow-auto rounded-lg border border-slate-100 dark:border-white/[0.07]">
                <table className="w-full text-left text-xs">
                  <thead className="sticky top-0 bg-slate-50 text-slate-500 dark:bg-slate-800">
                    <tr>
                      <th className="px-2 py-1.5">Code</th>
                      <th className="px-2 py-1.5">Name</th>
                      <th className="px-2 py-1.5">Missing</th>
                    </tr>
                  </thead>
                  <tbody>
                    {qiwa.blockedEmployees.map(e => (
                      <tr key={e.employeeId} className="border-t border-slate-100 dark:border-white/[0.05]">
                        <td className="px-2 py-1.5 font-medium">{e.employeeCode}</td>
                        <td className="px-2 py-1.5">{e.fullName}</td>
                        <td className="px-2 py-1.5 text-rose-600 dark:text-rose-400">{e.missingFields.join(', ')}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </div>
    </SectionCard>
  );
}

// ── WPS module card ───────────────────────────────────────────────────────────

function WpsCard({ wps }: { wps: WpsSection }) {
  const runStatusOk = wps.lastRunStatus === 'Locked' || wps.lastRunStatus === 'Paid';
  return (
    <SectionCard
      icon={Banknote}
      title="WPS"
      badge={wps.lastRunStatus ? <ConnectionBadge status={wps.lastRunStatus} /> : undefined}
    >
      <div className="space-y-4">
        <dl className="grid grid-cols-2 gap-2 text-xs">
          <div>
            <dt className="text-slate-400">Last run period</dt>
            <dd className="font-semibold text-slate-800 dark:text-slate-200">{wps.lastRunPeriod ?? '—'}</dd>
          </div>
          <div>
            <dt className="text-slate-400">Pending approvals</dt>
            <dd className={`font-semibold ${wps.pendingApprovals > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-800 dark:text-slate-200'}`}>
              {wps.pendingApprovals}
            </dd>
          </div>
          <div>
            <dt className="text-slate-400">Missing IBANs</dt>
            <dd className={`font-semibold ${wps.missingIbanCount > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
              {wps.missingIbanCount}
            </dd>
          </div>
          <div>
            <dt className="text-slate-400">SIF exports</dt>
            <dd className="font-semibold text-slate-800 dark:text-slate-200">{wps.exportHistoryCount}</dd>
          </div>
        </dl>

        {wps.lastExportDate && (
          <div className="rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 dark:border-white/[0.07] dark:bg-white/[0.03]">
            <p className="text-xs font-semibold text-slate-600 dark:text-slate-300">Last SIF Export</p>
            <p className="text-xs text-slate-400">{fmtDateTime(wps.lastExportDate)} — {wps.lastExportStatus ?? '—'}</p>
          </div>
        )}

        <div>
          <p className="mb-1.5 text-xs font-semibold text-slate-500">Blocking issues</p>
          {wps.blockingIssues.length === 0 ? (
            <p className="flex items-center gap-1.5 text-xs text-emerald-600 dark:text-emerald-400">
              <CheckCircle className="h-3.5 w-3.5" /> None
            </p>
          ) : (
            <ul className="space-y-1">
              {wps.blockingIssues.map((it, i) => (
                <li key={i} className="flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-400">
                  <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {it}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </SectionCard>
  );
}

// ── GOSI module card ──────────────────────────────────────────────────────────

function GosiCard({ gosi }: { gosi: GosiSection }) {
  const readinessColor = gosi.readinessPercent >= 90 ? 'text-emerald-600 dark:text-emerald-400' :
                         gosi.readinessPercent >= 60 ? 'text-amber-600 dark:text-amber-400' :
                                                       'text-rose-600 dark:text-rose-400';
  return (
    <SectionCard
      icon={Users}
      title="GOSI"
      badge={
        <span className={`text-sm font-black ${readinessColor}`}>
          {gosi.readinessPercent}%
        </span>
      }
    >
      <div className="space-y-4">
        <div className="space-y-1">
          <div className="flex justify-between text-xs text-slate-500">
            <span>Readiness</span>
            <span>{gosi.readyCount} ready / {gosi.blockedCount} blocked</span>
          </div>
          <ProgressBar percent={gosi.readinessPercent} />
        </div>

        <dl className="grid grid-cols-2 gap-2 text-xs">
          <div>
            <dt className="text-slate-400">Missing GOSI ref</dt>
            <dd className={`font-semibold ${gosi.employeesMissingGosiRef > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-slate-800 dark:text-slate-200'}`}>
              {gosi.employeesMissingGosiRef}
            </dd>
          </div>
          <div>
            <dt className="text-slate-400">Missing employer ID</dt>
            <dd className={`font-semibold ${gosi.employeesMissingGosiEmployerId > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-slate-800 dark:text-slate-200'}`}>
              {gosi.employeesMissingGosiEmployerId > 0 ? 'Not set' : 'Set'}
            </dd>
          </div>
          {gosi.gccEmployeeCount > 0 && (
            <div>
              <dt className="text-slate-400">GCC employees</dt>
              <dd className="font-semibold text-amber-600 dark:text-amber-400">{gosi.gccEmployeeCount} ⚠️</dd>
            </div>
          )}
          {gosi.varianceCount > 0 && (
            <div>
              <dt className="text-slate-400">Variances (last run)</dt>
              <dd className="font-semibold text-amber-600 dark:text-amber-400">{gosi.varianceCount}</dd>
            </div>
          )}
        </dl>

        {gosi.blockedEmployees.length > 0 && (
          <div className="max-h-36 overflow-auto rounded-lg border border-slate-100 dark:border-white/[0.07]">
            <table className="w-full text-left text-xs">
              <thead className="sticky top-0 bg-slate-50 text-slate-500 dark:bg-slate-800">
                <tr>
                  <th className="px-2 py-1.5">Code</th>
                  <th className="px-2 py-1.5">Name</th>
                  <th className="px-2 py-1.5">Issues</th>
                </tr>
              </thead>
              <tbody>
                {gosi.blockedEmployees.map(e => (
                  <tr key={e.employeeId} className="border-t border-slate-100 dark:border-white/[0.05]">
                    <td className="px-2 py-1.5 font-medium">{e.employeeCode}</td>
                    <td className="px-2 py-1.5">{e.fullName}</td>
                    <td className="px-2 py-1.5 text-rose-600 dark:text-rose-400">{e.blockingIssueCodes.join(', ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {gosi.warnings.length > 0 && (
          <ul className="space-y-1">
            {gosi.warnings.map((w, i) => (
              <li key={i} className="flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-400">
                <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {w}
              </li>
            ))}
          </ul>
        )}
      </div>
    </SectionCard>
  );
}

// ── Dashboard Tab ─────────────────────────────────────────────────────────────

function DashboardTab() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const res = await client.get<Dashboard>('/api/saudi-compliance/dashboard');
      setData(res.data);
    } catch {
      setError('Unable to load Saudi compliance data. You may not have access or the module is not enabled.');
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, []);

  if (loading) {
    return (
      <div className="space-y-4">
        <div className="surface h-28 animate-pulse" />
        <div className="surface h-48 animate-pulse" />
        <div className="grid gap-4 lg:grid-cols-3">
          {[0, 1, 2].map(i => <div key={i} className="surface h-56 animate-pulse" />)}
        </div>
      </div>
    );
  }

  if (error || !data) {
    return (
      <div className="surface flex flex-col items-center gap-3 p-10 text-center">
        <FileWarning className="h-8 w-8 text-amber-500" />
        <p className="text-sm text-slate-600 dark:text-slate-300">{error ?? 'No data available.'}</p>
        <button type="button" onClick={load} className="rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white">
          Retry
        </button>
      </div>
    );
  }

  const { overall, qiwa, wps, gosi, actionItems } = data;

  return (
    <div className="space-y-5">
      <div className="flex justify-end">
        <button
          type="button"
          onClick={load}
          className="flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-white/[0.04]"
        >
          <RefreshCw className="h-3.5 w-3.5" /> Refresh
        </button>
      </div>

      {/* Executive Score Card */}
      <OverallScoreCard overall={overall} qiwa={qiwa} wps={wps} gosi={gosi} />

      {/* Action Center */}
      <ActionCenter items={actionItems} />

      {/* Module cards */}
      <div className="grid gap-4 lg:grid-cols-3">
        <QiwaCard qiwa={qiwa} />
        <WpsCard wps={wps} />
        <GosiCard gosi={gosi} />
      </div>
    </div>
  );
}

// ── Main Component ────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'configure';

const TABS: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'dashboard', label: 'Dashboard', icon: ShieldCheck },
  { id: 'configure', label: 'Configure',  icon: Settings },
];

export function SaudiComplianceDashboard() {
  const [tab, setTab] = useState<Tab>('dashboard');

  return (
    <div className="space-y-5">
      <div className="flex items-center gap-2">
        <ShieldCheck className="h-6 w-6 text-sapphire dark:text-cyanAccent" />
        <h1 className="text-xl font-semibold text-slate-800 dark:text-slate-100">Saudi Compliance Command Center</h1>
      </div>

      <div className="flex gap-1 border-b border-slate-200 dark:border-white/[0.08]">
        {TABS.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            onClick={() => setTab(id)}
            className={`flex items-center gap-2 border-b-2 px-4 pb-2.5 pt-2 text-sm font-semibold transition ${
              tab === id
                ? 'border-sapphire text-sapphire dark:border-cyanAccent dark:text-cyanAccent'
                : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'
            }`}
          >
            <Icon className="h-4 w-4" />
            {label}
          </button>
        ))}
      </div>

      {tab === 'dashboard' && <DashboardTab />}
      {tab === 'configure' && <SaudiComplianceConfig />}
    </div>
  );
}

export default SaudiComplianceDashboard;
