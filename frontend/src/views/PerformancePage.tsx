'use client';

import { InfoTip } from '../components/InfoTip';
import { EmployeeSearchSelect } from '../components/EmployeeSearchSelect';
import type { EmployeeSelection } from '../components/EmployeeSearchSelect';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useEffect, useState } from 'react';
import {
  Activity, AlertTriangle, BarChart2, CheckCircle, ChevronRight,
  ClipboardList, Clock, FileText, Plus, Settings, Star,
  Target, TrendingUp, Users, X, Zap,
} from 'lucide-react';
import {
  analyticsApi, calibrationApi, cyclesApi, feedbackApi, goalsApi,
  pipApi, probationApi, recommendationsApi, reviewsApi, templatesApi,
} from '../api/performance';
import type {
  AppraisalReview, BonusRecommendation, CycleAnalytics, EmployeeGoal,
  IncrementRecommendation, PerformanceCycle, PerformanceImprovementPlan,
  ProbationReview, PromotionRecommendation, ScorecardTemplate,
} from '../api/performance';

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined) {
  if (!s) return '—';
  return new Date(s).toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' });
}

function fmtCurrency(n: number) {
  return n.toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
}

function scoreColor(score: number) {
  if (score >= 85) return 'text-emerald-600 dark:text-emerald-400';
  if (score >= 60) return 'text-sapphire dark:text-cyanAccent';
  if (score > 0) return 'text-rose-600 dark:text-rose-400';
  return 'text-slate-400';
}

