'use client';

import { InfoTip } from '../components/InfoTip';
import { msg } from '../i18n/translations';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { enumLabel } from '../i18n/enumLabel';
import { Modal as BaseModal } from '../components/Modal';
import { ConfirmDialog, type ConfirmState } from '../components/ui/ConfirmDialog';
import { useAppToast } from '../components/ui/AppToast';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';
import { EmployeeSearchSelect } from '../components/EmployeeSearchSelect';
import type { EmployeeSelection } from '../components/EmployeeSearchSelect';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useAuth } from '../contexts/AuthContext';
import { formatCalendarDate } from '../lib/calendarDate';
import { apiErrorReason, notifyApiError } from '../api/client';
import { canOpenPerformanceTab, landingPerformanceTab, performanceCapabilities } from '../lib/performanceAccess';
import type { PerformanceTab } from '../lib/performanceAccess';
import { useEffect, useState } from 'react';
import { usePagedList } from '../hooks/usePagedList';
import { ListWindowFooter } from '../components/ListWindowFooter';
import { requestFailureReason } from '../lib/requestFailure';
import {
  Activity, AlertTriangle, BarChart2, CheckCircle, ChevronRight,
  ClipboardList, Clock, FileText, Plus, Settings, Star,
  Target, TrendingUp, Users, Zap,
} from 'lucide-react';
import {
  analyticsApi, calibrationApi, cyclesApi, feedbackApi, goalsApi,
  pipApi, probationApi, recommendationsApi, reviewsApi, templatesApi,
} from '../api/performance';
import type {
  AppraisalAppeal, AppraisalReview, BonusRecommendation, CycleAnalytics, EmployeeGoal,
  IncrementRecommendation, PerformanceCycle, PerformanceImprovementPlan,
  PipTerminationQueueItem, ProbationReview, PromotionRecommendation, ScorecardTemplate,
} from '../api/performance';

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined, locale: string) {
  return formatCalendarDate(s, locale);
}

type Tr = (key: string, params?: Record<string, string | number | null | undefined>) => string;

/** The viewer-language text for a domain value the API sends as an English word (status, type, category, decision). */
function valueText(t: Tr, v: string): string {
  const known: Record<string, string> = {
    Draft: t('Draft'), Active: t('Active'), InReview: t('In Review'), Calibration: t('Calibration'),
    FinalApproval: t('Final Approval'), Published: t('Published'), Closed: t('Closed'), Pending: t('Pending'),
    Approved: t('Approved'), Rejected: t('Rejected'), SelfAssessmentDue: t('Self-Assessment Due'),
    SelfAssessmentSubmitted: t('Self-Assessment Submitted'), ManagerReview: t('Manager Review'),
    ManagerReviewed: t('Manager Reviewed'), Acknowledged: t('Acknowledged'), Appealed: t('Appealed'),
    Completed: t('Completed'), OnHold: t('On Hold'), Cancelled: t('Cancelled'), Improved: t('Improved'),
    Extended: t('Extended'), Failed: t('Failed'), TerminationRecommended: t('Termination Recommended'),
    Confirmed: t('Confirmed'), Terminated: t('Terminated'), Upheld: t('Upheld'),
    Annual: t('Annual'), 'Semi-Annual': t('Semi-Annual'), Quarterly: t('Quarterly'), Monthly: t('Monthly'),
    Probation: t('Probation'), Project: t('Project'),
    KPI: t('KPI'), Learning: t('Learning'), Behavioral: t('Behavioral'), Strategic: t('Strategic'),
    High: t('High'), Medium: t('Medium'), Low: t('Low'),
    Numeric: t('Numeric'), Percentage: t('Percentage'), Boolean: t('Boolean'), Currency: t('Currency'), Rating: t('Rating'),
    Commendation: t('Commendation'), Concern: t('Concern'), Coaching: t('Coaching'),
    Developmental: t('Developmental'), Recognition: t('Recognition'),
  };
  return known[v] ?? enumLabel(t, 'Status', v);
}

function scoreColor(score: number) {
  if (score >= 85) return 'text-emerald-600 dark:text-emerald-400';
  if (score >= 60) return 'text-sapphire dark:text-cyanAccent';
  if (score > 0) return 'text-rose-600 dark:text-rose-400';
  return 'text-slate-400';
}