function statusBadge(status: string) {
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
  const labels: Record<string, string> = {
    SelfAssessmentDue: 'Self-Assessment Due',
    SelfAssessmentSubmitted: 'Self-Assessment Submitted',
    ManagerReview: 'Manager Review',
    FinalApproval: 'Final Approval',
    InReview: 'In Review',
    OnHold: 'On Hold',
  };
  const cls = map[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${cls}`}>{labels[status] ?? status}</span>;
}

// ── KPI Card ──────────────────────────────────────────────────────────────────

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

// ── Modal shell ───────────────────────────────────────────────────────────────

function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className={`relative w-full rounded-2xl bg-white shadow-2xl dark:bg-slate-800 ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
        <div className="sticky top-0 flex items-center justify-between border-b border-slate-100 bg-white px-6 py-4 dark:border-white/10 dark:bg-slate-800">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h2>
          <button type="button" onClick={onClose} className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4 text-slate-500" /></button>
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
};

// ── Overview Tab ──────────────────────────────────────────────────────────────

function OverviewTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
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
        <KpiCard label="Active Cycles" value={dash?.activeCycles?.length ?? '—'} icon={Activity} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label="Self-Assessment Due" value={pa?.selfAssessmentDue ?? '—'} icon={ClipboardList} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label="Manager Reviews Pending" value={pa?.managerReviewPending ?? '—'} icon={Users} color="bg-blue-100 text-blue-600 dark:bg-blue-500/20 dark:text-blue-400" />
        <KpiCard label="Active PIPs" value={dash?.activePips ?? '—'} icon={AlertTriangle} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        {/* Pending actions */}
        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Pending Actions</h3>
          <div className="space-y-3">
            {[
              { label: 'Self-Assessment Due', val: pa?.selfAssessmentDue ?? 0, color: 'bg-amber-400', tab: 'my-reviews' as Tab },
              { label: 'Manager Reviews', val: pa?.managerReviewPending ?? 0, color: 'bg-sapphire', tab: 'team-reviews' as Tab },
              { label: 'Calibration Pending', val: pa?.calibrationPending ?? 0, color: 'bg-purple-500', tab: 'calibration' as Tab },
              { label: 'Appeals Pending', val: pa?.appealsPending ?? 0, color: 'bg-rose-500', tab: 'team-reviews' as Tab },
            ].map(row => (
              <button key={row.label} type="button" onClick={() => onNavigate(row.tab)} className="flex w-full items-center gap-3 rounded-lg p-1 text-left hover:bg-slate-50 dark:hover:bg-white/5">
                <div className={`h-2 w-2 shrink-0 rounded-full ${row.color}`} />
                <span className="flex-1 text-sm text-slate-700 dark:text-slate-300">{row.label}</span>
                <span className="text-sm font-semibold tabular-nums text-slate-900 dark:text-white">{row.val}</span>
                <ChevronRight className="h-3.5 w-3.5 text-slate-400" />
              </button>
            ))}
          </div>
        </div>

        {/* Recommendations */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Pending Recommendations</h3>
            <button type="button" onClick={() => onNavigate('recommendations')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
          </div>
          <div className="space-y-3">
            {[
              { label: 'Salary Increments', val: rec?.incrementPending ?? 0, icon: TrendingUp },
              { label: 'Promotions', val: rec?.promotionPending ?? 0, icon: Star },
              { label: 'Bonuses', val: rec?.bonusPending ?? 0, icon: Zap },
            ].map(row => (
              <div key={row.label} className="flex items-center gap-3">
                <row.icon className="h-4 w-4 shrink-0 text-slate-400" />
                <span className="flex-1 text-sm text-slate-700 dark:text-slate-300">{row.label}</span>
                <span className={`text-sm font-semibold tabular-nums ${row.val > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-400'}`}>{row.val}</span>
              </div>
            ))}
          </div>
          <p className="mt-4 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
            Approved recommendations are sent to Payroll as pending actions — not applied automatically.
          </p>
        </div>

        {/* Goals summary */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Goal Progress</h3>
            <button type="button" onClick={() => onNavigate('goals')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">Manage</button>
          </div>
          {goals ? (
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <span className="text-sm text-slate-600 dark:text-slate-400">Avg Achievement</span>
                <span className="text-lg font-bold text-sapphire dark:text-cyanAccent">{goals.avgAchievement}%</span>
              </div>
              <div className="h-2 rounded-full bg-slate-100 dark:bg-white/10">
                <div className="h-2 rounded-full bg-sapphire" style={{ width: `${Math.min(goals.avgAchievement, 100)}%` }} />
              </div>
              <div className="grid grid-cols-3 gap-2 pt-1">
                {[{ label: 'Completed', val: goals.completed, c: 'text-emerald-600 dark:text-emerald-400' },
                  { label: 'On Track', val: goals.onTrack, c: 'text-sapphire dark:text-cyanAccent' },
                  { label: 'At Risk', val: goals.atRisk, c: 'text-rose-600 dark:text-rose-400' }].map(g => (
                  <div key={g.label} className="text-center">
                    <p className={`text-xl font-bold tabular-nums ${g.c}`}>{g.val}</p>
                    <p className="text-[10px] text-slate-400">{g.label}</p>
                  </div>
                ))}
              </div>
            </div>
          ) : <p className="text-sm text-slate-400">Loading…</p>}
        </div>
      </div>

      {/* Active cycles */}
      {dash?.activeCycles && dash.activeCycles.length > 0 && (
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Active Cycles</h3>
            <button type="button" onClick={() => onNavigate('cycles')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">Manage cycles</button>
          </div>
          <div className="divide-y divide-slate-100 dark:divide-white/5">
            {(dash.activeCycles as PerformanceCycle[]).map((c) => (
              <div key={c.id} className="flex items-center justify-between py-3">
                <div>
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{c.name}</p>
                  <p className="text-xs text-slate-400">{c.cycleType} · ends {fmtDate(c.reviewPeriodEnd)}</p>
                </div>
                {statusBadge(c.status)}
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
      setError('Name and review period dates are required.'); return;
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
    } catch { setError('Failed to create cycle.'); setSaving(false); }
  };

  return (
    <Modal title="New Performance Cycle" onClose={onClose}>
      <div className="space-y-4">
        <Field label="Cycle Name *"><input className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder="e.g. Annual Appraisal 2026" /></Field>
        <Field label="Cycle Type">
          <select className={sel} value={form.cycleType} onChange={e => set('cycleType', e.target.value)}>
            {['Annual', 'Semi-Annual', 'Quarterly', 'Monthly', 'Probation', 'Project'].map(t => <option key={t}>{t}</option>)}
          </select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Period Start *"><input type="date" className={inp} value={form.reviewPeriodStart} onChange={e => set('reviewPeriodStart', e.target.value)} /></Field>
          <Field label="Period End *"><input type="date" className={inp} value={form.reviewPeriodEnd} onChange={e => set('reviewPeriodEnd', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Self-Assessment Deadline"><input type="date" className={inp} value={form.selfAssessmentDeadline} onChange={e => set('selfAssessmentDeadline', e.target.value)} /></Field>
          <Field label="Manager Review Deadline"><input type="date" className={inp} value={form.managerReviewDeadline} onChange={e => set('managerReviewDeadline', e.target.value)} /></Field>
          <Field label="Calibration Deadline"><input type="date" className={inp} value={form.calibrationDeadline} onChange={e => set('calibrationDeadline', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-2 gap-2">
          {([['enableSelfAssessment', 'Self-Assessment'], ['enableCalibration', 'Calibration'], ['enable360Feedback', '360° Feedback'], ['enableForcedDistribution', 'Forced Distribution']] as [keyof typeof form, string][]).map(([k, l]) => (
            <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />
              {l}
            </label>
          ))}
        </div>
        <Field label="Notes"><textarea className={inp} rows={2} value={form.notes} onChange={e => set('notes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Creating…' : 'Create Cycle'}</button>
        </div>
      </div>
    </Modal>
  );
}

// ── Cycles Tab ────────────────────────────────────────────────────────────────

function CyclesTab() {
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [launching, setLaunching] = useState<string | null>(null);

  const load = () => { setLoading(true); cyclesApi.list().then(r => { setCycles(r.items); setLoading(false); }).catch(() => setLoading(false)); };
  useEffect(load, []);

  const launch = async (id: string) => {
    setLaunching(id);
    try { await cyclesApi.launch(id); load(); } catch { alert('Launch failed. Ensure a default scorecard template is assigned.'); }
    setLaunching(null);
  };

  const advance = async (id: string) => {
    try { await cyclesApi.advance(id); load(); } catch { alert('Could not advance cycle.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">{cycles.length} cycle{cycles.length !== 1 ? 's' : ''}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Cycle</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : cycles.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Activity className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No performance cycles yet</p>
          <p className="text-xs text-slate-400 dark:text-slate-500">Create the first cycle to begin the appraisal process.</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {cycles.map(c => (
            <div key={c.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{c.name}</p>
                <p className="mt-0.5 text-xs text-slate-400">{c.cycleType} · {fmtDate(c.reviewPeriodStart)} – {fmtDate(c.reviewPeriodEnd)} · {c.enrolledCount ?? 0} enrolled</p>
              </div>
              <div className="ml-4 flex shrink-0 items-center gap-3">
                {statusBadge(c.status)}
                {c.status === 'Draft' && (
                  <button type="button" onClick={() => launch(c.id)} disabled={launching === c.id} className={btn.primary}>
                    {launching === c.id ? 'Launching…' : 'Launch'}
                  </button>
                )}
                {['Active', 'InReview', 'Calibration', 'FinalApproval'].includes(c.status) && (
                  <button type="button" onClick={() => advance(c.id)} className={btn.ghost}>Advance</button>
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
  const [form, setForm] = useState({ kpiScore: '', competencyScore: '', productivityScore: '', notes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    const kpi = Number(form.kpiScore); const comp = Number(form.competencyScore); const prod = Number(form.productivityScore);
    if (!kpi || !comp || !prod) { setError('Enter scores for KPI, Competency, and Productivity (0–100).'); return; }
    setSaving(true); setError('');
    try {
      await reviewsApi.submitSelfAssessment(review.id, { notes: form.notes, kpiScore: kpi, competencyScore: comp, productivityScore: prod });
      onSaved();
    } catch { setError('Submission failed.'); setSaving(false); }
  };

  return (
    <Modal title={`Self-Assessment — ${review.employeeName}`} onClose={onClose}>
      <div className="space-y-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">Cycle: <strong className="text-slate-700 dark:text-slate-200">{review.cycleName}</strong></p>
        <div className="grid grid-cols-3 gap-3">
          <Field label="KPI Score (0–100)"><input type="number" min={0} max={100} className={inp} value={form.kpiScore} onChange={e => set('kpiScore', e.target.value)} /></Field>
          <Field label="Competency Score"><input type="number" min={0} max={100} className={inp} value={form.competencyScore} onChange={e => set('competencyScore', e.target.value)} /></Field>
          <Field label="Productivity Score"><input type="number" min={0} max={100} className={inp} value={form.productivityScore} onChange={e => set('productivityScore', e.target.value)} /></Field>
        </div>
        <Field label="Self-Assessment Notes"><textarea className={inp} rows={4} placeholder="Describe your achievements, contributions, and areas for growth…" value={form.notes} onChange={e => set('notes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Submitting…' : 'Submit Assessment'}</button>
        </div>
      </div>
    </Modal>
  );
}

function MyReviewsTab() {
  const [reviews, setReviews] = useState<AppraisalReview[]>([]);
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState<AppraisalReview | null>(null);

  const load = () => { setLoading(true); reviewsApi.list().then(r => { setReviews(r.items); setLoading(false); }).catch(() => setLoading(false)); };
  useEffect(load, []);

  const acknowledge = async (id: string) => {
    try { await reviewsApi.acknowledge(id); load(); } catch { alert('Failed to acknowledge.'); }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500 dark:text-slate-400">{reviews.length} review record{reviews.length !== 1 ? 's' : ''}</p>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : reviews.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <ClipboardList className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No reviews assigned</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {reviews.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.cycleName}</p>
                <p className="mt-0.5 text-xs text-slate-400">{r.departmentName} · {r.designationTitle}</p>
                {r.finalScore > 0 && (
                  <p className={`mt-1 text-xs font-semibold ${scoreColor(r.finalScore)}`}>Score: {r.finalScore} — {r.finalRating}</p>
                )}
              </div>
              <div className="ml-4 flex shrink-0 items-center gap-3">
                {statusBadge(r.status)}
                {r.status === 'SelfAssessmentDue' && (
                  <button type="button" className={btn.primary} onClick={() => setSelected(r)}>Start Assessment</button>
                )}
                {r.status === 'Published' && (
                  <button type="button" className={btn.ghost} onClick={() => acknowledge(r.id)}>Acknowledge</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      {selected && <SelfAssessmentModal review={selected} onClose={() => setSelected(null)} onSaved={() => { setSelected(null); load(); }} />}
    </div>
  );
}

// ── Manager Review Modal ──────────────────────────────────────────────────────

function ManagerReviewModal({ review, onClose, onSaved }: { review: AppraisalReview; onClose: () => void; onSaved: () => void }) {
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
    } catch { setError('Review submission failed.'); setSaving(false); }
  };

  const autoAttendance = async () => {
    try { const r = await reviewsApi.computeAttendance(review.id); set('attendanceScore', String(r.attendanceScore)); }
    catch { alert('Could not compute attendance — check attendance records.'); }
  };

  return (
    <Modal title={`Manager Review — ${review.employeeName}`} onClose={onClose} wide>
      <div className="space-y-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">Cycle: <strong className="text-slate-700 dark:text-slate-200">{review.cycleName}</strong> · Self-assessment: {review.selfAssessmentNotes || '(none)'}</p>
        <div className="grid grid-cols-3 gap-3">
          {[['kpiScore', 'KPI Score'], ['competencyScore', 'Competency Score'], ['productivityScore', 'Productivity Score'],
            ['feedbackScore', 'Feedback Score'], ['disciplineScore', 'Discipline Score']] .map(([k, l]) => (
            <Field key={k} label={`${l} (0–100)`}><input type="number" min={0} max={100} className={inp} value={form[k as keyof typeof form]} onChange={e => set(k as keyof typeof form, e.target.value)} /></Field>
          ))}
          <Field label="Attendance Score (0–100)">
            <div className="flex gap-2">
              <input type="number" min={0} max={100} className={inp} value={form.attendanceScore} onChange={e => set('attendanceScore', e.target.value)} />
              <button type="button" onClick={autoAttendance} className={btn.ghost} title="Auto-compute from attendance records"><Zap className="h-4 w-4" /></button>
            </div>
          </Field>
        </div>
        <Field label="Manager Notes"><textarea className={inp} rows={3} placeholder="Performance observations, achievements, areas for development…" value={form.managerNotes} onChange={e => set('managerNotes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Submitting…' : 'Submit Review'}</button>
        </div>
      </div>
    </Modal>
  );
}

// ── Team Reviews Tab ──────────────────────────────────────────────────────────

function TeamReviewsTab() {
  const [reviews, setReviews] = useState<AppraisalReview[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [selected, setSelected] = useState<AppraisalReview | null>(null);

  const load = () => {
    setLoading(true);
    reviewsApi.list({ status: statusFilter || undefined }).then(r => { setReviews(r.items); setLoading(false); }).catch(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  const publish = async (id: string) => {
    try { await reviewsApi.publish(id); load(); } catch { alert('Publish failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select className={`${sel} w-56`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All Statuses</option>
          {['SelfAssessmentDue', 'SelfAssessmentSubmitted', 'ManagerReview', 'Calibration', 'FinalApproval', 'Published', 'Acknowledged'].map(s => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>
        <p className="text-sm text-slate-500 dark:text-slate-400">{reviews.length} review{reviews.length !== 1 ? 's' : ''}</p>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : reviews.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Users className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No reviews match the filter</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {reviews.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="mt-0.5 text-xs text-slate-400">{r.departmentName} · {r.designationTitle} · {r.cycleName}</p>
                {r.finalScore > 0 && <p className={`mt-1 text-xs font-semibold ${scoreColor(r.finalScore)}`}>Score: {r.finalScore} — {r.finalRating}</p>}
              </div>
              <div className="ml-4 flex shrink-0 items-center gap-3">
                {statusBadge(r.status)}
                {['SelfAssessmentSubmitted', 'ManagerReview'].includes(r.status) && (
                  <button type="button" className={btn.primary} onClick={() => setSelected(r)}>Review</button>
                )}
                {r.status === 'FinalApproval' && (
                  <button type="button" className={btn.primary} onClick={() => publish(r.id)}>Publish</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      {selected && <ManagerReviewModal review={selected} onClose={() => setSelected(null)} onSaved={() => { setSelected(null); load(); }} />}
    </div>
  );
}

// ── Goals Tab ─────────────────────────────────────────────────────────────────

function CreateGoalModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
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
    if (!selectedEmployee) { setError('Please select an employee.'); return; }
    if (!form.title || !form.targetValue) { setError('Title and Target Value are required.'); return; }
    setSaving(true); setError('');
    try {
      await goalsApi.create({
        employeeId: selectedEmployee.intId, employeeName: selectedEmployee.fullName,
        title: form.title, description: form.description, category: form.category,
        kpiType: form.kpiType, measurementUnit: form.measurementUnit,
        baselineValue: Number(form.baselineValue),
        targetValue: Number(form.targetValue), actualValue: Number(form.actualValue),
        weight: Number(form.weight), priority: form.priority,
        startDate: form.startDate || undefined, dueDate: form.dueDate || undefined,
      });
      onSaved();
    } catch { setError('Failed to create goal.'); setSaving(false); }
  };

  return (
    <Modal title="Add Goal / KPI" onClose={onClose} wide>
      <div className="space-y-4">
        <Field label="Employee *">
          <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
        </Field>
        <Field label="Goal Title *"><input className={inp} value={form.title} onChange={e => set('title', e.target.value)} placeholder="e.g. Achieve 95% customer satisfaction score" /></Field>
        <Field label="Description"><textarea className={inp} rows={2} value={form.description} onChange={e => set('description', e.target.value)} /></Field>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Category">
            <select className={sel} value={form.category} onChange={e => set('category', e.target.value)}>
              {['KPI', 'Learning', 'Behavioral', 'Project', 'Strategic'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
          <Field label="Priority">
            <select className={sel} value={form.priority} onChange={e => set('priority', e.target.value)}>
              {['High', 'Medium', 'Low'].map(p => <option key={p}>{p}</option>)}
            </select>
          </Field>
          <Field label="Measurement">
            <select className={sel} value={form.kpiType} onChange={e => set('kpiType', e.target.value)}>
              {['Numeric', 'Percentage', 'Boolean', 'Currency', 'Rating'].map(t => <option key={t}>{t}</option>)}
            </select>
          </Field>
        </div>
        <div className="grid grid-cols-4 gap-3">
          <Field label="Baseline"><input type="number" className={inp} value={form.baselineValue} onChange={e => set('baselineValue', e.target.value)} /></Field>
          <Field label="Target *"><input type="number" className={inp} value={form.targetValue} onChange={e => set('targetValue', e.target.value)} /></Field>
          <Field label="Actual"><input type="number" className={inp} value={form.actualValue} onChange={e => set('actualValue', e.target.value)} /></Field>
          <Field label="Unit"><input className={inp} value={form.measurementUnit} onChange={e => set('measurementUnit', e.target.value)} placeholder="e.g. %" /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Weight (%)"><input type="number" min={1} max={100} className={inp} value={form.weight} onChange={e => set('weight', e.target.value)} /></Field>
          <Field label="Start Date"><input type="date" title="Start Date" className={inp} value={form.startDate} onChange={e => set('startDate', e.target.value)} /></Field>
          <Field label="Due Date"><input type="date" title="Due Date" className={inp} value={form.dueDate} onChange={e => set('dueDate', e.target.value)} /></Field>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Add Goal'}</button>
        </div>
      </div>
    </Modal>
  );
}

const PRIORITY_BADGE: Record<string, string> = {
  High: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Medium: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Low: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
};

function GoalsTab() {
  const [goals, setGoals] = useState<EmployeeGoal[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [statusFilter, setStatusFilter] = useState('');
  const [progressGoal, setProgressGoal] = useState<EmployeeGoal | null>(null);
  const [progressVal, setProgressVal] = useState('');
  const [progressNotes, setProgressNotes] = useState('');
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);

  const load = () => {
    setLoading(true);
    goalsApi.list({ status: statusFilter || undefined }).then(r => { setGoals(r.items); setLoading(false); }).catch(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  const updateProgress = async () => {
    if (!progressGoal || !progressVal) return;
    try { await goalsApi.updateProgress(progressGoal.id, Number(progressVal), progressNotes); setProgressGoal(null); load(); }
    catch { alert('Update failed.'); }
  };

  const confirmDelete = async () => {
    if (!deleteId) return;
    setDeleting(true);
    try { await goalsApi.delete(deleteId); setDeleteId(null); load(); }
    catch { alert('Delete failed. Goal may be approved.'); }
    setDeleting(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All Statuses</option>
          {['Draft', 'Active', 'Completed', 'OnHold', 'Cancelled'].map(s => <option key={s}>{s}</option>)}
        </select>
        <p className="flex-1 text-sm text-slate-500 dark:text-slate-400">{goals.length} goal{goals.length !== 1 ? 's' : ''}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> Add Goal</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : goals.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Target className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No goals found</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {goals.map(g => (
            <div key={g.id} className="px-5 py-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{g.title}</p>
                  <p className="mt-0.5 text-xs text-slate-400">{g.employeeName} · {g.category} · {g.kpiType} · Weight: {g.weight}%{g.dueDate ? ` · Due ${fmtDate(g.dueDate)}` : ''}</p>
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
                  <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${PRIORITY_BADGE[g.priority] ?? PRIORITY_BADGE.Medium}`}>{g.priority}</span>
                  {statusBadge(g.status)}
                  {g.status === 'Active' && (
                    <button type="button" className={btn.ghost} onClick={() => { setProgressGoal(g); setProgressVal(String(g.actualValue)); setProgressNotes(''); }}>Update</button>
                  )}
                  {!g.managerApproved && (
                    <button type="button" className="rounded px-2 py-1 text-xs text-rose-600 hover:bg-rose-50 dark:text-rose-400 dark:hover:bg-rose-900/20" onClick={() => setDeleteId(g.id)}>Delete</button>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      {showCreate && <CreateGoalModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
      {progressGoal && (
        <Modal title={`Update Progress — ${progressGoal.title}`} onClose={() => setProgressGoal(null)}>
          <div className="space-y-4">
            <Field label={`New Actual Value (target: ${progressGoal.targetValue} ${progressGoal.measurementUnit})`}>
              <input type="number" title="New Actual Value" className={inp} value={progressVal} onChange={e => setProgressVal(e.target.value)} />
            </Field>
            <Field label="Notes"><textarea className={inp} value={progressNotes} onChange={e => setProgressNotes(e.target.value)} /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setProgressGoal(null)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={updateProgress}>Save Progress</button>
            </div>
          </div>
        </Modal>
      )}
      {deleteId && (
        <Modal title="Delete Goal" onClose={() => setDeleteId(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-700 dark:text-slate-300">This will permanently remove the goal and its progress history. Approved goals cannot be deleted.</p>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setDeleteId(null)}>Cancel</button>
              <button type="button" className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50" onClick={confirmDelete} disabled={deleting}>{deleting ? 'Deleting…' : 'Delete Goal'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Scorecard Templates Tab ───────────────────────────────────────────────────

function TemplateFormModal({ tpl, onClose, onSaved }: { tpl?: ScorecardTemplate; onClose: () => void; onSaved: () => void }) {
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
    if (!form.name) { setError('Name is required.'); return; }
    if (totalWeight !== 100) { setError(`Weights must total 100%. Currently: ${totalWeight}%`); return; }
    setSaving(true); setError('');
    try {
      if (tpl) await templatesApi.update(tpl.id, form);
      else await templatesApi.create(form);
      onSaved();
    } catch { setError('Save failed.'); setSaving(false); }
  };

  const weights: [keyof typeof form, string][] = [
    ['kpiWeight', 'KPI / Goals'], ['competencyWeight', 'Competencies'], ['attendanceWeight', 'Attendance'],
    ['productivityWeight', 'Productivity'], ['feedbackWeight', 'Feedback'], ['disciplineWeight', 'Discipline'],
  ];

  return (
    <Modal title={tpl ? 'Edit Template' : 'New Scorecard Template'} onClose={onClose} wide>
      <div className="space-y-4">
        <Field label="Template Name *"><input className={inp} value={form.name} onChange={e => set('name', e.target.value)} /></Field>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Department"><input className={inp} value={form.departmentName} onChange={e => set('departmentName', e.target.value)} placeholder="All departments" /></Field>
          <Field label="Designation"><input className={inp} value={form.designationTitle} onChange={e => set('designationTitle', e.target.value)} placeholder="All designations" /></Field>
          <Field label="Grade"><input className={inp} value={form.grade} onChange={e => set('grade', e.target.value)} placeholder="All grades" /></Field>
        </div>

        <div>
          <div className="mb-2 flex items-center justify-between">
            <p className="text-xs font-medium text-slate-600 dark:text-slate-300">Component Weights</p>
            <span className={`text-xs font-semibold ${totalWeight === 100 ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>Total: {totalWeight}%</span>
          </div>
          <div className="grid grid-cols-3 gap-3">
            {weights.map(([k, l]) => (
              <Field key={k} label={`${l} (%)`}>
                <input type="number" min={0} max={100} className={inp} value={form[k] as number} onChange={e => set(k, Number(e.target.value))} />
              </Field>
            ))}
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <Field label="Min Passing Score"><input type="number" min={0} max={100} className={inp} value={form.minPassingScore} onChange={e => set('minPassingScore', Number(e.target.value))} /></Field>
        </div>
        <div className="flex flex-wrap gap-4">
          {([['requiresCalibration', 'Requires Calibration'], ['requires360Feedback', 'Requires 360° Feedback'], ['isDefault', 'Default Template']] as [keyof typeof form, string][]).map(([k, l]) => (
            <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />
              {l}
            </label>
          ))}
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : tpl ? 'Update Template' : 'Create Template'}</button>
        </div>
      </div>
    </Modal>
  );
}

function TemplatesTab() {
  const [templates, setTemplates] = useState<ScorecardTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<ScorecardTemplate | undefined>(undefined);
  const [showCreate, setShowCreate] = useState(false);

  const load = () => { setLoading(true); templatesApi.list().then(setTemplates).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  const del = async (id: string) => {
    if (!confirm('Deactivate this template?')) return;
    try { await templatesApi.delete(id); load(); } catch { alert('Delete failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">{templates.length} template{templates.length !== 1 ? 's' : ''}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Template</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : templates.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Settings className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No scorecard templates yet</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2">
          {templates.map(t => (
            <div key={t.id} className="surface p-5">
              <div className="mb-3 flex items-start justify-between gap-2">
                <div>
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{t.name}</p>
                  <p className="text-xs text-slate-400">{t.departmentName || 'All depts'} · {t.designationTitle || 'All designations'} · {t.grade || 'All grades'}</p>
                  {t.isDefault && <span className="mt-1 inline-flex items-center rounded bg-sapphire/10 px-2 py-0.5 text-[10px] font-semibold text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent">DEFAULT</span>}
                </div>
                <div className="flex gap-1">
                  <button type="button" className={btn.ghost} onClick={() => setEditing(t)}>Edit</button>
                  <button type="button" className={btn.ghost} onClick={() => del(t.id)}>Delete</button>
                </div>
              </div>
              <div className="grid grid-cols-3 gap-2">
                {[['KPI', t.kpiWeight], ['Competency', t.competencyWeight], ['Attendance', t.attendanceWeight],
                  ['Productivity', t.productivityWeight], ['Feedback', t.feedbackWeight], ['Discipline', t.disciplineWeight]].map(([l, w]) => (
                  <div key={l} className="rounded-lg bg-slate-50 px-3 py-2 text-center dark:bg-white/5">
                    <p className="text-lg font-bold tabular-nums text-sapphire dark:text-cyanAccent">{w}%</p>
                    <p className="text-[10px] text-slate-400">{l}</p>
                  </div>
                ))}
              </div>
              <p className="mt-2 text-xs text-slate-400">Pass score: {t.minPassingScore}% · {t.requiresCalibration ? 'Calibration required' : 'No calibration'}</p>
            </div>
          ))}
        </div>
      )}
      {showCreate && <TemplateFormModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
      {editing && <TemplateFormModal tpl={editing} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); load(); }} />}
    </div>
  );
}

// ── Calibration Tab ───────────────────────────────────────────────────────────

function CalibrationTab() {
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [selectedCycle, setSelectedCycle] = useState('');
  const [board, setBoard] = useState<Awaited<ReturnType<typeof calibrationApi.getBoard>> | null>(null);
  const [loading, setLoading] = useState(false);
  const [adjustModal, setAdjustModal] = useState<{ reviewId: string; name: string; current: number } | null>(null);
  const [adjustment, setAdjustment] = useState('');
  const [adjustReason, setAdjustReason] = useState('');

  useEffect(() => { cyclesApi.list({ status: 'Calibration' }).then(r => { setCycles(r.items); if (r.items.length > 0) setSelectedCycle(r.items[0].id); }).catch(() => {}); }, []);

  useEffect(() => {
    if (!selectedCycle) return;
    setLoading(true);
    calibrationApi.getBoard(selectedCycle).then(setBoard).catch(() => {}).finally(() => setLoading(false));
  }, [selectedCycle]);

  const doAdjust = async () => {
    if (!adjustModal || !adjustReason) { alert('Reason is required for calibration adjustments.'); return; }
    try {
      await calibrationApi.adjust(selectedCycle, adjustModal.reviewId, Number(adjustment), adjustReason);
      setAdjustModal(null); setAdjustment(''); setAdjustReason('');
      calibrationApi.getBoard(selectedCycle).then(setBoard).catch(() => {});
    } catch { alert('Calibration adjustment failed.'); }
  };

  const reviews = (board?.reviews ?? []) as AppraisalReview[];
  const managerStats = (board?.managerStats ?? []) as { managerName: string; avgScore: number; count: number; possibleLeniency: boolean; possibleSeverity: boolean }[];

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select className={`${sel} w-72`} value={selectedCycle} onChange={e => setSelectedCycle(e.target.value)}>
          <option value="">Select cycle in Calibration status</option>
          {cycles.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </div>

      {cycles.length === 0 && (
        <div className="surface flex flex-col items-center py-16 text-center">
          <BarChart2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No cycles in Calibration status</p>
          <p className="text-xs text-slate-400">Advance an Active cycle through InReview to reach Calibration.</p>
        </div>
      )}

      {loading && <p className="text-sm text-slate-400">Loading board…</p>}

      {board && !loading && (
        <div className="space-y-4">
          {/* Manager bias alerts */}
          {managerStats.filter(m => m.possibleLeniency || m.possibleSeverity).length > 0 && (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-500/20 dark:bg-amber-500/10">
              <p className="mb-2 flex items-center gap-2 text-sm font-semibold text-amber-800 dark:text-amber-400"><AlertTriangle className="h-4 w-4" /> Bias Alerts (Human review required)</p>
              <div className="space-y-1">
                {managerStats.filter(m => m.possibleLeniency || m.possibleSeverity).map(m => (
                  <p key={m.managerName} className="text-xs text-amber-700 dark:text-amber-400">
                    {m.managerName}: avg {m.avgScore} — {m.possibleLeniency ? 'Possible leniency (avg > 87)' : 'Possible severity (avg < 48)'}
                  </p>
                ))}
              </div>
            </div>
          )}

          {/* Reviews list */}
          <div className="surface divide-y divide-slate-100 dark:divide-white/5">
            <div className="grid grid-cols-5 gap-4 px-5 py-3 text-xs font-medium uppercase tracking-wide text-slate-400">
              <span className="col-span-2">Employee</span><span>Score</span><span>Rating</span><span>Action</span>
            </div>
            {reviews.map(r => (
              <div key={r.id} className="grid grid-cols-5 items-center gap-4 px-5 py-3">
                <div className="col-span-2">
                  <p className="text-sm font-medium text-slate-900 dark:text-white">{r.employeeName}</p>
                  <p className="text-xs text-slate-400">{r.departmentName}</p>
                </div>
                <span className={`text-sm font-bold tabular-nums ${scoreColor(r.finalScore)}`}>{r.finalScore}</span>
                <span className="text-sm text-slate-600 dark:text-slate-300">{r.finalRating || '—'}</span>
                <button type="button" className={btn.ghost} onClick={() => { setAdjustModal({ reviewId: r.id, name: r.employeeName, current: r.finalScore }); setAdjustment('0'); setAdjustReason(''); }}>Adjust</button>
              </div>
            ))}
          </div>
        </div>
      )}

      {adjustModal && (
        <Modal title={`Calibrate — ${adjustModal.name}`} onClose={() => setAdjustModal(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-600 dark:text-slate-400">Current score: <strong className="text-slate-900 dark:text-white">{adjustModal.current}</strong></p>
            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-700 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-400">
              AI may surface outliers, but all calibration decisions are made by the calibration committee. A mandatory reason is required for audit purposes.
            </div>
            <Field label="Score Adjustment (e.g. +5 or -3)"><input type="number" className={inp} value={adjustment} onChange={e => setAdjustment(e.target.value)} /></Field>
            <Field label="Reason (required) *"><textarea className={inp} rows={3} value={adjustReason} onChange={e => setAdjustReason(e.target.value)} placeholder="Explain the calibration rationale…" /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setAdjustModal(null)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={doAdjust}>Apply Calibration</button>
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
    } catch { alert('Decision failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex w-fit gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
        {(['increments', 'promotions', 'bonuses'] as const).map(t => (
          <button key={t} type="button" onClick={() => setTab(t)} className={`rounded-lg px-4 py-1.5 text-sm font-medium capitalize transition ${tab === t ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}>{t}</button>
        ))}
      </div>

      <div className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:border-blue-500/20 dark:bg-blue-500/10 dark:text-blue-400">
        Approved recommendations are sent to Payroll as pending approved actions. They are not applied automatically — a Payroll manager must process them.
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {tab === 'increments' && (increments.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">No pending increments</p> : increments.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName} · {r.designationTitle}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">Current: {currencyCode} {fmtCurrency(r.currentSalary)} → New: {currencyCode} {fmtCurrency(r.newSalary)} (+{r.recommendedIncrementPct}%)</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'increment', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>Decide</button>
            </div>
          )))}
          {tab === 'promotions' && (promotions.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">No pending promotions</p> : promotions.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{r.currentDesignation} → {r.proposedDesignation} · effective {fmtDate(r.effectiveDate)}</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'promotion', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>Decide</button>
            </div>
          )))}
          {tab === 'bonuses' && (bonuses.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">No pending bonuses</p> : bonuses.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName} · {r.bonusType}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">Amount: {currencyCode} {fmtCurrency(r.bonusAmount)}</p>
              </div>
              <button type="button" className={btn.primary} onClick={() => { setDecideModal({ type: 'bonus', id: r.id, name: r.employeeName }); setDecision('Approved'); setNotes(''); }}>Decide</button>
            </div>
          )))}
        </div>
      )}

      {decideModal && (
        <Modal title={`Decision — ${decideModal.name}`} onClose={() => setDecideModal(null)}>
          <div className="space-y-4">
            <Field label="Decision">
              <select title="Decision" className={sel} value={decision} onChange={e => setDecision(e.target.value)}>
                <option value="Approved">Approve</option>
                <option value="Rejected">Reject</option>
              </select>
            </Field>
            <Field label="Notes"><textarea title="Notes" className={inp} rows={3} value={notes} onChange={e => setNotes(e.target.value)} /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setDecideModal(null)}>Cancel</button>
              <button type="button" className={decision === 'Approved' ? btn.primary : btn.danger} onClick={decide}>{decision}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── PIP & Probation Tab ───────────────────────────────────────────────────────

function CreatePIPModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [selectedEmployee, setSelectedEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({ performanceGaps: '', improvementGoals: '', supportPlan: '', startDate: '', endDate: '', hrNotes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!selectedEmployee) { setError('Please select an employee.'); return; }
    if (!form.performanceGaps || !form.improvementGoals || !form.startDate || !form.endDate) { setError('Performance gaps, goals and dates are required.'); return; }
    setSaving(true); setError('');
    try {
      await pipApi.create({ employeeId: selectedEmployee.intId, employeeName: selectedEmployee.fullName, departmentName: selectedEmployee.department, performanceGaps: form.performanceGaps, improvementGoals: form.improvementGoals, supportPlan: form.supportPlan, startDate: form.startDate, endDate: form.endDate, hrNotes: form.hrNotes });
      onSaved();
    } catch { setError('Failed to create PIP.'); setSaving(false); }
  };

  return (
    <Modal title="Create Performance Improvement Plan" onClose={onClose} wide>
      <div className="space-y-4">
        <Field label="Employee *">
          <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
        </Field>
        {selectedEmployee && (
          <p className="text-xs text-slate-500 dark:text-slate-400">Department: {selectedEmployee.department}</p>
        )}
        <Field label="Performance Gaps *"><textarea className={inp} rows={3} value={form.performanceGaps} onChange={e => set('performanceGaps', e.target.value)} placeholder="Describe specific performance deficiencies…" /></Field>
        <Field label="Improvement Goals *"><textarea className={inp} rows={3} value={form.improvementGoals} onChange={e => set('improvementGoals', e.target.value)} placeholder="Measurable objectives to achieve within the PIP period…" /></Field>
        <Field label="Support Plan"><textarea className={inp} rows={2} value={form.supportPlan} onChange={e => set('supportPlan', e.target.value)} placeholder="Training, coaching, resources provided…" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Start Date *"><input type="date" title="Start Date" className={inp} value={form.startDate} onChange={e => set('startDate', e.target.value)} /></Field>
          <Field label="End Date *"><input type="date" title="End Date" className={inp} value={form.endDate} onChange={e => set('endDate', e.target.value)} /></Field>
        </div>
        <Field label="HR Notes"><textarea title="HR Notes" className={inp} rows={2} value={form.hrNotes} onChange={e => set('hrNotes', e.target.value)} /></Field>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Creating…' : 'Create PIP'}</button>
        </div>
      </div>
    </Modal>
  );
}

function PIPProbationTab() {
  const [subTab, setSubTab] = useState<'pip' | 'probation'>('pip');
  const [pips, setPips] = useState<PerformanceImprovementPlan[]>([]);
  const [probations, setProbations] = useState<ProbationReview[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreatePIP, setShowCreatePIP] = useState(false);
  const [statusModal, setStatusModal] = useState<{ id: string; name: string } | null>(null);
  const [newStatus, setNewStatus] = useState('Improved');
  const [statusNotes, setStatusNotes] = useState('');

  const load = () => {
    setLoading(true);
    Promise.all([
      pipApi.list().then(setPips),
      probationApi.list().then(setProbations),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const updatePIPStatus = async () => {
    if (!statusModal) return;
    try { await pipApi.updateStatus(statusModal.id, newStatus, statusNotes); setStatusModal(null); load(); }
    catch { alert('Status update failed.'); }
  };

  const hrDecideProbation = async (id: string, decision: string) => {
    const notes = prompt('HR notes (optional):') ?? '';
    try { await probationApi.hrDecision(id, decision, notes); load(); }
    catch { alert('Decision failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex w-fit gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
        <button type="button" onClick={() => setSubTab('pip')} className={`rounded-lg px-4 py-1.5 text-sm font-medium transition ${subTab === 'pip' ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>PIPs ({pips.length})</button>
        <button type="button" onClick={() => setSubTab('probation')} className={`rounded-lg px-4 py-1.5 text-sm font-medium transition ${subTab === 'probation' ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>Probation ({probations.length})</button>
      </div>

      {subTab === 'pip' && (
        <div className="space-y-4">
          <div className="flex justify-end">
            <button type="button" className={btn.primary} onClick={() => setShowCreatePIP(true)}><Plus className="h-4 w-4" /> New PIP</button>
          </div>
          {loading ? <p className="text-sm text-slate-400">Loading…</p> : pips.length === 0 ? (
            <div className="surface flex flex-col items-center py-16 text-center">
              <FileText className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No active PIPs</p>
            </div>
          ) : (
            <div className="surface divide-y divide-slate-100 dark:divide-white/5">
              {pips.map(p => (
                <div key={p.id} className="flex items-center justify-between px-5 py-4">
                  <div className="min-w-0">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{p.employeeName}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{p.departmentName} · {fmtDate(p.startDate)} – {fmtDate(p.endDate)}</p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400 line-clamp-1">{p.improvementGoals}</p>
                  </div>
                  <div className="ml-4 flex shrink-0 items-center gap-3">
                    {statusBadge(p.status)}
                    {p.status === 'Active' && (
                      <button type="button" className={btn.ghost} onClick={() => { setStatusModal({ id: p.id, name: p.employeeName }); setNewStatus('Improved'); setStatusNotes(''); }}>Update Status</button>
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
          {loading ? <p className="text-sm text-slate-400">Loading…</p> : probations.length === 0 ? (
            <div className="surface flex flex-col items-center py-16 text-center">
              <Clock className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No probation reviews</p>
            </div>
          ) : (
            <div className="surface divide-y divide-slate-100 dark:divide-white/5">
              {probations.map(p => (
                <div key={p.id} className="flex items-center justify-between px-5 py-4">
                  <div className="min-w-0">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{p.employeeName}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{p.departmentName} · {p.designationTitle}</p>
                    <p className="text-xs text-slate-400">Probation: {fmtDate(p.probationStartDate)} – {fmtDate(p.probationEndDate)}</p>
                    {p.managerRecommendation && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Manager: {p.managerRecommendation}</p>}
                  </div>
                  <div className="ml-4 flex shrink-0 items-center gap-2">
                    {statusBadge(p.status)}
                    {p.status === 'ManagerReviewed' && (
                      <div className="flex gap-1">
                        <button type="button" className={btn.primary} onClick={() => hrDecideProbation(p.id, 'Confirmed')}>Confirm</button>
                        <button type="button" className={btn.ghost} onClick={() => hrDecideProbation(p.id, 'Extended')}>Extend</button>
                        <button type="button" className={btn.danger} onClick={() => hrDecideProbation(p.id, 'Terminated')}>Terminate</button>
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
      {statusModal && (
        <Modal title={`Update PIP Status — ${statusModal.name}`} onClose={() => setStatusModal(null)}>
          <div className="space-y-4">
            <Field label="New Status">
              <select title="New Status" className={sel} value={newStatus} onChange={e => setNewStatus(e.target.value)}>
                {['Improved', 'Extended', 'Failed', 'TerminationRecommended'].map(s => <option key={s}>{s}</option>)}
              </select>
            </Field>
            {newStatus === 'TerminationRecommended' && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-400">
                This is a recommendation only — not an automatic termination. HR and leadership must make the final employment decision.
              </div>
            )}
            <Field label="Notes"><textarea title="Status Notes" className={inp} rows={3} value={statusNotes} onChange={e => setStatusNotes(e.target.value)} /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setStatusModal(null)}>Cancel</button>
              <button type="button" className={newStatus === 'TerminationRecommended' || newStatus === 'Failed' ? btn.danger : btn.primary} onClick={updatePIPStatus}>Update</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Analytics Tab ─────────────────────────────────────────────────────────────

function AnalyticsTab() {
  const [cycles, setCycles] = useState<PerformanceCycle[]>([]);
  const [selectedCycle, setSelectedCycle] = useState('');
  const [analytics, setAnalytics] = useState<CycleAnalytics | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { cyclesApi.list().then(r => { setCycles(r.items); if (r.items.length > 0) setSelectedCycle(r.items[0].id); }).catch(() => {}); }, []);
  useEffect(() => {
    if (!selectedCycle) return;
    setLoading(true);
    analyticsApi.cycleAnalytics(selectedCycle).then(setAnalytics).catch(() => {}).finally(() => setLoading(false));
  }, [selectedCycle]);

  return (
    <div className="space-y-4">
      <select title="Select Cycle" className={`${sel} w-72`} value={selectedCycle} onChange={e => setSelectedCycle(e.target.value)}>
        <option value="">Select cycle</option>
        {cycles.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
      </select>

      {loading && <p className="text-sm text-slate-400">Loading analytics…</p>}

      {analytics && !loading && (
        <div className="space-y-4">
          {/* Summary */}
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <KpiCard label="Total Enrolled" value={analytics.summary.totalEnrolled} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
            <KpiCard label="Completion Rate" value={`${analytics.summary.completionRate}%`} icon={CheckCircle} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
            <KpiCard label="Avg Score" value={analytics.summary.overallAvgScore} icon={BarChart2} color="bg-blue-100 text-blue-600 dark:bg-blue-500/20 dark:text-blue-400" />
            <KpiCard label="High Performers" value={analytics.summary.highPerformers} sub="Score ≥ 85" icon={Star} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            {/* Rating Distribution */}
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Rating Distribution</h3>
              {analytics.distribution.length === 0 ? <p className="text-sm text-slate-400">No completed reviews yet.</p> : (
                <div className="space-y-3">
                  {analytics.distribution.map(d => (
                    <div key={d.rating} className="flex items-center gap-3">
                      <span className="w-28 shrink-0 text-xs text-slate-600 dark:text-slate-400">{d.rating || 'Unrated'}</span>
                      <div className="flex-1 rounded-full bg-slate-100 dark:bg-white/10">
                        <div className="h-2 rounded-full bg-sapphire" style={{ width: `${d.pct}%` }} />{/* dynamic — inline required */}
                      </div>
                      <span className="w-12 text-right text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{d.count} ({d.pct}%)</span>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Dept Averages */}
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Department Averages</h3>
              {analytics.deptAvg.length === 0 ? <p className="text-sm text-slate-400">No data yet.</p> : (
                <div className="space-y-3">
                  {analytics.deptAvg.slice(0, 8).map(d => (
                    <div key={d.department} className="flex items-center gap-3">
                      <span className="min-w-0 flex-1 truncate text-xs text-slate-600 dark:text-slate-400">{d.department}</span>
                      <div className="w-32 rounded-full bg-slate-100 dark:bg-white/10">
                        <div className={`h-2 rounded-full ${d.avgScore >= 75 ? 'bg-emerald-500' : d.avgScore >= 60 ? 'bg-sapphire' : 'bg-rose-400'}`} style={{ width: `${d.avgScore}%` }} />
                      </div>
                      <span className="w-8 text-right text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{d.avgScore}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Top performers */}
          {analytics.topPerformers.length > 0 && (
            <div className="surface p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Top Performers (Score ≥ 85)</h3>
              <div className="divide-y divide-slate-100 dark:divide-white/5">
                {analytics.topPerformers.map((p, i) => (
                  <div key={p.employeeId} className="flex items-center justify-between py-2">
                    <div className="flex items-center gap-3">
                      <span className="w-6 text-center text-xs font-bold text-slate-400">#{i + 1}</span>
                      <div>
                        <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{p.employeeName}</p>
                        <p className="text-xs text-slate-400">{p.departmentName}</p>
                      </div>
                    </div>
                    <div className="text-right">
                      <p className="text-sm font-bold text-emerald-600 dark:text-emerald-400">{p.finalScore}</p>
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
                <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Manager Rating Analysis</h3>
                <span className="rounded bg-slate-100 px-2 py-0.5 text-[10px] font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400">Human review required</span>
              </div>
              <div className="divide-y divide-slate-100 dark:divide-white/5">
                {analytics.managerBias.map(m => (
                  <div key={m.managerName} className="flex items-center justify-between py-3">
                    <div>
                      <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{m.managerName}</p>
                      <p className="text-xs text-slate-400">{m.count} direct reports rated</p>
                    </div>
                    <div className="text-right">
                      <p className={`text-sm font-bold tabular-nums ${scoreColor(m.avgScore)}`}>{m.avgScore} avg</p>
                      {m.possibleLeniency && <span className="text-[10px] text-amber-500">Possible leniency</span>}
                      {m.possibleSeverity && <span className="text-[10px] text-rose-500">Possible severity</span>}
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
    catch { alert('Failed to submit feedback.'); }
    setSaving(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> Give Feedback</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : feedbacks.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Star className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No continuous feedback entries yet</p>
        </div>
      ) : (
        <div className="space-y-3">
          {feedbacks.map(f => (
            <div key={f.id} className="surface p-4">
              <div className="mb-2 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">{f.employeeName}</span>
                  <span className="rounded bg-sapphire/10 px-2 py-0.5 text-[10px] font-medium text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent">{f.feedbackType}</span>
                  {f.isPrivate && <span className="rounded bg-slate-100 px-2 py-0.5 text-[10px] font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400">Private</span>}
                </div>
                <span className="text-xs text-slate-400">{fmtDate(f.createdAtUtc)} · by {f.givenByName}</span>
              </div>
              <p className="text-sm text-slate-700 dark:text-slate-300">{f.content}</p>
            </div>
          ))}
        </div>
      )}

      {showCreate && (
        <Modal title="Give Continuous Feedback" onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            <Field label="Employee *">
              <EmployeeSearchSelect value={selectedEmployee} onChange={setSelectedEmployee} required />
            </Field>
            <Field label="Feedback Type">
              <select title="Feedback Type" className={sel} value={form.feedbackType} onChange={e => set('feedbackType', e.target.value)}>
                {['Commendation', 'Concern', 'Coaching', 'Developmental', 'Recognition'].map(t => <option key={t}>{t}</option>)}
              </select>
            </Field>
            <Field label="Feedback *"><textarea className={inp} rows={4} value={form.content} onChange={e => set('content', e.target.value)} placeholder="Share specific, constructive observations…" /></Field>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.isPrivate} onChange={e => set('isPrivate', e.target.checked)} className="rounded" />
              Private (only visible to you and HR)
            </label>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Submitting…' : 'Submit Feedback'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Root Page ─────────────────────────────────────────────────────────────────

type Tab = 'overview' | 'cycles' | 'my-reviews' | 'team-reviews' | 'goals' | 'templates' | 'calibration' | 'recommendations' | 'pip' | 'analytics' | 'feedback';

const TABS: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: 'overview', label: 'Overview', icon: Activity },
  { id: 'cycles', label: 'Cycles', icon: ClipboardList },
  { id: 'my-reviews', label: 'My Reviews', icon: Star },
  { id: 'team-reviews', label: 'Team Reviews', icon: Users },
  { id: 'goals', label: 'Goals & KPIs', icon: Target },
  { id: 'templates', label: 'Scorecards', icon: Settings },
  { id: 'calibration', label: 'Calibration', icon: BarChart2 },
  { id: 'recommendations', label: 'Recommendations', icon: TrendingUp },
  { id: 'pip', label: 'PIP & Probation', icon: AlertTriangle },
  { id: 'analytics', label: 'Analytics', icon: Zap },
  { id: 'feedback', label: 'Feedback', icon: FileText },
];

export function PerformancePage({ initialTab }: { initialTab?: Tab } = {}) {
  const [tab, setTab] = useState<Tab>(initialTab ?? 'overview');

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Performance & Appraisals</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          Balanced scorecard appraisals, goal management, calibration, recommendations, PIPs, and analytics.
        </p>
      </div>

      {/* Tab bar — scrollable on mobile */}
      <div className="overflow-x-auto">
        <div className="flex w-max gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]">
          {TABS.map(t => (
            <button
              key={t.id}
              type="button"
              onClick={() => setTab(t.id)}
              className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${tab === t.id ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}
            >
              <t.icon className="h-3.5 w-3.5" />
              {t.label}
            </button>
          ))}
        </div>
      </div>

      {tab === 'overview' && <OverviewTab onNavigate={setTab} />}
      {tab === 'cycles' && <CyclesTab />}
      {tab === 'my-reviews' && <MyReviewsTab />}
      {tab === 'team-reviews' && <TeamReviewsTab />}
      {tab === 'goals' && <GoalsTab />}
      {tab === 'templates' && <TemplatesTab />}
      {tab === 'calibration' && <CalibrationTab />}
      {tab === 'recommendations' && <RecommendationsTab />}
      {tab === 'pip' && <PIPProbationTab />}
      {tab === 'analytics' && <AnalyticsTab />}
      {tab === 'feedback' && <FeedbackTab />}
    </div>
  );
}