function StatusBadge({ status }: { status: string }) {
  const { t } = useLocale();
  const map: Record<string, string> = {
    Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    Active: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    InReview: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Calibration: 'bg-purple-50 text-purple-700 dark:bg-purple-500/10 dark:text-purple-400',
    FinalApproval: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
    Published: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
    Closed: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
    Pending: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
    Approved: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Rejected: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    SelfAssessmentDue: 'bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-400',
    SelfAssessmentSubmitted: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    ManagerReview: 'bg-indigo-50 text-indigo-700 dark:bg-indigo-500/10 dark:text-indigo-400',
    Acknowledged: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
    Appealed: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  };
  const cls = map[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-lg px-2 py-0.5 text-xs font-medium ${cls}`}>{valueText(t, status)}</span>;
}

// ── KPI Card ──────────────────────────────────────────────────────────────────

function KpiCard({ label, value, sub, icon: Icon, color }: {
  label: string; value: string | number; sub?: string;
  icon: React.ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' | 'false' }>; color: string;
}) {
  return (
    <div className="surface flex items-center gap-4 p-4">
      <div className={`grid h-10 w-10 shrink-0 place-items-center rounded-xl ${color}`}>
        <Icon className="h-5 w-5" aria-hidden="true" />
      </div>
      <div className="min-w-0">
        <p className="text-2xl font-semibold tabular-nums text-slate-900 dark:text-white">{value}</p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">{label}</p>
        {sub && <p className="text-caption text-slate-400 dark:text-slate-500">{sub}</p>}
      </div>
    </div>
  );
}

// ── Modal shell ───────────────────────────────────────────────────────────────

function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  const { t } = useLocale();
  return <BaseModal isOpen title={title} onClose={onClose} closeLabel={t('Close')} size={wide ? 'lg' : 'md'}>{children}</BaseModal>;
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
};

/** A list that failed to load says so, instead of rendering as "No … found". */
function LoadFailure({ what, error }: { what: 'reviews' | 'goals'; error: unknown }) {
  const { t } = useLocale();
  return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{what === 'goals' ? t('Goals could not be loaded.') : t('Reviews could not be loaded.')} {requestFailureReason(error)}</p>;
}

// ── Overview Tab ──────────────────────────────────────────────────────────────

/** What the signed-in user may do here, from their effective permissions (see lib/performanceAccess). */
function usePerformanceCapabilities() {
  const { hasPermission } = useAuth();
  return performanceCapabilities(hasPermission);
}

function OverviewTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
  const { t, locale } = useLocale();
  const [dash, setDash] = useState<Awaited<ReturnType<typeof analyticsApi.dashboard>> | null>(null);
  const [goals, setGoals] = useState<{ total: number; completed: number; onTrack: number; atRisk: number; avgAchievement: number } | null>(null);

  useEffect(() => {
    analyticsApi.dashboard().then(setDash).catch(() => {});
    analyticsApi.goalsCompletion().then(setGoals).catch(() => {});
  }, []);

  const pa = dash?.pendingActions;
  const rec = dash?.recommendations;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiCard label={t('Active Cycles')} value={dash?.activeCycles?.length ?? '—'} icon={Activity} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label={t('Self-Assessment Due')} value={pa?.selfAssessmentDue ?? '—'} icon={ClipboardList} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label={t('Manager Reviews Pending')} value={pa?.managerReviewPending ?? '—'} icon={Users} color="bg-blue-100 text-blue-600 dark:bg-blue-500/20 dark:text-blue-400" />
        <KpiCard label={t('Active PIPs')} value={dash?.activePips ?? '—'} icon={AlertTriangle} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        {/* Pending actions */}
        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Pending Actions')}</h3>
          <div className="space-y-3">
            {[
              { label: t('Self-Assessment Due'), val: pa?.selfAssessmentDue ?? 0, color: 'bg-amber-400', tab: 'my-reviews' as Tab },
              { label: t('Manager Reviews'), val: pa?.managerReviewPending ?? 0, color: 'bg-sapphire', tab: 'team-reviews' as Tab },
              { label: t('Calibration Pending'), val: pa?.calibrationPending ?? 0, color: 'bg-purple-500', tab: 'calibration' as Tab },
              { label: t('Appeals Pending'), val: pa?.appealsPending ?? 0, color: 'bg-rose-500', tab: 'team-reviews' as Tab },
            ].map(row => (
              <button key={row.label} type="button" onClick={() => onNavigate(row.tab)} className="flex w-full items-center gap-3 rounded-lg p-1 text-start hover:bg-slate-50 dark:hover:bg-white/5">
                <div className={`h-2 w-2 shrink-0 rounded-full ${row.color}`} />
                <span className="flex-1 text-sm text-slate-700 dark:text-slate-300">{row.label}</span>
                <span className="text-sm font-semibold tabular-nums text-slate-900 dark:text-white">{row.val}</span>
                <ChevronRight className="h-3.5 w-3.5 text-slate-400 rtl:rotate-180" aria-hidden="true" />
              </button>
            ))}
          </div>
        </div>

        {/* Recommendations */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Pending Recommendations')}</h3>
            <button type="button" onClick={() => onNavigate('recommendations')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">{t('View all')}</button>
          </div>
          <div className="space-y-3">
            {[
              { label: t('Salary Increments'), val: rec?.incrementPending ?? 0, icon: TrendingUp },
              { label: t('Promotions'), val: rec?.promotionPending ?? 0, icon: Star },
              { label: t('Bonuses'), val: rec?.bonusPending ?? 0, icon: Zap },
            ].map(row => (
              <div key={row.label} className="flex items-center gap-3">
                <row.icon className="h-4 w-4 shrink-0 text-slate-400" aria-hidden="true" />
                <span className="flex-1 text-sm text-slate-700 dark:text-slate-300">{row.label}</span>
                <span className={`text-sm font-semibold tabular-nums ${row.val > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-400'}`}>{row.val}</span>
              </div>
            ))}
          </div>
          <p className="mt-4 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
            {t('Approved recommendations are sent to Payroll as pending actions — not applied automatically.')}
          </p>
        </div>

        {/* Goals summary */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Goal Progress')}</h3>
            <button type="button" onClick={() => onNavigate('goals')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">{t('Manage')}</button>
          </div>
          {goals ? (
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <span className="text-sm text-slate-600 dark:text-slate-400">{t('Avg Achievement')}</span>
                <span className="text-lg font-semibold text-sapphire dark:text-cyanAccent">{goals.avgAchievement}%</span>
              </div>
              <div className="h-2 rounded-full bg-slate-100 dark:bg-white/10">
                <div className="h-2 rounded-full bg-sapphire" style={{ width: `${Math.min(goals.avgAchievement, 100)}%` }} />
              </div>
              <div className="grid grid-cols-3 gap-2 pt-1">
                {[{ label: t('Completed'), val: goals.completed, c: 'text-emerald-600 dark:text-emerald-400' },
                  { label: t('On Track'), val: goals.onTrack, c: 'text-sapphire dark:text-cyanAccent' },
                  { label: t('At Risk'), val: goals.atRisk, c: 'text-rose-600 dark:text-rose-400' }].map(g => (
                  <div key={g.label} className="text-center">
                    <p className={`text-xl font-semibold tabular-nums ${g.c}`}>{g.val}</p>
                    <p className="text-caption text-slate-400">{g.label}</p>
                  </div>
                ))}
              </div>
            </div>
          ) : <p className="text-sm text-slate-400">{t('Loading…')}</p>}
        </div>
      </div>

      {/* Active cycles */}
      {dash?.activeCycles && dash.activeCycles.length > 0 && (
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Active Cycles')}</h3>
            <button type="button" onClick={() => onNavigate('cycles')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">{t('Manage cycles')}</button>
          </div>
          <div className="divide-y divide-slate-100 dark:divide-white/5">
            {(dash.activeCycles as PerformanceCycle[]).map((c) => (
              <div key={c.id} className="flex items-center justify-between py-3">
                <div>
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{c.name}</p>
                  <p className="text-xs text-slate-400">{t('{type} · ends {date}', { type: valueText(t, c.cycleType), date: fmtDate(c.reviewPeriodEnd, locale) })}</p>
                </div>
                <StatusBadge status={c.status} />
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ── Create Cycle Modal ────────────────────────────────────────────────────────

function CreateCycleModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const [form, setForm] = useState({
    name: '', cycleType: 'Annual', reviewPeriodStart: '', reviewPeriodEnd: '',
    selfAssessmentDeadline: '', managerReviewDeadline: '', calibrationDeadline: '',
    enableCalibration: true, enable360Feedback: false, enableSelfAssessment: true,
    enableForcedDistribution: false, notes: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | boolean) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.name || !form.reviewPeriodStart || !form.reviewPeriodEnd) {
      setError(t('Name and review period dates are required.')); return;
    }
    setSaving(true); setError('');
    try {
      await cyclesApi.create({
        name: form.name, cycleType: form.cycleType,
        reviewPeriodStart: form.reviewPeriodStart, reviewPeriodEnd: form.reviewPeriodEnd,
        enableCalibration: form.enableCalibration, enable360Feedback: form.enable360Feedback,
        enableSelfAssessment: form.enableSelfAssessment, enableForcedDistribution: form.enableForcedDistribution,
        selfAssessmentDeadline: form.selfAssessmentDeadline || undefined,
        managerReviewDeadline: form.managerReviewDeadline || undefined,
        calibrationDeadline: form.calibrationDeadline || undefined,
        notes: form.notes || undefined,
      });
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Failed to create cycle.'))); setSaving(false); }
  };

  return (
    <Modal title={t('New Performance Cycle')} onClose={onClose}>
      <div className="space-y-4">
        <Field label={t('Cycle Name *')}><input className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder={t('Example: Annual Appraisal 2026')} /></Field>
        <Field label={t('Cycle Type')}>
          <select className={sel} value={form.cycleType} onChange={e => set('cycleType', e.target.value)}>
            {['Annual', 'Semi-Annual', 'Quarterly', 'Monthly', 'Probation', 'Project'].map(ct => <option key={ct} value={ct}>{valueText(t, ct)}</option>)}
          </select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Period Start *')}><input type="date" className={inp} value={form.reviewPeriodStart} onChange={e => set('reviewPeriodStart', e.target.value)} /></Field>
          <Field label={t('Period End *')}><input type="date" className={inp} value={form.reviewPeriodEnd} onChange={e => set('reviewPeriodEnd', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label={t('Self-Assessment Deadline')}><input type="date" className={inp} value={form.selfAssessmentDeadline} onChange={e => set('selfAssessmentDeadline', e.target.value)} /></Field>
          <Field label={t('Manager Review Deadline')}><input type="date" className={inp} value={form.managerReviewDeadline} onChange={e => set('managerReviewDeadline', e.target.value)} /></Field>
          <Field label={t('Calibration Deadline')}><input type="date" className={inp} value={form.calibrationDeadline} onChange={e => set('calibrationDeadline', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-2 gap-2">
          {([['enableSelfAssessment', t('Self-Assessment')], ['enableCalibration', t('Calibration')], ['enable360Feedback', t('360° Feedback')], ['enableForcedDistribution', t('Forced Distribution')]] as [keyof typeof form, string][]).map(([k, l]) => (
            <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />
              {l}
            </label>
          ))}
        </div>
        <Field label={t('Notes')}><textarea className={inp} rows={2} value={form.notes} onChange={e => set('notes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Creating…') : t('Create Cycle')}</button>
        </div>
      </div>
    </Modal>
  );
}

// ── Cycles Tab ────────────────────────────────────────────────────────────────

function CyclesTab() {
  const { t, locale } = useLocale();
  const can = usePerformanceCapabilities();
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [launching, setLaunching] = useState<string | null>(null);

  const load = () => { setLoading(true); cyclesApi.listAll().then(all => { setCycles(all); setLoading(false); }).catch(() => setLoading(false)); };
  useEffect(load, []);

  const launch = async (id: string) => {
    setLaunching(id);
    try { await cyclesApi.launch(id); load(); } catch (e) { notifyApiError(e, t('Launch failed. Ensure a default scorecard template is assigned.')); }
    setLaunching(null);
  };

  const advance = async (id: string) => {
    try { await cyclesApi.advance(id); load(); } catch (e) { notifyApiError(e, t('Could not advance cycle.')); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# cycle} other {# cycles}}', { count: cycles.length })}</p>
        {can.setUpCycles && (
          <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('New Cycle')}</button>
        )}
      </div>

      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : cycles.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Activity className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No performance cycles yet')}</p>
          <p className="text-xs text-slate-400 dark:text-slate-500">{t('Create the first cycle to begin the appraisal process.')}</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {cycles.map(c => (
            <div key={c.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{c.name}</p>
                <p className="mt-0.5 text-xs text-slate-400">{t('{type} · {start} – {end} · {count} enrolled', { type: valueText(t, c.cycleType), start: fmtDate(c.reviewPeriodStart, locale), end: fmtDate(c.reviewPeriodEnd, locale), count: c.enrolledCount ?? 0 })}</p>
              </div>
              <div className="ms-4 flex shrink-0 items-center gap-3">
                <StatusBadge status={c.status} />
                {c.status === 'Draft' && can.setUpCycles && (
                  <button type="button" onClick={() => launch(c.id)} disabled={launching === c.id} className={btn.primary}>
                    {launching === c.id ? t('Launching…') : t('Launch')}
                  </button>
                )}
                {['Active', 'InReview', 'Calibration', 'FinalApproval'].includes(c.status) && can.setUpCycles && (
                  <button type="button" onClick={() => advance(c.id)} className={btn.ghost}>{t('Advance')}</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {showCreate && <CreateCycleModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
    </div>
  );
}

// ── My Reviews Tab (Self-Assessment) ──────────────────────────────────────────

function SelfAssessmentModal({ review, onClose, onSaved }: { review: AppraisalReview; onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const [form, setForm] = useState({ kpiScore: '', competencyScore: '', productivityScore: '', notes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    const kpi = Number(form.kpiScore); const comp = Number(form.competencyScore); const prod = Number(form.productivityScore);
    if (!kpi || !comp || !prod) { setError(t('Enter scores for KPI, Competency, and Productivity (0–100).')); return; }
    setSaving(true); setError('');
    try {
      await reviewsApi.submitSelfAssessment(review.id, { notes: form.notes, kpiScore: kpi, competencyScore: comp, productivityScore: prod });
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Submission failed.'))); setSaving(false); }
  };

  return (
    <Modal title={t('Self-Assessment — {name}', { name: review.employeeName })} onClose={onClose}>
      <div className="space-y-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Cycle: {name}', { name: review.cycleName })}</p>
        <div className="grid grid-cols-3 gap-3">
          <Field label={t('KPI Score (0–100)')}><input type="number" min={0} max={100} className={inp} value={form.kpiScore} onChange={e => set('kpiScore', e.target.value)} /></Field>
          <Field label={t('Competency Score')}><input type="number" min={0} max={100} className={inp} value={form.competencyScore} onChange={e => set('competencyScore', e.target.value)} /></Field>
          <Field label={t('Productivity Score')}><input type="number" min={0} max={100} className={inp} value={form.productivityScore} onChange={e => set('productivityScore', e.target.value)} /></Field>
        </div>
        <Field label={t('Self-Assessment Notes')}><textarea className={inp} rows={4} placeholder={t('Describe your achievements, contributions, and areas for growth…')} value={form.notes} onChange={e => set('notes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Submitting…') : t('Submit Assessment')}</button>
        </div>
      </div>
    </Modal>
  );
}

function MyReviewsTab() {
  const { t } = useLocale();
  const [selected, setSelected] = useState<AppraisalReview | null>(null);

  // One page at a time with the server's total: this used to show the first 50 as every review.
  // view: 'mine' keeps My Reviews to the caller's own reviews; without it HR would see everyone's.
  const list = usePagedList<AppraisalReview>((page, pageSize) => reviewsApi.list({ view: 'mine', page, pageSize }));
  const { items: reviews, loading } = list;
  const load = () => { void list.reload(); };
  useEffect(load, [list.reload]);
  const reviewCount = list.total ?? reviews.length;

  const acknowledge = async (id: string) => {
    try { await reviewsApi.acknowledge(id); load(); } catch (e) { notifyApiError(e, t('Failed to acknowledge.')); }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# review record} other {# review records}}', { count: reviewCount })}</p>
      {list.error != null && <LoadFailure what="reviews" error={list.error} />}
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : list.error != null && reviews.length === 0 ? null : reviews.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <ClipboardList className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No reviews assigned')}</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {reviews.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.cycleName}</p>
                <p className="mt-0.5 text-xs text-slate-400">{r.departmentName} · {r.designationTitle}</p>
                {r.finalScore > 0 && (
                  <p className={`mt-1 text-xs font-semibold ${scoreColor(r.finalScore)}`}>{t('Score: {score} — {rating}', { score: r.finalScore, rating: r.finalRating })}</p>
                )}
              </div>
              <div className="ms-4 flex shrink-0 items-center gap-3">
                <StatusBadge status={r.status} />
                {r.status === 'SelfAssessmentDue' && (
                  <button type="button" className={btn.primary} onClick={() => setSelected(r)}>{t('Start Assessment')}</button>
                )}
                {r.status === 'Published' && (
                  <button type="button" className={btn.ghost} onClick={() => acknowledge(r.id)}>{t('Acknowledge')}</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      <ListWindowFooter shown={reviews.length} total={list.total} noun="reviews" loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} labels={{ window: t('Showing {shown} of {total} reviews', { shown: reviews.length, total: list.total ?? 0 }), loadMore: t('Load more'), loading: t('Loading…') }} />
      {selected && <SelfAssessmentModal review={selected} onClose={() => setSelected(null)} onSaved={() => { setSelected(null); load(); }} />}
    </div>
  );
}

// ── Manager Review Modal ──────────────────────────────────────────────────────

function ManagerReviewModal({ review, onClose, onSaved }: { review: AppraisalReview; onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const can = usePerformanceCapabilities();
  const [form, setForm] = useState({
    kpiScore: String(review.kpiScore || ''), competencyScore: String(review.competencyScore || ''),
    attendanceScore: String(review.attendanceScore || ''), productivityScore: String(review.productivityScore || ''),
    feedbackScore: String(review.feedbackScore || ''), disciplineScore: String(review.disciplineScore || ''),
    managerNotes: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    setSaving(true); setError('');
    try {
      await reviewsApi.submitManagerReview(review.id, {
        kpiScore: Number(form.kpiScore), competencyScore: Number(form.competencyScore),
        attendanceScore: Number(form.attendanceScore), productivityScore: Number(form.productivityScore),
        feedbackScore: Number(form.feedbackScore), disciplineScore: Number(form.disciplineScore),
        managerNotes: form.managerNotes,
      });
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Review submission failed.'))); setSaving(false); }
  };

  const autoAttendance = async () => {
    try { const r = await reviewsApi.computeAttendance(review.id); set('attendanceScore', String(r.attendanceScore)); }
    catch (e) { notifyApiError(e, t('Could not compute attendance — check attendance records.')); }
  };

  return (
    <Modal title={t('Manager Review — {name}', { name: review.employeeName })} onClose={onClose} wide>
      <div className="space-y-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Cycle: {name} · Self-assessment: {notes}', { name: review.cycleName, notes: review.selfAssessmentNotes || t('(none)') })}</p>
        <div className="grid grid-cols-3 gap-3">
          {[['kpiScore', t('KPI Score (0–100)')], ['competencyScore', t('Competency Score (0–100)')], ['productivityScore', t('Productivity Score (0–100)')],
            ['feedbackScore', t('Feedback Score (0–100)')], ['disciplineScore', t('Discipline Score (0–100)')]] .map(([k, l]) => (
            <Field key={k} label={l}><input type="number" min={0} max={100} className={inp} value={form[k as keyof typeof form]} onChange={e => set(k as keyof typeof form, e.target.value)} /></Field>
          ))}
          <Field label={t('Attendance Score (0–100)')}>
            <div className="flex gap-2">
              <input type="number" min={0} max={100} className={inp} value={form.attendanceScore} onChange={e => set('attendanceScore', e.target.value)} />
              {can.decideRatings && (
                <button type="button" onClick={autoAttendance} className={btn.ghost} title={t('Auto-compute from attendance records')} aria-label={t('Auto-compute from attendance records')}><Zap className="h-4 w-4" aria-hidden="true" /></button>
              )}
            </div>
          </Field>
        </div>
        <Field label={t('Manager Notes')}><textarea className={inp} rows={3} placeholder={t('Performance observations, achievements, areas for development…')} value={form.managerNotes} onChange={e => set('managerNotes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Submitting…') : t('Submit Review')}</button>
        </div>
      </div>
    </Modal>
  );
}

// ── Team Reviews Tab ──────────────────────────────────────────────────────────

/**
 * Open appeals, and the decision that resolves them.
 *
 * `respondToAppeal` existed in the API client with ZERO call sites, so an appeal — including one HR
 * meant to reject — could never be decided from the product. Submitting one parked the review at
 * "Appealed", and every increment, promotion and bonus for that employee was refused from then on,
 * permanently. This panel is the missing entry point.
 */
function OpenAppealsPanel({ onResolved }: { onResolved: () => void }) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [appeals, setAppeals] = useState<AppraisalAppeal[]>([]);
  const [deciding, setDeciding] = useState<{ appeal: AppraisalAppeal; decision: string } | null>(null);
  const [response, setResponse] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = () => { reviewsApi.listAppeals().then(setAppeals).catch(() => setAppeals([])); };
  useEffect(load, []);

  const decide = async () => {
    if (!deciding) return;
    if (!response.trim()) { setError(t('Record the reasoning given to the employee.')); return; }
    setSaving(true); setError('');
    try {
      const r = await reviewsApi.respondToAppeal(deciding.appeal.id, deciding.decision, response.trim());
      setDeciding(null); setResponse(''); load(); onResolved();
      const status = valueText(t, r.reviewStatus);
      toast.success(deciding.decision === 'Upheld'
        ? t('Appeal upheld. Review is now {status}. {nextStep}', { status, nextStep: r.nextStep })
        : t('Appeal rejected. Review is now {status}. {nextStep}', { status, nextStep: r.nextStep }));
    } catch (e) { setError(apiErrorReason(e, t('The decision could not be recorded.'))); }
    finally { setSaving(false); }
  };

  if (appeals.length === 0) return null;

  return (
    <div className="surface border-s-4 border-s-rose-500 p-5">
      <div className="flex items-center gap-2">
        <AlertTriangle className="h-4 w-4 text-rose-500" aria-hidden="true" />
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">
          {t('Open appeals ({count})', { count: appeals.length })}
        </h3>
      </div>
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
        {t('While an appeal is open, no increment, promotion or bonus can be raised for that employee. Decide it to release them.')}
      </p>
      <div className="mt-3 divide-y divide-slate-100 dark:divide-white/5">
        {appeals.map(a => (
          <div key={a.id} className="flex items-start justify-between gap-4 py-3">
            <div className="min-w-0">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{a.employeeName}</p>
              <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{a.appealReason}</p>
              {a.employeeJustification && (
                <p className="mt-1 text-xs text-slate-400">{a.employeeJustification}</p>
              )}
              <p className="mt-1 text-xs text-slate-400">{t('Submitted {date}', { date: fmtDate(a.submittedAt, locale) })}</p>
            </div>
            <div className="flex shrink-0 gap-1">
              <button type="button" className={btn.ghost}
                onClick={() => { setDeciding({ appeal: a, decision: 'Rejected' }); setResponse(''); setError(''); }}>
                {t('Reject appeal')}
              </button>
              <button type="button" className={btn.primary}
                onClick={() => { setDeciding({ appeal: a, decision: 'Upheld' }); setResponse(''); setError(''); }}>
                {t('Uphold appeal')}
              </button>
            </div>
          </div>
        ))}
      </div>

      {deciding && (
        <Modal title={deciding.decision === 'Upheld' ? t('Uphold appeal — {name}', { name: deciding.appeal.employeeName }) : t('Reject appeal — {name}', { name: deciding.appeal.employeeName })}
          onClose={() => setDeciding(null)}>
          <div className="space-y-4">
            <div className={`rounded-lg border p-3 text-xs ${deciding.decision === 'Upheld'
              ? 'border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-300'
              : 'border-slate-200 bg-slate-50 text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300'}`}>
              {deciding.decision === 'Upheld'
                ? t('Upholding withdraws the published result for revision. The review returns to Final Approval; revise the scores (a written reason is required) and publish again to re-issue it. Compensation stays paused until you do.')
                : t('Rejecting confirms the published result. The employee returns to where they were and becomes eligible for increment, promotion and bonus recommendations again.')}
            </div>
            <Field label={t('Response to the employee *')}>
              <textarea title={t('Appeal response')} className={inp} rows={4} value={response}
                onChange={e => setResponse(e.target.value)}
                placeholder={t('What was considered, and why this outcome…')} />
            </Field>
            {error && <p className="text-xs text-rose-500">{error}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setDeciding(null)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={decide} disabled={saving}>
                {saving ? t('Recording…') : deciding.decision === 'Upheld' ? t('Record upheld') : t('Record rejected')}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

function TeamReviewsTab() {
  const { t } = useLocale();
  const can = usePerformanceCapabilities();
  const [statusFilter, setStatusFilter] = useState('');
  const [selected, setSelected] = useState<AppraisalReview | null>(null);

  // One page at a time with the server's total: this used to show the first 50 as every review.
  const list = usePagedList<AppraisalReview>((page, pageSize) =>
    reviewsApi.list({ status: statusFilter || undefined, page, pageSize }));
  const { items: reviews, loading } = list;
  const load = () => { void list.reload(); };
  useEffect(load, [statusFilter, list.reload]);
  const reviewCount = list.total ?? reviews.length;

  const publish = async (id: string) => {
    try { await reviewsApi.publish(id); load(); } catch (e) { notifyApiError(e, t('Publish failed.')); }
  };

  return (
    <div className="space-y-4">
      {can.decideRatings && <OpenAppealsPanel onResolved={load} />}
      <div className="flex items-center gap-3">
        <select aria-label={t('Status')} className={`${sel} w-56`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">{t('All Statuses')}</option>
          {['SelfAssessmentDue', 'SelfAssessmentSubmitted', 'ManagerReview', 'Calibration', 'FinalApproval', 'Published', 'Acknowledged', 'Appealed'].map(s => (
            <option key={s} value={s}>{valueText(t, s)}</option>
          ))}
        </select>
        <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# review} other {# reviews}}', { count: reviewCount })}</p>
      </div>

      {list.error != null && <LoadFailure what="reviews" error={list.error} />}
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : list.error != null && reviews.length === 0 ? null : reviews.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Users className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No reviews match the filter')}</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {reviews.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="mt-0.5 text-xs text-slate-400">{r.departmentName} · {r.designationTitle} · {r.cycleName}</p>
                {r.finalScore > 0 && <p className={`mt-1 text-xs font-semibold ${scoreColor(r.finalScore)}`}>{t('Score: {score} — {rating}', { score: r.finalScore, rating: r.finalRating })}</p>}
              </div>
              <div className="ms-4 flex shrink-0 items-center gap-3">
                <StatusBadge status={r.status} />
                {['SelfAssessmentSubmitted', 'ManagerReview'].includes(r.status) && can.writeReviews && (
                  <button type="button" className={btn.primary} onClick={() => setSelected(r)}>{t('Review')}</button>
                )}
                {r.status === 'FinalApproval' && can.decideRatings && (
                  <button type="button" className={btn.primary} onClick={() => publish(r.id)}>{t('Publish')}</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      <ListWindowFooter shown={reviews.length} total={list.total} noun="reviews" loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} labels={{ window: t('Showing {shown} of {total} reviews', { shown: reviews.length, total: list.total ?? 0 }), loadMore: t('Load more'), loading: t('Loading…') }} />
      {selected && <ManagerReviewModal review={selected} onClose={() => setSelected(null)} onSaved={() => { setSelected(null); load(); }} />}
    </div>
  );
}

// ── Goals Tab ─────────────────────────────────────────────────────────────────

function CreateGoalModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const [selectedEmployee, setSelectedEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({
    title: '', description: '', category: 'KPI',
    kpiType: 'Numeric', measurementUnit: '', baselineValue: '0', targetValue: '', actualValue: '0',
    weight: '10', priority: 'Medium', startDate: '', dueDate: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!selectedEmployee) { setError(t('Please select an employee.')); return; }
    if (!form.title || !form.targetValue) { setError(t('Title and Target Value are required.')); return; }
    const weight = Number(form.weight);
    if (!(weight > 0 && weight <= 100)) { setError(t('Weight is this goal\'s share of the scorecard: enter a percentage above 0 and up to 100.')); return; }
    setSaving(true); setError('');
    try {
      await goalsApi.create({
        employeeId: selectedEmployee.intId, employeeName: selectedEmployee.fullName,
        title: form.title, description: form.description, category: form.category,
        kpiType: form.kpiType, measurementUnit: form.measurementUnit,
        baselineValue: Number(form.baselineValue),
        targetValue: Number(form.targetValue), actualValue: Number(form.actualValue),
        weight, priority: form.priority,
        startDate: form.startDate || undefined, dueDate: form.dueDate || undefined,
      });
      onSaved();
    } catch (e) { setError(goalApiMessage(e, t('Failed to create goal.'))); setSaving(false); }
  };

  return (
    <Modal title={t('Add Goal / KPI')} onClose={onClose} wide>
      <div className="space-y-4">
        <Field label={t('Employee *')}>
          <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
        </Field>
        <Field label={t('Goal Title *')}><input className={inp} value={form.title} onChange={e => set('title', e.target.value)} placeholder={t('Example: Achieve 95% customer satisfaction score')} /></Field>
        <Field label={t('Description')}><textarea className={inp} rows={2} value={form.description} onChange={e => set('description', e.target.value)} /></Field>
        <div className="grid grid-cols-3 gap-3">
          <Field label={t('Category')}>
            <select className={sel} value={form.category} onChange={e => set('category', e.target.value)}>
              {['KPI', 'Learning', 'Behavioral', 'Project', 'Strategic'].map(c => <option key={c} value={c}>{valueText(t, c)}</option>)}
            </select>
          </Field>
          <Field label={t('Priority')}>
            <select className={sel} value={form.priority} onChange={e => set('priority', e.target.value)}>
              {['High', 'Medium', 'Low'].map(p => <option key={p} value={p}>{valueText(t, p)}</option>)}
            </select>
          </Field>
          <Field label={t('Measurement')}>
            <select className={sel} value={form.kpiType} onChange={e => set('kpiType', e.target.value)}>
              {['Numeric', 'Percentage', 'Boolean', 'Currency', 'Rating'].map(k => <option key={k} value={k}>{valueText(t, k)}</option>)}
            </select>
          </Field>
        </div>
        <div className="grid grid-cols-4 gap-3">
          <Field label={t('Baseline')}><input type="number" className={inp} value={form.baselineValue} onChange={e => set('baselineValue', e.target.value)} /></Field>
          <Field label={t('Target *')}><input type="number" className={inp} value={form.targetValue} onChange={e => set('targetValue', e.target.value)} /></Field>
          <Field label={t('Actual')}><input type="number" className={inp} value={form.actualValue} onChange={e => set('actualValue', e.target.value)} /></Field>
          <Field label={t('Unit')}><input className={inp} value={form.measurementUnit} onChange={e => set('measurementUnit', e.target.value)} placeholder={t('Example: %')} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label={t('Weight (%)')}><input type="number" min={1} max={100} className={inp} value={form.weight} onChange={e => set('weight', e.target.value)} /></Field>
          <Field label={t('Start Date')}><input type="date" title={t('Start Date')} className={inp} value={form.startDate} onChange={e => set('startDate', e.target.value)} /></Field>
          <Field label={t('Due Date')}><input type="date" title={t('Due Date')} className={inp} value={form.dueDate} onChange={e => set('dueDate', e.target.value)} /></Field>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Add Goal')}</button>
        </div>
      </div>
    </Modal>
  );
}

/** The server's reason for refusing a goal action, so the user sees why rather than "failed". */
function goalApiMessage(e: unknown, fallback: string) {
  return (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? fallback;
}

const PRIORITY_BADGE: Record<string, string> = {
  High: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Medium: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Low: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
};

function GoalsTab() {
  const { t, locale } = useLocale();
  // Setting and approving goals is the reviewer tier (performance.write): the line manager for their own
  // reports, or HR. The server also checks the reporting line, and a refusal is shown with its reason.
  // An employee sees their own goals and records progress on the active ones.
  const can = usePerformanceCapabilities();
  const canApproveGoals = can.writeReviews;
  const [approvingId, setApprovingId] = useState<string | null>(null);
  const [goalActionError, setGoalActionError] = useState('');
  const [progressError, setProgressError] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [statusFilter, setStatusFilter] = useState('');
  const [progressGoal, setProgressGoal] = useState<EmployeeGoal | null>(null);
  const [progressVal, setProgressVal] = useState('');
  const [progressNotes, setProgressNotes] = useState('');
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);

  // One page at a time with the server's total: this used to show the first 50 as every goal.
  const list = usePagedList<EmployeeGoal>((page, pageSize) =>
    goalsApi.list({ status: statusFilter || undefined, page, pageSize }));
  const { items: goals, loading } = list;
  const load = () => { void list.reload(); };
  useEffect(load, [statusFilter, list.reload]);
  const goalCount = list.total ?? goals.length;

  // A goal is agreed before it is measured: approving a draft makes it Active, which is what opens
  // progress updates. The server refuses progress on anything that is not Active.
  const approveGoal = async (id: string) => {
    setApprovingId(id); setGoalActionError('');
    try { await goalsApi.approve(id); load(); }
    catch (e) { setGoalActionError(goalApiMessage(e, t('The goal could not be approved. Check that you approve for this employee.'))); }
    finally { setApprovingId(null); }
  };

  const updateProgress = async () => {
    if (!progressGoal || !progressVal) return;
    setProgressError('');
    try { await goalsApi.updateProgress(progressGoal.id, Number(progressVal), progressNotes); setProgressGoal(null); load(); }
    catch (e) { setProgressError(goalApiMessage(e, t('Progress could not be saved.'))); }
  };

  const confirmDelete = async () => {
    if (!deleteId) return;
    setDeleting(true);
    try { await goalsApi.delete(deleteId); setDeleteId(null); load(); }
    catch (e) { notifyApiError(e, t('Delete failed. Goal may be approved.')); }
    setDeleting(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select aria-label={t('Status')} className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">{t('All Statuses')}</option>
          {['Draft', 'Active', 'Completed', 'OnHold', 'Cancelled'].map(s => <option key={s} value={s}>{valueText(t, s)}</option>)}
        </select>
        <p className="flex-1 text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# goal} other {# goals}}', { count: goalCount })}</p>
        {can.writeReviews && (
          <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('Add Goal')}</button>
        )}
      </div>
      {goalActionError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{goalActionError}</p>}

      {list.error != null && <LoadFailure what="goals" error={list.error} />}
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : list.error != null && goals.length === 0 ? null : goals.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Target className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No goals found')}</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {goals.map(g => (
            <div key={g.id} className="px-5 py-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{g.title}</p>
                  <p className="mt-0.5 text-xs text-slate-400">{g.dueDate
                    ? t('{employee} · {category} · {type} · Weight: {weight}% · Due {date}', { employee: g.employeeName, category: valueText(t, g.category), type: valueText(t, g.kpiType), weight: g.weight, date: fmtDate(g.dueDate, locale) })
                    : t('{employee} · {category} · {type} · Weight: {weight}%', { employee: g.employeeName, category: valueText(t, g.category), type: valueText(t, g.kpiType), weight: g.weight })}</p>
                  <div className="mt-2 flex items-center gap-4">
                    <div className="flex-1">
                      <div className="h-1.5 w-48 rounded-full bg-slate-100 dark:bg-white/10">
                        <div className={`h-1.5 rounded-full ${g.achievementPct >= 75 ? 'bg-emerald-500' : g.achievementPct >= 50 ? 'bg-amber-400' : 'bg-rose-400'}`} style={{ width: `${Math.min(g.achievementPct, 100)}%` }} />
                      </div>
                    </div>
                    <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{g.achievementPct}%</span>
                    <span className="text-xs text-slate-400">{g.actualValue} / {g.targetValue} {g.measurementUnit}</span>
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <span className={`inline-flex rounded-lg px-2 py-0.5 text-xs font-medium ${PRIORITY_BADGE[g.priority] ?? PRIORITY_BADGE.Medium}`}>{valueText(t, g.priority)}</span>
                  <StatusBadge status={g.status} />
                  {g.status === 'Draft' && canApproveGoals && (
                    <button type="button" className={btn.primary} disabled={approvingId !== null} onClick={() => approveGoal(g.id)}>
                      {approvingId === g.id ? t('Approving…') : t('Approve goal')}
                    </button>
                  )}
                  {g.status === 'Active' && (
                    <button type="button" className={btn.ghost} onClick={() => { setProgressGoal(g); setProgressVal(String(g.actualValue)); setProgressNotes(''); setProgressError(''); }}>{t('Update progress')}</button>
                  )}
                  {!g.managerApproved && can.writeReviews && (
                    <button type="button" className="rounded px-2 py-1 text-xs text-rose-600 hover:bg-rose-50 dark:text-rose-400 dark:hover:bg-rose-900/20" onClick={() => setDeleteId(g.id)}>{t('Delete')}</button>
                  )}
                </div>
              </div>
              {g.status === 'Draft' && (
                <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
                  {canApproveGoals
                    ? t('Next step: approve this goal to make it active — progress can be recorded once it is active.')
                    : t('Next step: the employee\'s manager or HR approves this goal — progress can be recorded once it is active.')}
                </p>
              )}
            </div>
          ))}
        </div>
      )}
      <ListWindowFooter shown={goals.length} total={list.total} noun="goals" loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} labels={{ window: t('Showing {shown} of {total} goals', { shown: goals.length, total: list.total ?? 0 }), loadMore: t('Load more'), loading: t('Loading…') }} />

      {showCreate && <CreateGoalModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
      {progressGoal && (
        <Modal title={t('Update Progress — {title}', { title: progressGoal.title })} onClose={() => setProgressGoal(null)}>
          <div className="space-y-4">
            <Field label={t('New Actual Value (target: {target} {unit})', { target: progressGoal.targetValue, unit: progressGoal.measurementUnit })}>
              <input type="number" title={t('New Actual Value')} className={inp} value={progressVal} onChange={e => setProgressVal(e.target.value)} />
            </Field>
            <Field label={t('Notes')}><textarea className={inp} value={progressNotes} onChange={e => setProgressNotes(e.target.value)} /></Field>
            {progressError && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{progressError}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setProgressGoal(null)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={updateProgress}>{t('Save Progress')}</button>
            </div>
          </div>
        </Modal>
      )}
      {deleteId && (
        <Modal title={t('Delete Goal')} onClose={() => setDeleteId(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-700 dark:text-slate-300">{t('This will permanently remove the goal and its progress history. Approved goals cannot be deleted.')}</p>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setDeleteId(null)}>{t('Cancel')}</button>
              <button type="button" className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50" onClick={confirmDelete} disabled={deleting}>{deleting ? t('Deleting…') : t('Delete Goal')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Scorecard Templates Tab ───────────────────────────────────────────────────

function TemplateFormModal({ tpl, onClose, onSaved }: { tpl?: ScorecardTemplate; onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const [form, setForm] = useState({
    name: tpl?.name ?? '', departmentName: tpl?.departmentName ?? '', designationTitle: tpl?.designationTitle ?? '',
    grade: tpl?.grade ?? '', kpiWeight: tpl?.kpiWeight ?? 40, competencyWeight: tpl?.competencyWeight ?? 20,
    attendanceWeight: tpl?.attendanceWeight ?? 10, productivityWeight: tpl?.productivityWeight ?? 15,
    feedbackWeight: tpl?.feedbackWeight ?? 10, disciplineWeight: tpl?.disciplineWeight ?? 5,
    minPassingScore: tpl?.minPassingScore ?? 60, requiresCalibration: tpl?.requiresCalibration ?? true,
    requires360Feedback: tpl?.requires360Feedback ?? false, isDefault: tpl?.isDefault ?? false,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number | boolean) => setForm(f => ({ ...f, [k]: v }));

  const totalWeight = form.kpiWeight + form.competencyWeight + form.attendanceWeight + form.productivityWeight + form.feedbackWeight + form.disciplineWeight;

  const save = async () => {
    if (!form.name) { setError(t('Name is required.')); return; }
    if (totalWeight !== 100) { setError(t('Weights must total 100%. Currently: {total}%', { total: totalWeight })); return; }
    setSaving(true); setError('');
    try {
      if (tpl) await templatesApi.update(tpl.id, form);
      else await templatesApi.create(form);
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Save failed.'))); setSaving(false); }
  };

  const weights: [keyof typeof form, string][] = [
    ['kpiWeight', t('KPI / Goals (%)')], ['competencyWeight', t('Competencies (%)')], ['attendanceWeight', t('Attendance (%)')],
    ['productivityWeight', t('Productivity (%)')], ['feedbackWeight', t('Feedback (%)')], ['disciplineWeight', t('Discipline (%)')],
  ];

  return (
    <Modal title={tpl ? t('Edit Template') : t('New Scorecard Template')} onClose={onClose} wide>
      <div className="space-y-4">
        <Field label={t('Template Name *')}><input className={inp} value={form.name} onChange={e => set('name', e.target.value)} /></Field>
        <div className="grid grid-cols-3 gap-3">
          <Field label={t('Department')}><input className={inp} value={form.departmentName} onChange={e => set('departmentName', e.target.value)} placeholder={t('All departments')} /></Field>
          <Field label={t('Designation')}><input className={inp} value={form.designationTitle} onChange={e => set('designationTitle', e.target.value)} placeholder={t('All designations')} /></Field>
          <Field label={t('Grade')}><input className={inp} value={form.grade} onChange={e => set('grade', e.target.value)} placeholder={t('All grades')} /></Field>
        </div>

        <div>
          <div className="mb-2 flex items-center justify-between">
            <p className="text-xs font-medium text-slate-600 dark:text-slate-300">{t('Component Weights')}</p>
            <span className={`text-xs font-semibold ${totalWeight === 100 ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>{t('Total: {total}%', { total: totalWeight })}</span>
          </div>
          <div className="grid grid-cols-3 gap-3">
            {weights.map(([k, l]) => (
              <Field key={k} label={l}>
                <input type="number" min={0} max={100} className={inp} value={form[k] as number} onChange={e => set(k, Number(e.target.value))} />
              </Field>
            ))}
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Min Passing Score')}><input type="number" min={0} max={100} className={inp} value={form.minPassingScore} onChange={e => set('minPassingScore', Number(e.target.value))} /></Field>
        </div>
        <div className="flex flex-wrap gap-4">
          {([['requiresCalibration', t('Requires Calibration')], ['requires360Feedback', t('Requires 360° Feedback')], ['isDefault', t('Default Template')]] as [keyof typeof form, string][]).map(([k, l]) => (
            <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />
              {l}
            </label>
          ))}
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Saving…') : tpl ? t('Update Template') : t('Create Template')}</button>
        </div>
      </div>
    </Modal>
  );
}

/** The review scorecards editor: the Setup section Review scorecards shows it. */
export function TemplatesTab() {
  const { t } = useLocale();
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [templates, setTemplates] = useState<ScorecardTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<ScorecardTemplate | undefined>(undefined);
  const [showCreate, setShowCreate] = useState(false);

  const load = () => { setLoading(true); templatesApi.list().then(setTemplates).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  const del = (id: string) => {
    setConfirm({
      title: t('Deactivate template'),
      message: t('Deactivate this template?'),
      confirmLabel: t('Deactivate'),
      cancelLabel: t('Cancel'),
      busyLabel: t('Working…'),
      closeLabel: t('Close'),
      fallbackError: t('The action could not be completed.'),
      onConfirm: async () => {
        try { await templatesApi.delete(id); } catch (e) { notifyApiError(e, t('Delete failed.')); return; }
        load();
      },
    });
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# template} other {# templates}}', { count: templates.length })}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('New Template')}</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : templates.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Settings className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No scorecard templates yet')}</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2">
          {templates.map(tpl => (
            <div key={tpl.id} className="surface p-5">
              <div className="mb-3 flex items-start justify-between gap-2">
                <div>
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{tpl.name}</p>
                  <p className="text-xs text-slate-400">{tpl.departmentName || t('All departments')} · {tpl.designationTitle || t('All designations')} · {tpl.grade || t('All grades')}</p>
                  {tpl.isDefault && <span className="mt-1 inline-flex items-center rounded bg-sapphire/10 px-2 py-0.5 text-caption font-semibold uppercase text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent">{t('Default')}</span>}
                </div>
                <div className="flex gap-1">
                  <button type="button" className={btn.ghost} onClick={() => setEditing(tpl)}>{t('Edit')}</button>
                  <button type="button" className={btn.ghost} onClick={() => del(tpl.id)}>{t('Delete')}</button>
                </div>
              </div>
              <div className="grid grid-cols-3 gap-2">
                {[[t('KPI'), tpl.kpiWeight], [t('Competency'), tpl.competencyWeight], [t('Attendance'), tpl.attendanceWeight],
                  [t('Productivity'), tpl.productivityWeight], [t('Feedback'), tpl.feedbackWeight], [t('Discipline'), tpl.disciplineWeight]].map(([l, w]) => (
                  <div key={l} className="rounded-lg bg-slate-50 px-3 py-2 text-center dark:bg-white/5">
                    <p className="text-lg font-semibold tabular-nums text-sapphire dark:text-cyanAccent">{w}%</p>
                    <p className="text-caption text-slate-400">{l}</p>
                  </div>
                ))}
              </div>
              <p className="mt-2 text-xs text-slate-400">{tpl.requiresCalibration ? t('Pass score: {score}% · Calibration required', { score: tpl.minPassingScore }) : t('Pass score: {score}% · No calibration', { score: tpl.minPassingScore })}</p>
            </div>
          ))}
        </div>
      )}
      {showCreate && <TemplateFormModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
      {editing && <TemplateFormModal tpl={editing} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); load(); }} />}
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}

// ── Calibration Tab ───────────────────────────────────────────────────────────

function CalibrationTab() {
  const { t } = useLocale();
  const [adjustError, setAdjustError] = useState('');
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [selectedCycle, setSelectedCycle] = useState('');
  const [board, setBoard] = useState<Awaited<ReturnType<typeof calibrationApi.getBoard>> | null>(null);
  const [loading, setLoading] = useState(false);
  const [adjustModal, setAdjustModal] = useState<{ reviewId: string; name: string; current: number } | null>(null);
  const [adjustment, setAdjustment] = useState('');
  const [adjustReason, setAdjustReason] = useState('');

  useEffect(() => { cyclesApi.listAll({ status: 'Calibration' }).then(all => { setCycles(all); if (all.length > 0) setSelectedCycle(all[0].id); }).catch(() => {}); }, []);

  useEffect(() => {
    if (!selectedCycle) return;
    setLoading(true);
    calibrationApi.getBoard(selectedCycle).then(setBoard).catch(() => {}).finally(() => setLoading(false));
  }, [selectedCycle]);

  const doAdjust = async () => {
    if (!adjustModal) return;
    if (!adjustReason.trim()) { setAdjustError(t('Reason is required for calibration adjustments.')); return; }
    setAdjustError('');
    try {
      await calibrationApi.adjust(selectedCycle, adjustModal.reviewId, Number(adjustment), adjustReason);
      setAdjustModal(null); setAdjustment(''); setAdjustReason('');
      calibrationApi.getBoard(selectedCycle).then(setBoard).catch(() => {});
    } catch (e) { notifyApiError(e, t('Calibration adjustment failed.')); }
  };

  const reviews = (board?.reviews ?? []) as AppraisalReview[];
  const managerStats = (board?.managerStats ?? []) as { managerName: string; avgScore: number; count: number; possibleLeniency: boolean; possibleSeverity: boolean }[];

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select aria-label={t('Cycle')} className={`${sel} w-72`} value={selectedCycle} onChange={e => setSelectedCycle(e.target.value)}>
          <option value="">{t('Select cycle in Calibration status')}</option>
          {cycles.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </div>

      {cycles.length === 0 && (
        <div className="surface flex flex-col items-center py-16 text-center">
          <BarChart2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No cycles in Calibration status')}</p>
          <p className="text-xs text-slate-400">{t('Advance an Active cycle through InReview to reach Calibration.')}</p>
        </div>
      )}

      {loading && <p className="text-sm text-slate-400">{t('Loading board…')}</p>}

      {board && !loading && (
        <div className="space-y-4">
          {/* Manager bias alerts */}
          {managerStats.filter(m => m.possibleLeniency || m.possibleSeverity).length > 0 && (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-500/20 dark:bg-amber-500/10">
              <p className="mb-2 flex items-center gap-2 text-sm font-semibold text-amber-800 dark:text-amber-400"><AlertTriangle className="h-4 w-4" aria-hidden="true" /> {t('Bias Alerts (Human review required)')}</p>
              <div className="space-y-1">
                {managerStats.filter(m => m.possibleLeniency || m.possibleSeverity).map(m => (
                  <p key={m.managerName} className="text-xs text-amber-700 dark:text-amber-400">
                    {m.possibleLeniency
                      ? t('{manager}: avg {avg} — Possible leniency (avg > 87)', { manager: m.managerName, avg: m.avgScore })
                      : t('{manager}: avg {avg} — Possible severity (avg < 48)', { manager: m.managerName, avg: m.avgScore })}
                  </p>
                ))}
              </div>
            </div>
          )}

          {/* Reviews list */}
          <div className="surface divide-y divide-slate-100 dark:divide-white/5">
            <div className="grid grid-cols-5 gap-4 px-5 py-3 text-xs font-medium uppercase tracking-wide text-slate-400">
              <span className="col-span-2">{t('Employee')}</span><span>{t('Score')}</span><span>{t('Rating')}</span><span>{t('Action')}</span>
            </div>
            {reviews.map(r => (
              <div key={r.id} className="grid grid-cols-5 items-center gap-4 px-5 py-3">
                <div className="col-span-2">
                  <p className="text-sm font-medium text-slate-900 dark:text-white">{r.employeeName}</p>
                  <p className="text-xs text-slate-400">{r.departmentName}</p>
                </div>
                <span className={`text-sm font-semibold tabular-nums ${scoreColor(r.finalScore)}`}>{r.finalScore}</span>
                <span className="text-sm text-slate-600 dark:text-slate-300">{r.finalRating || '—'}</span>
                <button type="button" className={btn.ghost} onClick={() => { setAdjustModal({ reviewId: r.id, name: r.employeeName, current: r.finalScore }); setAdjustment('0'); setAdjustReason(''); setAdjustError(''); }}>{t('Adjust')}</button>
              </div>
            ))}
          </div>
        </div>
      )}

      {adjustModal && (
        <Modal title={t('Calibrate — {name}', { name: adjustModal.name })} onClose={() => setAdjustModal(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-600 dark:text-slate-400">{t('Current score: {score}', { score: adjustModal.current })}</p>
            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-700 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-400">
              {t('AI may surface outliers, but all calibration decisions are made by the calibration committee. A mandatory reason is required for audit purposes.')}
            </div>
            <Field label={t('Score Adjustment (e.g. +5 or -3)')}><input type="number" className={inp} value={adjustment} onChange={e => setAdjustment(e.target.value)} /></Field>
            <Field label={t('Reason (required) *')}><textarea className={inp} rows={3} value={adjustReason} onChange={e => setAdjustReason(e.target.value)} placeholder={t('Explain the calibration rationale…')} /></Field>
            {adjustError && <p role="alert" className="text-xs text-rose-500">{adjustError}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setAdjustModal(null)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={doAdjust}>{t('Apply Calibration')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Recommendations Tab ───────────────────────────────────────────────────────

function RecommendationsTab() {
  const { currencyCode } = useTenantSettings();
  const { t, locale } = useLocale();
  const fx = useFormat();
  const [tab, setTab] = useState<'increments' | 'promotions' | 'bonuses'>('increments');
  const [increments, setIncrements] = useState<IncrementRecommendation[]>([]);
  const [promotions, setPromotions] = useState<PromotionRecommendation[]>([]);
  const [bonuses, setBonuses] = useState<BonusRecommendation[]>([]);
  const [loading, setLoading] = useState(true);
  const [decideModal, setDecideModal] = useState<{ type: 'increment' | 'promotion' | 'bonus'; id: string; name: string } | null>(null);
  const [decision, setDecision] = useState('Approved');
  const [notes, setNotes] = useState('');

  const load = () => {
    setLoading(true);
    Promise.all([
      recommendationsApi.listIncrements('Pending').then(setIncrements),
      recommendationsApi.listPromotions('Pending').then(setPromotions),
      recommendationsApi.listBonuses('Pending').then(setBonuses),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const decide = async () => {
    if (!decideModal) return;
    try {
      if (decideModal.type === 'increment') await recommendationsApi.approveIncrement(decideModal.id, decision, notes);
      else if (decideModal.type === 'promotion') await recommendationsApi.approvePromotion(decideModal.id, decision, notes);
      else await recommendationsApi.approveBonus(decideModal.id, decision);
      setDecideModal(null); setNotes(''); load();
    } catch (e) { notifyApiError(e, t('Decision failed.')); }
  };

  return (
    <div className="space-y-4">
      <div className="flex w-fit gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
        {(['increments', 'promotions', 'bonuses'] as const).map(k => (
          <button key={k} type="button" onClick={() => setTab(k)} className={`rounded-lg px-4 py-1.5 text-sm font-medium capitalize transition ${tab === k ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}>{k === 'increments' ? t('Increments') : k === 'promotions' ? t('Promotions') : t('Bonuses')}</button>
        ))}
      </div>

      <div className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:border-blue-500/20 dark:bg-blue-500/10 dark:text-blue-400">
        {t('Approved recommendations are sent to Payroll as pending approved actions. They are not applied automatically — a Payroll manager must process them.')}
      </div>

      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {tab === 'increments' && (increments.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">{t('No pending increments')}</p> : increments.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName} · {r.designationTitle}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{t('Current: {currency} {current} → New: {currency} {next} (+{pct}%)', { currency: currencyCode, current: fx.integer(r.currentSalary), next: fx.integer(r.newSalary), pct: r.recommendedIncrementPct })}</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'increment', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>{t('Decide')}</button>
            </div>
          )))}
          {tab === 'promotions' && (promotions.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">{t('No pending promotions')}</p> : promotions.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{t('{from} → {to} · effective {date}', { from: r.currentDesignation, to: r.proposedDesignation, date: fmtDate(r.effectiveDate, locale) })}</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'promotion', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>{t('Decide')}</button>
            </div>
          )))}
          {tab === 'bonuses' && (bonuses.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">{t('No pending bonuses')}</p> : bonuses.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName} · {r.bonusType}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{t('Amount: {currency} {amount}', { currency: currencyCode, amount: fx.integer(r.bonusAmount) })}</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'bonus', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>{t('Decide')}</button>
            </div>
          )))}
        </div>
      )}

      {decideModal && (
        <Modal title={t('Decision — {name}', { name: decideModal.name })} onClose={() => setDecideModal(null)}>
          <div className="space-y-4">
            <Field label={t('Decision')}>
              <select title={t('Decision')} className={sel} value={decision} onChange={e => setDecision(e.target.value)}>
                <option value="Approved">{t('Approve')}</option>
                <option value="Rejected">{t('Reject')}</option>
              </select>
            </Field>
            <Field label={t('Notes')}><textarea title={t('Notes')} className={inp} rows={3} value={notes} onChange={e => setNotes(e.target.value)} /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setDecideModal(null)}>{t('Cancel')}</button>
              <button type="button" className={decision === 'Approved' ? btn.primary : btn.danger} onClick={decide}>{valueText(t, decision)}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── PIP & Probation Tab ───────────────────────────────────────────────────────

function CreatePIPModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { t } = useLocale();
  const [selectedEmployee, setSelectedEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({ performanceGaps: '', improvementGoals: '', supportPlan: '', startDate: '', endDate: '', hrNotes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!selectedEmployee) { setError(t('Please select an employee.')); return; }
    if (!form.performanceGaps || !form.improvementGoals || !form.startDate || !form.endDate) { setError(t('Performance gaps, goals and dates are required.')); return; }
    setSaving(true); setError('');
    try {
      await pipApi.create({ employeeId: selectedEmployee.intId, employeeName: selectedEmployee.fullName, departmentName: selectedEmployee.department, performanceGaps: form.performanceGaps, improvementGoals: form.improvementGoals, supportPlan: form.supportPlan, startDate: form.startDate, endDate: form.endDate, hrNotes: form.hrNotes });
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Failed to create PIP.'))); setSaving(false); }
  };

  return (
    <Modal title={t('Create Performance Improvement Plan')} onClose={onClose} wide>
      <div className="space-y-4">
        <Field label={t('Employee *')}>
          <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
        </Field>
        {selectedEmployee && (
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Department: {name}', { name: selectedEmployee.department })}</p>
        )}
        <Field label={t('Performance Gaps *')}><textarea className={inp} rows={3} value={form.performanceGaps} onChange={e => set('performanceGaps', e.target.value)} placeholder={t('Describe specific performance deficiencies…')} /></Field>
        <Field label={t('Improvement Goals *')}><textarea className={inp} rows={3} value={form.improvementGoals} onChange={e => set('improvementGoals', e.target.value)} placeholder={t('Measurable objectives to achieve within the PIP period…')} /></Field>
        <Field label={t('Support Plan')}><textarea className={inp} rows={2} value={form.supportPlan} onChange={e => set('supportPlan', e.target.value)} placeholder={t('Training, coaching, resources provided…')} /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Start Date *')}><input type="date" title={t('Start Date')} className={inp} value={form.startDate} onChange={e => set('startDate', e.target.value)} /></Field>
          <Field label={t('End Date *')}><input type="date" title={t('End Date')} className={inp} value={form.endDate} onChange={e => set('endDate', e.target.value)} /></Field>
        </div>
        <Field label={t('HR Notes')}><textarea title={t('HR Notes')} className={inp} rows={2} value={form.hrNotes} onChange={e => set('hrNotes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Creating…') : t('Create PIP')}</button>
        </div>
      </div>
    </Modal>
  );
}

function PIPProbationTab() {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const can = usePerformanceCapabilities();
  const [subTab, setSubTab] = useState<'pip' | 'probation'>('pip');
  const [pips, setPips] = useState<PerformanceImprovementPlan[]>([]);
  const [probations, setProbations] = useState<ProbationReview[]>([]);
  const [terminationQueue, setTerminationQueue] = useState<PipTerminationQueueItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreatePIP, setShowCreatePIP] = useState(false);
  const [statusModal, setStatusModal] = useState<{ id: string; name: string } | null>(null);
  const [newStatus, setNewStatus] = useState('Improved');
  const [statusNotes, setStatusNotes] = useState('');
  const [statusError, setStatusError] = useState('');
  const [probationModal, setProbationModal] = useState<{ p: ProbationReview; decision: string } | null>(null);
  const [probationNotes, setProbationNotes] = useState('');
  const [probationDate, setProbationDate] = useState('');
  const [probationError, setProbationError] = useState('');

  const load = () => {
    setLoading(true);
    Promise.all([
      pipApi.list().then(setPips),
      probationApi.list().then(setProbations),
      // HR's queue (performance.approve): a line manager is not asked for it.
      can.decideRatings
        ? pipApi.terminationQueue().then(r => setTerminationQueue(r.items)).catch(() => setTerminationQueue([]))
        : Promise.resolve(setTerminationQueue([])),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const updatePIPStatus = async () => {
    if (!statusModal) return;
    setStatusError('');
    try {
      const r = await pipApi.updateStatus(statusModal.id, newStatus, statusNotes);
      setStatusModal(null); load();
      if (r.nextStep) toast.info(r.nextStep);
    } catch (e) {
      // The adverse-close guard answers 409 with a machine-readable code; show what it said.
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setStatusError(msg ?? t('Status update failed.'));
    }
  };

  const submitProbationDecision = async () => {
    if (!probationModal) return;
    setProbationError('');
    const { p, decision } = probationModal;
    try {
      const r = await probationApi.hrDecision(p.id, decision, probationNotes, decision === 'Extended'
        ? { newProbationEndDate: probationDate }
        : probationDate ? { effectiveDate: probationDate } : undefined);
      setProbationModal(null); load();
      toast.success(r.separationRaised
        ? t('{name} is now {status}. A \'{type}\' separation was raised — complete the offboarding checklist and final settlement in Offboarding.', { name: p.employeeName, status: valueText(t, r.employeeStatus), type: r.separationType })
        : r.confirmationDate
          ? t('Recorded. Probation now ends {date}; confirmed from {confirmed}.', { date: r.probationEndDate ?? '—', confirmed: r.confirmationDate })
          : t('Recorded. Probation now ends {date}.', { date: r.probationEndDate ?? '—' }));
    } catch (e) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setProbationError(msg ?? t('The decision could not be recorded.'));
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex w-fit gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
        <button type="button" onClick={() => setSubTab('pip')} className={`rounded-lg px-4 py-1.5 text-sm font-medium transition ${subTab === 'pip' ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>{t('PIPs ({count})', { count: pips.length })}</button>
        <button type="button" onClick={() => setSubTab('probation')} className={`rounded-lg px-4 py-1.5 text-sm font-medium transition ${subTab === 'probation' ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>{t('Probation ({count})', { count: probations.length })}</button>
      </div>

      {subTab === 'pip' && (
        <div className="space-y-4">
          {terminationQueue.length > 0 && (
            <div className="surface border-s-4 border-s-rose-500 p-5">
              <div className="flex items-center gap-2">
                <AlertTriangle className="h-4 w-4 text-rose-500" aria-hidden="true" />
                <h3 className="text-sm font-semibold text-slate-900 dark:text-white">
                  {t('Awaiting an employment decision ({count})', { count: terminationQueue.length })}
                </h3>
              </div>
              <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                {t('A PIP was closed with a termination recommendation and no separation has been raised for that person. This is a recommendation, not a termination — HR and leadership decide. Each entry clears once a separation exists in Offboarding.')}
              </p>
              <div className="mt-3 divide-y divide-slate-100 dark:divide-white/5">
                {terminationQueue.map(q => (
                  <div key={q.id} className="py-3">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{q.employeeName}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{t('{department} · recommended {date}', { department: q.departmentName, date: fmtDate(q.closedAtUtc, locale) })}</p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{q.recommendationReason}</p>
                    <p className="mt-1 text-xs text-slate-400">{q.action}</p>
                  </div>
                ))}
              </div>
            </div>
          )}
          <div className="flex justify-end">
            {can.writeReviews && (
              <button type="button" className={btn.primary} onClick={() => setShowCreatePIP(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('New PIP')}</button>
            )}
          </div>
          {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : pips.length === 0 ? (
            <div className="surface flex flex-col items-center py-16 text-center">
              <FileText className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No active PIPs')}</p>
            </div>
          ) : (
            <div className="surface divide-y divide-slate-100 dark:divide-white/5">
              {pips.map(p => (
                <div key={p.id} className="flex items-center justify-between px-5 py-4">
                  <div className="min-w-0">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{p.employeeName}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{p.departmentName} · {fmtDate(p.startDate, locale)} – {fmtDate(p.endDate, locale)}</p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400 line-clamp-1">{p.improvementGoals}</p>
                    <p className="mt-1 text-xs text-slate-400">
                      {p.checkInCount ? t('{count, plural, one {# check-in} other {# check-ins}} · latest: {outcome}', { count: p.checkInCount, outcome: p.latestCheckInOutcome }) : t('No check-ins recorded yet')}
                    </p>
                  </div>
                  <div className="ms-4 flex shrink-0 items-center gap-3">
                    <StatusBadge status={p.status} />
                    {p.status === 'Active' && can.decideRatings && (
                      <button type="button" className={btn.ghost} onClick={() => { setStatusModal({ id: p.id, name: p.employeeName }); setNewStatus('Improved'); setStatusNotes(''); }}>{t('Update Status')}</button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {subTab === 'probation' && (
        <div className="space-y-4">
          {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : probations.length === 0 ? (
            <div className="surface flex flex-col items-center py-16 text-center">
              <Clock className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No probation reviews')}</p>
            </div>
          ) : (
            <div className="surface divide-y divide-slate-100 dark:divide-white/5">
              {probations.map(p => (
                <div key={p.id} className="flex items-center justify-between px-5 py-4">
                  <div className="min-w-0">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{p.employeeName}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{p.departmentName} · {p.designationTitle}</p>
                    <p className="text-xs text-slate-400">{t('Probation: {start} – {end}', { start: fmtDate(p.probationStartDate, locale), end: fmtDate(p.probationEndDate, locale) })}</p>
                    {p.managerRecommendation && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Manager: {text}', { text: p.managerRecommendation })}</p>}
                  </div>
                  <div className="ms-4 flex shrink-0 items-center gap-2">
                    <StatusBadge status={p.status} />
                    {p.status === 'ManagerReviewed' && can.decideRatings && (
                      <div className="flex gap-1">
                        {['Confirmed', 'Extended', 'Terminated'].map(d => (
                          <button key={d} type="button"
                            className={d === 'Terminated' ? btn.danger : d === 'Confirmed' ? btn.primary : btn.ghost}
                            onClick={() => {
                              setProbationModal({ p, decision: d });
                              setProbationNotes('');
                              setProbationDate(d === 'Terminated' ? p.probationEndDate.slice(0, 10) : '');
                              setProbationError('');
                            }}>
                            {d === 'Confirmed' ? t('Confirm') : d === 'Extended' ? t('Extend') : t('Terminate')}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {showCreatePIP && <CreatePIPModal onClose={() => setShowCreatePIP(false)} onSaved={() => { setShowCreatePIP(false); load(); }} />}
      {probationModal && (
        <Modal title={probationModal.decision === 'Confirmed' ? t('Confirm probation — {name}', { name: probationModal.p.employeeName }) : probationModal.decision === 'Extended' ? t('Extend probation — {name}', { name: probationModal.p.employeeName }) : t('Terminate probation — {name}', { name: probationModal.p.employeeName })}
          onClose={() => setProbationModal(null)}>
          <div className="space-y-4">
            <div className={`rounded-lg border p-3 text-xs ${probationModal.decision === 'Terminated'
              ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-400'
              : 'border-slate-200 bg-slate-50 text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300'}`}>
              {probationModal.decision === 'Terminated'
                ? t('This ENDS the employment. A separation of type "Probation not passed" is raised, the WPS payroll footprint is deactivated and the offboarding record is created for final settlement. It is not reversible from this screen. A dismissal for cause (Article 80, which forfeits the end-of-service award) must be raised through Offboarding instead.')
                : probationModal.decision === 'Confirmed'
                  ? t('This ends the probation period from the effective date. Leave policies that exclude probationers start applying from that date, and the employee leaves the probation headcount.')
                  : t('An extension must state the new probation end date. The leave rules and the probation headcount follow that date.')}
            </div>
            <Field label={probationModal.decision === 'Extended' ? t('New probation end date *')
              : probationModal.decision === 'Terminated' ? t('Last working day *') : t('Effective date')}>
              <input type="date"
                title={probationModal.decision === 'Extended' ? t('New probation end date') : t('Effective date')}
                className={inp} value={probationDate} onChange={e => setProbationDate(e.target.value)} />
            </Field>
            <Field label={t('HR notes')}>
              <textarea title={t('HR notes')} className={inp} rows={3} value={probationNotes}
                onChange={e => setProbationNotes(e.target.value)} />
            </Field>
            {probationError && <p className="text-xs text-rose-500">{probationError}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setProbationModal(null)}>{t('Cancel')}</button>
              <button type="button"
                className={probationModal.decision === 'Terminated' ? btn.danger : btn.primary}
                onClick={submitProbationDecision}>
                {t('Record decision')}
              </button>
            </div>
          </div>
        </Modal>
      )}
      {statusModal && (
        <Modal title={t('Update PIP Status — {name}', { name: statusModal.name })} onClose={() => setStatusModal(null)}>
          <div className="space-y-4">
            <Field label={t('New Status')}>
              <select title={t('New Status')} className={sel} value={newStatus} onChange={e => setNewStatus(e.target.value)}>
                {['Improved', 'Extended', 'Failed', 'TerminationRecommended'].map(s => <option key={s} value={s}>{valueText(t, s)}</option>)}
              </select>
            </Field>
            {newStatus === 'TerminationRecommended' && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-400">
                {t('This is a recommendation only — not an automatic termination. HR and leadership must make the final employment decision.')}
              </div>
            )}
            <Field label={t('Notes')}><textarea title={t('Status Notes')} className={inp} rows={3} value={statusNotes} onChange={e => setStatusNotes(e.target.value)} /></Field>
            {statusError && <p className="text-xs text-rose-500">{statusError}</p>}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setStatusModal(null)}>{t('Cancel')}</button>
              <button type="button" className={newStatus === 'TerminationRecommended' || newStatus === 'Failed' ? btn.danger : btn.primary} onClick={updatePIPStatus}>{t('Update')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Analytics Tab ─────────────────────────────────────────────────────────────

function AnalyticsTab() {
  const { t } = useLocale();
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [selectedCycle, setSelectedCycle] = useState('');
  const [analytics, setAnalytics] = useState<CycleAnalytics | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { cyclesApi.listAll().then(all => { setCycles(all); if (all.length > 0) setSelectedCycle(all[0].id); }).catch(() => {}); }, []);
  useEffect(() => {
    if (!selectedCycle) return;
    setLoading(true);
    analyticsApi.cycleAnalytics(selectedCycle).then(setAnalytics).catch(() => {}).finally(() => setLoading(false));
  }, [selectedCycle]);

  return (
    <div className="space-y-4">
      <select title={t('Select Cycle')} aria-label={t('Select Cycle')} className={`${sel} w-72`} value={selectedCycle} onChange={e => setSelectedCycle(e.target.value)}>
        <option value="">{t('Select cycle')}</option>
        {cycles.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
      </select>

      {loading && <p className="text-sm text-slate-400">{t('Loading analytics…')}</p>}

      {analytics && !loading && (
        <div className="space-y-4">
          {/* Summary */}
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <KpiCard label={t('Total Enrolled')} value={analytics.summary.totalEnrolled} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
            <KpiCard label={t('Completion Rate')} value={`${analytics.summary.completionRate}%`} icon={CheckCircle} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
            <KpiCard label={t('Avg Score')} value={analytics.summary.overallAvgScore} icon={BarChart2} color="bg-blue-100 text-blue-600 dark:bg-blue-500/20 dark:text-blue-400" />
            <KpiCard label={t('High Performers')} value={analytics.summary.highPerformers} sub={t('Score ≥ 85')} icon={Star} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            {/* Rating Distribution */}
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Rating Distribution')}</h3>
              {analytics.distribution.length === 0 ? <p className="text-sm text-slate-400">{t('No completed reviews yet.')}</p> : (
                <div className="space-y-3">
                  {analytics.distribution.map(d => (
                    <div key={d.rating} className="flex items-center gap-3">
                      <span className="w-28 shrink-0 text-xs text-slate-600 dark:text-slate-400">{d.rating || t('Unrated')}</span>
                      <div className="flex-1 rounded-full bg-slate-100 dark:bg-white/10">
                        <div className="h-2 rounded-full bg-sapphire" style={{ width: `${d.pct}%` }} />{/* dynamic — inline required */}
                      </div>
                      <span className="w-12 text-end text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{d.count} ({d.pct}%)</span>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Dept Averages */}
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Department Averages')}</h3>
              {analytics.deptAvg.length === 0 ? <p className="text-sm text-slate-400">{t('No data yet.')}</p> : (
                <div className="space-y-3">
                  {analytics.deptAvg.slice(0, 8).map(d => (
                    <div key={d.department} className="flex items-center gap-3">
                      <span className="min-w-0 flex-1 truncate text-xs text-slate-600 dark:text-slate-400">{d.department}</span>
                      <div className="w-32 rounded-full bg-slate-100 dark:bg-white/10">
                        <div className={`h-2 rounded-full ${d.avgScore >= 75 ? 'bg-emerald-500' : d.avgScore >= 60 ? 'bg-sapphire' : 'bg-rose-400'}`} style={{ width: `${d.avgScore}%` }} />
                      </div>
                      <span className="w-8 text-end text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{d.avgScore}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Top performers */}
          {analytics.topPerformers.length > 0 && (
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Top Performers (Score ≥ 85)')}</h3>
              <div className="divide-y divide-slate-100 dark:divide-white/5">
                {analytics.topPerformers.map((p, i) => (
                  <div key={p.employeeId} className="flex items-center justify-between py-2">
                    <div className="flex items-center gap-3">
                      <span className="w-6 text-center text-xs font-semibold text-slate-400">#{i + 1}</span>
                      <div>
                        <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</p>
                        <p className="text-xs text-slate-400">{p.departmentName}</p>
                      </div>
                    </div>
                    <div className="text-end">
                      <p className="text-sm font-semibold text-emerald-600 dark:text-emerald-400">{p.finalScore}</p>
                      <p className="text-xs text-slate-400">{p.finalRating}</p>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Manager bias */}
          {analytics.managerBias.length > 0 && (
            <div className="surface p-5">
              <div className="mb-3 flex items-center gap-2">
                <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Manager Rating Analysis')}</h3>
                <span className="rounded bg-slate-100 px-2 py-0.5 text-caption font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400">{t('Human review required')}</span>
              </div>
              <div className="divide-y divide-slate-100 dark:divide-white/5">
                {analytics.managerBias.map(m => (
                  <div key={m.managerName} className="flex items-center justify-between py-3">
                    <div>
                      <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{m.managerName}</p>
                      <p className="text-xs text-slate-400">{t('{count, plural, one {# direct report rated} other {# direct reports rated}}', { count: m.count })}</p>
                    </div>
                    <div className="text-end">
                      <p className={`text-sm font-semibold tabular-nums ${scoreColor(m.avgScore)}`}>{t('{avg} avg', { avg: m.avgScore })}</p>
                      {m.possibleLeniency && <span className="text-caption text-amber-500">{t('Possible leniency')}</span>}
                      {m.possibleSeverity && <span className="text-caption text-rose-500">{t('Possible severity')}</span>}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── Feedback Tab ──────────────────────────────────────────────────────────────

function FeedbackTab() {
  const { t, locale } = useLocale();
  const [feedbacks, setFeedbacks] = useState<Awaited<ReturnType<typeof feedbackApi.listContinuous>>>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [selectedEmployee, setSelectedEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({ feedbackType: 'Commendation', content: '', isPrivate: false });
  const [saving, setSaving] = useState(false);
  const set = (k: keyof typeof form, v: string | boolean) => setForm(f => ({ ...f, [k]: v }));

  const load = () => { setLoading(true); feedbackApi.listContinuous().then(setFeedbacks).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  const save = async () => {
    if (!selectedEmployee || !form.content) return;
    setSaving(true);
    try {
      await feedbackApi.createContinuous({ employeeId: selectedEmployee.intId, employeeName: selectedEmployee.fullName, feedbackType: form.feedbackType, content: form.content, isPrivate: form.isPrivate });
      setShowCreate(false);
      setSelectedEmployee(null);
      load();
    }
    catch (e) { notifyApiError(e, t('Failed to submit feedback.')); }
    setSaving(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('Give Feedback')}</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : feedbacks.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Star className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No continuous feedback entries yet')}</p>
        </div>
      ) : (
        <div className="space-y-3">
          {feedbacks.map(f => (
            <div key={f.id} className="surface p-4">
              <div className="mb-2 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">{f.employeeName}</span>
                  <span className="rounded bg-sapphire/10 px-2 py-0.5 text-caption font-medium text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent">{valueText(t, f.feedbackType)}</span>
                  {f.isPrivate && <span className="rounded bg-slate-100 px-2 py-0.5 text-caption font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400">{t('Private')}</span>}
                </div>
                <span className="text-xs text-slate-400">{t('{date} · by {name}', { date: fmtDate(f.createdAtUtc, locale), name: f.givenByName })}</span>
              </div>
              <p className="text-sm text-slate-700 dark:text-slate-300">{f.content}</p>
            </div>
          ))}
        </div>
      )}

      {showCreate && (
        <Modal title={t('Give Continuous Feedback')} onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            <Field label={t('Employee *')}>
              <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
            </Field>
            <Field label={t('Feedback Type')}>
              <select title={t('Feedback Type')} className={sel} value={form.feedbackType} onChange={e => set('feedbackType', e.target.value)}>
                {['Commendation', 'Concern', 'Coaching', 'Developmental', 'Recognition'].map(k => <option key={k} value={k}>{valueText(t, k)}</option>)}
              </select>
            </Field>
            <Field label={t('Feedback *')}><textarea className={inp} rows={4} value={form.content} onChange={e => set('content', e.target.value)} placeholder={t('Share specific, constructive observations…')} /></Field>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.isPrivate} onChange={e => set('isPrivate', e.target.checked)} className="rounded" />
              {t('Private (only visible to you and HR)')}
            </label>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Submitting…') : t('Submit Feedback')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Root Page ─────────────────────────────────────────────────────────────────

type Tab = PerformanceTab;

const TABS: { id: Tab; label: string; icon: React.ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' | 'false' }> }[] = [
  { id: 'overview', label: msg('Overview'), icon: Activity },
  { id: 'cycles', label: msg('Cycles'), icon: ClipboardList },
  { id: 'my-reviews', label: msg('My Reviews'), icon: Star },
  { id: 'team-reviews', label: msg('Team Reviews'), icon: Users },
  { id: 'goals', label: msg('Goals & KPIs'), icon: Target },
  { id: 'calibration', label: msg('Calibration'), icon: BarChart2 },
  { id: 'recommendations', label: msg('Recommendations'), icon: TrendingUp },
  { id: 'pip', label: msg('PIP & Probation'), icon: AlertTriangle },
  { id: 'analytics', label: msg('Analytics'), icon: Zap },
  { id: 'feedback', label: msg('Feedback'), icon: FileText },
];

export function PerformancePage({ initialTab }: { initialTab?: Tab } = {}) {
  const { t: tr } = useLocale();
  const { hasPermission } = useAuth();
  // Each tab shows only for the keys its API calls need (lib/performanceAccess): an employee lands on their own
  // review and a line manager on their team, and neither is shown HR's calibration or tenant-wide analytics.
  const tabs = TABS.filter(t => canOpenPerformanceTab(t.id, hasPermission));
  const [chosen, setChosen] = useState<Tab | null>(null);
  const tab = chosen && canOpenPerformanceTab(chosen, hasPermission)
    ? chosen
    : landingPerformanceTab(hasPermission, initialTab);
  const setTab = (next: Tab) => setChosen(next);

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{tr('Performance & Appraisals')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            {tr('Balanced scorecard appraisals, goal management, calibration, recommendations, PIPs, and analytics.')}
          </p>
        </div>
        <ModuleSettingsLink sections={['performanceScorecards']} label={msg('Performance settings')} />
      </div>

      {/* Tab bar — scrollable on mobile */}
      <div className="overflow-x-auto">
        <div className="flex w-max gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
          {tabs.map(t => (
            <button
              key={t.id}
              type="button"
              onClick={() => setTab(t.id)}
              className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${tab === t.id ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}
            >
              <t.icon className="h-3.5 w-3.5" aria-hidden="true" />
              {tr(t.label)}
            </button>
          ))}
        </div>
      </div>

      {tab === 'overview' && <OverviewTab onNavigate={setTab} />}
      {tab === 'cycles' && <CyclesTab />}
      {tab === 'my-reviews' && <MyReviewsTab />}
      {tab === 'team-reviews' && <TeamReviewsTab />}
      {tab === 'goals' && <GoalsTab />}
      {tab === 'calibration' && <CalibrationTab />}
      {tab === 'recommendations' && <RecommendationsTab />}
      {tab === 'pip' && <PIPProbationTab />}
      {tab === 'analytics' && <AnalyticsTab />}
      {tab === 'feedback' && <FeedbackTab />}
    </div>
  );
}
