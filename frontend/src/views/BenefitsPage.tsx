'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle, CheckCircle2, CircleSlash, HeartPulse, Link2, Loader2, Pencil, Plus, RefreshCw, Search, ShieldCheck, UserPlus, X, XCircle,
} from 'lucide-react';
import {
  benefitsApi, benefitsErrorMessage,
  type BenefitDeductionCandidate, type BenefitEligibilityCheck, type BenefitEligibilityRule,
  type BenefitEnrollment, type BenefitEnrollmentDetail, type BenefitPlan,
} from '@/src/api/benefits';
import { employeesApi, type EmployeeListItem } from '@/src/api/employees';
import { gradesApi, type GradeDto } from '@/src/api/organization';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useAppToast } from '@/src/components/ui/AppToast';

// ── shared styling ──────────────────────────────────────────────────────────────

const INPUT = 'mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100';
const LABEL = 'block text-xs font-medium text-slate-600 dark:text-slate-300';
const PRIMARY = 'flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50';
const SECONDARY = 'flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.08]';
const CARD = 'rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]';

const PLAN_TYPES = ['Medical', 'Dental', 'Life', 'Vision', 'Pension', 'Education', 'Housing', 'Transport', 'Other'];
const COVERAGE_TIERS = ['Employee', 'Employee + Spouse', 'Employee + Children', 'Family'];
const today = () => new Date().toISOString().slice(0, 10);
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const range = (from: string, to: string | null) => `${from} → ${to ?? 'open-ended'}`;

function StatusPill({ active, label }: { active: boolean; label?: string }) {
  return (
    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${active
      ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
      : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}>
      {label ?? (active ? 'Active' : 'Inactive')}
    </span>
  );
}

function Modal({ title, onClose, children, wide = false }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label={title}>
      <div className={`max-h-[90vh] w-full overflow-y-auto rounded-2xl border border-slate-200 bg-white p-5 shadow-2xl dark:border-white/[0.08] dark:bg-[#0c1120] ${wide ? 'max-w-2xl' : 'max-w-md'}`}>
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{title}</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg p-1 text-slate-400 hover:bg-slate-100 dark:hover:bg-white/[0.06]"><X className="h-4 w-4" /></button>
        </div>
        {children}
      </div>
    </div>
  );
}

function FormError({ message }: { message: string | null }) {
  if (!message) return null;
  return <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{message}</p>;
}

// ── page ────────────────────────────────────────────────────────────────────────

type Tab = 'plans' | 'enrollments';

export function BenefitsPage() {
  const { hasRole } = useAuth();
  const { companies, companyVersion } = useCompany();
  const canManagePlans = hasRole('Admin') || hasRole('HR Manager');
  const canEnroll = canManagePlans || hasRole('HR Officer');
  const canRecordMoney = canManagePlans || hasRole('Finance');

  const [tab, setTab] = useState<Tab>('plans');
  const [plans, setPlans] = useState<BenefitPlan[]>([]);
  const [enrollments, setEnrollments] = useState<BenefitEnrollment[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [selectedPlanId, setSelectedPlanId] = useState<string | null>(null);
  const [planModal, setPlanModal] = useState<{ mode: 'create' } | { mode: 'edit'; plan: BenefitPlan } | null>(null);
  const [enrollFor, setEnrollFor] = useState<{ planId?: string } | null>(null);
  const [openEnrollmentId, setOpenEnrollmentId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [p, e] = await Promise.all([benefitsApi.listPlans(), benefitsApi.listEnrollments()]);
      setPlans(p);
      setEnrollments(e);
    } catch (err) {
      setError(benefitsErrorMessage(err, 'Could not load benefits.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load, companyVersion]);
  useEffect(() => {
    gradesApi.list(1, 200).then((r) => setGrades(r.items ?? [])).catch(() => setGrades([]));
  }, []);

  const companyName = useCallback((id: string | null) =>
    id === null ? 'All companies' : (companies.find((c) => c.id === id)?.name ?? 'Company'), [companies]);
  const gradeName = useCallback((id: string | null) =>
    id === null ? 'Any grade' : (grades.find((g) => g.id === id)?.name ?? 'Grade'), [grades]);

  const selectedPlan = plans.find((p) => p.id === selectedPlanId) ?? null;
  const enrolledCount = useMemo(() => {
    const m = new Map<string, number>();
    enrollments.filter((e) => e.status === 'Active').forEach((e) => m.set(e.benefitPlanId, (m.get(e.benefitPlanId) ?? 0) + 1));
    return m;
  }, [enrollments]);
  const activePlans = plans.filter((p) => p.isActive);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Benefits Administration</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">Plans, eligibility, enrolments, contributions and payroll deduction links</p>
        </div>
        {!loading && !error && plans.length > 0 && (
          <div className="flex gap-2">
            {canEnroll && (
              <button type="button" className={SECONDARY} disabled={activePlans.length === 0} onClick={() => setEnrollFor({})}>
                <UserPlus className="h-3.5 w-3.5" /> Enrol employee
              </button>
            )}
            {canManagePlans && (
              <button type="button" className={PRIMARY} onClick={() => setPlanModal({ mode: 'create' })}>
                <Plus className="h-3.5 w-3.5" /> New plan
              </button>
            )}
          </div>
        )}
      </div>

      {loading ? (
        <div className="space-y-3" aria-busy="true" aria-label="Loading benefits">
          <div className="h-10 w-64 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />
          <div className="h-64 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : plans.length === 0 ? (
        <FirstPlanSetup canManage={canManagePlans} onCreate={() => setPlanModal({ mode: 'create' })} />
      ) : (
        <>
          <div className="flex gap-1 rounded-xl border border-slate-200 bg-white p-1 dark:border-white/[0.06] dark:bg-white/[0.03]" role="tablist">
            {(['plans', 'enrollments'] as Tab[]).map((t) => (
              <button key={t} type="button" role="tab" aria-selected={tab === t} onClick={() => setTab(t)}
                className={`rounded-lg px-3 py-1.5 text-xs font-semibold transition ${tab === t
                  ? 'bg-slate-800 text-white dark:bg-white dark:text-slate-900'
                  : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]'}`}>
                {t === 'plans' ? `Plans (${plans.length})` : `Enrolments (${enrollments.length})`}
              </button>
            ))}
          </div>

          {tab === 'plans' ? (
            <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
              <PlanList plans={plans} selectedId={selectedPlanId} onSelect={setSelectedPlanId}
                companyName={companyName} enrolledCount={enrolledCount} />
              {selectedPlan ? (
                <PlanDetail key={selectedPlan.id} plan={selectedPlan} companyName={companyName} gradeName={gradeName}
                  companies={companies} grades={grades} canManage={canManagePlans} canEnroll={canEnroll}
                  onEdit={() => setPlanModal({ mode: 'edit', plan: selectedPlan })}
                  onEnroll={() => setEnrollFor({ planId: selectedPlan.id })} />
              ) : (
                <div className={`${CARD} flex flex-col items-center justify-center gap-2 p-10 text-center`}>
                  <HeartPulse className="h-8 w-8 text-slate-300 dark:text-slate-600" />
                  <p className="text-sm font-medium text-slate-600 dark:text-slate-300">Select a plan</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">View and edit its details and eligibility rules, or enrol employees in it.</p>
                </div>
              )}
            </div>
          ) : (
            <EnrollmentList enrollments={enrollments} plans={plans} companies={companies} companyName={companyName}
              canEnroll={canEnroll && activePlans.length > 0} onEnroll={() => setEnrollFor({})} onOpen={setOpenEnrollmentId} />
          )}
        </>
      )}

      {planModal && (
        <PlanModal
          initial={planModal.mode === 'edit' ? planModal.plan : null}
          companies={companies}
          onClose={() => setPlanModal(null)}
          onSaved={(p) => { setPlanModal(null); setSelectedPlanId(p.id); setTab('plans'); void load(); }}
        />
      )}
      {enrollFor && (
        <EnrollModal
          plans={activePlans} initialPlanId={enrollFor.planId}
          onClose={() => setEnrollFor(null)}
          onEnrolled={() => { setEnrollFor(null); setTab('enrollments'); void load(); }}
        />
      )}
      {openEnrollmentId && (
        <EnrollmentDrawer enrollmentId={openEnrollmentId} plans={plans} canRecord={canRecordMoney}
          onClose={() => setOpenEnrollmentId(null)} />
      )}
    </div>
  );
}

// ── empty state: guided first plan ──────────────────────────────────────────────

function FirstPlanSetup({ canManage, onCreate }: { canManage: boolean; onCreate: () => void }) {
  const steps = [
    { n: 1, title: 'Create a plan', body: 'Medical, dental, life or any other benefit, with its currency and effective dates.' },
    { n: 2, title: 'Set eligibility (optional)', body: 'Limit the plan to a company or a grade. With no rules, everyone in the plan’s scope is eligible.' },
    { n: 3, title: 'Enrol employees', body: 'Eligibility is checked before you submit, then record contributions and link payroll deductions.' },
  ];
  return (
    <div data-testid="benefits-empty" className={`${CARD} p-8`}>
      <div className="mx-auto max-w-2xl text-center">
        <HeartPulse className="mx-auto h-10 w-10 text-sapphire" />
        <h2 className="mt-3 text-base font-bold text-slate-800 dark:text-slate-100">Set up your first benefit plan</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">No benefit plans exist yet. Three steps get employees covered.</p>
      </div>
      <ol className="mx-auto mt-6 grid max-w-3xl gap-3 sm:grid-cols-3">
        {steps.map((s) => (
          <li key={s.n} className="rounded-xl border border-slate-200 p-4 dark:border-white/[0.06]">
            <span className="flex h-6 w-6 items-center justify-center rounded-full bg-sapphire text-xs font-bold text-white">{s.n}</span>
            <p className="mt-2 text-sm font-semibold text-slate-800 dark:text-slate-100">{s.title}</p>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{s.body}</p>
          </li>
        ))}
      </ol>
      <div className="mt-6 flex justify-center">
        {canManage ? (
          <button type="button" className={`${PRIMARY} px-4 py-2.5 text-sm`} onClick={onCreate}>
            <Plus className="h-4 w-4" /> Create your first plan
          </button>
        ) : (
          <p className="text-xs text-slate-500 dark:text-slate-400">Ask an Admin or HR Manager to create the first plan.</p>
        )}
      </div>
    </div>
  );
}

// ── plans ───────────────────────────────────────────────────────────────────────

function PlanList({ plans, selectedId, onSelect, companyName, enrolledCount }: {
  plans: BenefitPlan[]; selectedId: string | null; onSelect: (id: string) => void;
  companyName: (id: string | null) => string; enrolledCount: Map<string, number>;
}) {
  return (
    <div className={`${CARD} divide-y divide-slate-100 dark:divide-white/[0.04]`} data-testid="benefit-plan-list">
      {plans.map((p) => (
        <button key={p.id} type="button" onClick={() => onSelect(p.id)} aria-pressed={selectedId === p.id}
          className={`flex w-full items-start justify-between gap-3 px-4 py-3 text-start transition first:rounded-t-2xl last:rounded-b-2xl ${selectedId === p.id
            ? 'bg-sapphire/[0.06] dark:bg-sapphire/[0.12]'
            : 'hover:bg-slate-50 dark:hover:bg-white/[0.02]'}`}>
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold text-slate-800 dark:text-slate-100">{p.name}</p>
            <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">
              <span className="font-mono">{p.code}</span> · {p.planType} · {p.currency} · {companyName(p.companyId)}
            </p>
            <p className="text-[11px] text-slate-400">{range(p.effectiveFrom, p.effectiveTo)}</p>
          </div>
          <div className="flex shrink-0 flex-col items-end gap-1">
            <StatusPill active={p.isActive} />
            <span className="text-[11px] text-slate-500 dark:text-slate-400">{enrolledCount.get(p.id) ?? 0} enrolled</span>
          </div>
        </button>
      ))}
    </div>
  );
}

function PlanDetail({ plan, companyName, gradeName, companies, grades, canManage, canEnroll, onEdit, onEnroll }: {
  plan: BenefitPlan; companyName: (id: string | null) => string; gradeName: (id: string | null) => string;
  companies: { id: string; name: string }[]; grades: GradeDto[];
  canManage: boolean; canEnroll: boolean; onEdit: () => void; onEnroll: () => void;
}) {
  const toast = useAppToast();
  const [rules, setRules] = useState<BenefitEligibilityRule[] | null>(null);
  const [rulesError, setRulesError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const loadRules = useCallback(async () => {
    setRulesError(null);
    try { setRules(await benefitsApi.listRules(plan.id)); }
    catch (e) { setRules([]); setRulesError(benefitsErrorMessage(e, 'Could not load eligibility rules.')); }
  }, [plan.id]);
  useEffect(() => { void loadRules(); }, [loadRules]);

  const deactivate = async (rule: BenefitEligibilityRule) => {
    try {
      await benefitsApi.deactivateRule(plan.id, rule.id);
      toast.success('Eligibility rule deactivated.');
      void loadRules();
    } catch (e) { toast.error(benefitsErrorMessage(e, 'Could not deactivate the rule.')); }
  };

  const activeRules = (rules ?? []).filter((r) => r.isActive);

  return (
    <div className={`${CARD} space-y-4 p-4`} data-testid="benefit-plan-detail">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="text-base font-bold text-slate-800 dark:text-slate-100">{plan.name}</h2>
          <p className="text-xs text-slate-500 dark:text-slate-400"><span className="font-mono">{plan.code}</span> · {plan.planType} · {plan.currency}</p>
        </div>
        <div className="flex gap-2">
          {canManage && <button type="button" className={SECONDARY} onClick={onEdit}><Pencil className="h-3.5 w-3.5" /> Edit plan</button>}
          {canEnroll && plan.isActive && <button type="button" className={PRIMARY} onClick={onEnroll}><UserPlus className="h-3.5 w-3.5" /> Enrol in this plan</button>}
        </div>
      </div>
      <dl className="grid grid-cols-2 gap-3 text-xs">
        <div><dt className="text-slate-500 dark:text-slate-400">Company scope</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{companyName(plan.companyId)}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Effective</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{range(plan.effectiveFrom, plan.effectiveTo)}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Enrolment</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{plan.requiresEnrollment ? 'Requires enrolment' : 'Automatic'}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Status</dt><dd><StatusPill active={plan.isActive} /></dd></div>
      </dl>

      <section>
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">Eligibility rules</h3>
          {canManage && !adding && (
            <button type="button" className={SECONDARY} onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> Add rule</button>
          )}
        </div>
        <p className="mb-2 text-[11px] text-slate-500 dark:text-slate-400">
          An employee is eligible when they match any active rule in effect on their start date. With no rules in effect, the plan is open to everyone in its company scope.
        </p>
        {adding && (
          <AddRuleForm planId={plan.id} planFrom={plan.effectiveFrom} companies={companies} grades={grades}
            onCancel={() => setAdding(false)} onAdded={() => { setAdding(false); void loadRules(); }} />
        )}
        {rules === null ? (
          <div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />
        ) : rulesError ? (
          <FormError message={rulesError} />
        ) : rules.length === 0 ? (
          <div data-testid="rules-empty" className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">
            No eligibility rules. Every employee in <span className="font-semibold">{companyName(plan.companyId)}</span> can be enrolled.
          </div>
        ) : (
          <ul className="space-y-1.5" data-testid="rules-list">
            {rules.map((r) => (
              <li key={r.id} className={`flex items-center justify-between gap-2 rounded-xl border px-3 py-2 text-xs ${r.isActive
                ? 'border-slate-200 dark:border-white/[0.06]' : 'border-dashed border-slate-200 opacity-60 dark:border-white/[0.06]'}`}>
                <div>
                  <p className="font-semibold text-slate-800 dark:text-slate-100">{companyName(r.companyId)} · {gradeName(r.gradeId)}</p>
                  <p className="text-slate-500 dark:text-slate-400">{range(r.effectiveFrom, r.effectiveTo)}</p>
                </div>
                <div className="flex items-center gap-2">
                  <StatusPill active={r.isActive} />
                  {canManage && r.isActive && (
                    <button type="button" onClick={() => void deactivate(r)} className="rounded-lg p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-500/[0.08]" aria-label="Deactivate rule">
                      <CircleSlash className="h-3.5 w-3.5" />
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
        {activeRules.length > 0 && (
          <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">{activeRules.length} active rule(s) restrict this plan.</p>
        )}
      </section>
    </div>
  );
}

function AddRuleForm({ planId, planFrom, companies, grades, onCancel, onAdded }: {
  planId: string; planFrom: string; companies: { id: string; name: string }[]; grades: GradeDto[];
  onCancel: () => void; onAdded: () => void;
}) {
  const toast = useAppToast();
  const [companyId, setCompanyId] = useState('');
  const [gradeId, setGradeId] = useState('');
  const [from, setFrom] = useState(planFrom);
  const [to, setTo] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!companyId && !gradeId) { setError('Pick a company, a grade, or both. A rule with neither would match everyone.'); return; }
    setSaving(true); setError(null);
    try {
      await benefitsApi.addRule(planId, { companyId: companyId || null, gradeId: gradeId || null, effectiveFrom: from, effectiveTo: to || null, isActive: true });
      toast.success('Eligibility rule added.');
      onAdded();
    } catch (err) { setError(benefitsErrorMessage(err, 'Could not add the rule.')); }
    finally { setSaving(false); }
  };

  return (
    <form onSubmit={submit} aria-label="Add eligibility rule" className="mb-3 space-y-2 rounded-xl border border-sapphire/30 bg-sapphire/[0.03] p-3 dark:bg-sapphire/[0.06]">
      <FormError message={error} />
      <div className="grid grid-cols-2 gap-2">
        <label className={LABEL}>Company
          <select className={INPUT} value={companyId} onChange={(e) => setCompanyId(e.target.value)} aria-label="Rule company">
            <option value="">Any company</option>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label className={LABEL}>Grade
          <select className={INPUT} value={gradeId} onChange={(e) => setGradeId(e.target.value)} aria-label="Rule grade">
            <option value="">Any grade</option>
            {grades.map((g) => <option key={g.id} value={g.id}>{g.name} ({g.code})</option>)}
          </select>
        </label>
        <label className={LABEL}>Effective from
          <input type="date" required className={INPUT} value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className={LABEL}>Effective to
          <input type="date" className={INPUT} value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onCancel} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
        <button type="submit" disabled={saving} className={PRIMARY}>{saving ? 'Saving…' : 'Save rule'}</button>
      </div>
    </form>
  );
}

function PlanModal({ initial, companies, onClose, onSaved }: {
  initial: BenefitPlan | null; companies: { id: string; name: string }[];
  onClose: () => void; onSaved: (p: BenefitPlan) => void;
}) {
  const toast = useAppToast();
  const editing = initial !== null;
  const [form, setForm] = useState({
    companyId: initial?.companyId ?? '',
    code: initial?.code ?? '',
    name: initial?.name ?? '',
    planType: initial?.planType ?? 'Medical',
    currency: initial?.currency ?? 'SAR',
    effectiveFrom: initial?.effectiveFrom ?? `${new Date().getFullYear()}-01-01`,
    effectiveTo: initial?.effectiveTo ?? '',
    requiresEnrollment: initial?.requiresEnrollment ?? true,
    isActive: initial?.isActive ?? true,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) => setForm((f) => ({ ...f, [k]: v }));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (form.effectiveTo && form.effectiveTo < form.effectiveFrom) { setError('Effective to cannot be before effective from.'); return; }
    setSaving(true); setError(null);
    try {
      const body = {
        name: form.name.trim(), planType: form.planType, currency: form.currency.trim().toUpperCase(),
        effectiveFrom: form.effectiveFrom, effectiveTo: form.effectiveTo || null,
        requiresEnrollment: form.requiresEnrollment, isActive: form.isActive,
      };
      const saved = editing
        ? await benefitsApi.updatePlan(initial!.id, body)
        : await benefitsApi.createPlan({ ...body, companyId: form.companyId || null, code: form.code.trim().toUpperCase() });
      toast.success(editing ? 'Plan updated.' : 'Plan created. Add eligibility rules or enrol employees next.');
      onSaved(saved);
    } catch (err) { setError(benefitsErrorMessage(err, editing ? 'Could not update the plan.' : 'Could not create the plan.')); }
    finally { setSaving(false); }
  };

  return (
    <Modal title={editing ? `Edit plan ${initial!.code}` : 'New benefit plan'} onClose={onClose}>
      <form onSubmit={submit} className="space-y-3">
        <FormError message={error} />
        <div className="grid grid-cols-2 gap-3">
          <label className={LABEL}>Plan code <span className="text-rose-500">*</span>
            <input required disabled={editing} value={form.code} onChange={(e) => set('code', e.target.value)} className={`${INPUT} disabled:opacity-60`} placeholder="MED-GOLD" />
          </label>
          <label className={LABEL}>Plan type
            <select className={INPUT} value={form.planType} onChange={(e) => set('planType', e.target.value)}>
              {PLAN_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
          </label>
        </div>
        <label className={LABEL}>Plan name <span className="text-rose-500">*</span>
          <input required value={form.name} onChange={(e) => set('name', e.target.value)} className={INPUT} placeholder="Medical Gold" />
        </label>
        <div className="grid grid-cols-2 gap-3">
          <label className={LABEL}>Company scope
            <select disabled={editing} className={`${INPUT} disabled:opacity-60`} value={form.companyId} onChange={(e) => set('companyId', e.target.value)}>
              <option value="">All companies</option>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label className={LABEL}>Currency <span className="text-rose-500">*</span>
            <input required maxLength={3} value={form.currency} onChange={(e) => set('currency', e.target.value)} className={INPUT} />
          </label>
          <label className={LABEL}>Effective from <span className="text-rose-500">*</span>
            <input type="date" required value={form.effectiveFrom} onChange={(e) => set('effectiveFrom', e.target.value)} className={INPUT} />
          </label>
          <label className={LABEL}>Effective to
            <input type="date" value={form.effectiveTo} onChange={(e) => set('effectiveTo', e.target.value)} className={INPUT} />
          </label>
        </div>
        {editing && <p className="text-[11px] text-slate-500 dark:text-slate-400">Code and company scope identify the plan to existing enrolments and cannot be changed.</p>}
        <label className="flex items-center gap-2 text-xs font-medium text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={form.requiresEnrollment} onChange={(e) => set('requiresEnrollment', e.target.checked)} /> Requires enrolment
        </label>
        <label className="flex items-center gap-2 text-xs font-medium text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={form.isActive} onChange={(e) => set('isActive', e.target.checked)} /> Active (open for enrolment)
        </label>
        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
          <button type="submit" disabled={saving} className={PRIMARY}>{saving ? 'Saving…' : editing ? 'Save changes' : 'Create plan'}</button>
        </div>
      </form>
    </Modal>
  );
}

// ── enrolment with eligibility preview ──────────────────────────────────────────

function EnrollModal({ plans, initialPlanId, onClose, onEnrolled }: {
  plans: BenefitPlan[]; initialPlanId?: string; onClose: () => void; onEnrolled: () => void;
}) {
  const toast = useAppToast();
  const [planId, setPlanId] = useState(initialPlanId ?? plans[0]?.id ?? '');
  const plan = plans.find((p) => p.id === planId) ?? null;
  const [search, setSearch] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [employee, setEmployee] = useState<EmployeeListItem | null>(null);
  const [tier, setTier] = useState('Employee');
  const [from, setFrom] = useState(() => {
    const t = today();
    return plan && t < plan.effectiveFrom ? plan.effectiveFrom : t;
  });
  const [to, setTo] = useState('');
  const [check, setCheck] = useState<BenefitEligibilityCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Employee search (debounced).
  useEffect(() => {
    if (employee) return;
    const q = search.trim();
    if (q.length < 2) { setResults([]); return; }
    const t = setTimeout(() => {
      setSearching(true);
      employeesApi.list({ search: q, status: 'Active', pageSize: 8 })
        .then((r) => setResults(r.items ?? []))
        .catch(() => setResults([]))
        .finally(() => setSearching(false));
    }, 250);
    return () => clearTimeout(t);
  }, [search, employee]);

  // Eligibility preview: the server runs the exact checks the enrol endpoint runs.
  useEffect(() => {
    setCheck(null); setCheckError(null);
    if (!planId || !employee || !from) return;
    let cancelled = false;
    setChecking(true);
    const t = setTimeout(() => {
      benefitsApi.checkEligibility(planId, employee.id, from)
        .then((c) => { if (!cancelled) setCheck(c); })
        .catch((e) => { if (!cancelled) setCheckError(benefitsErrorMessage(e, 'Could not check eligibility.')); })
        .finally(() => { if (!cancelled) setChecking(false); });
    }, 200);
    return () => { cancelled = true; clearTimeout(t); };
  }, [planId, employee, from]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!employee || !check?.eligible) return;
    setSaving(true); setError(null);
    try {
      await benefitsApi.enroll({ benefitPlanId: planId, employeeId: employee.id, coverageTier: tier, effectiveFrom: from, effectiveTo: to || null });
      toast.success(`${employee.fullName} enrolled in ${plan?.name ?? 'the plan'}.`);
      onEnrolled();
    } catch (err) { setError(benefitsErrorMessage(err, 'Could not enrol the employee.')); }
    finally { setSaving(false); }
  };

  return (
    <Modal title="Enrol employee" onClose={onClose} wide>
      <form onSubmit={submit} className="space-y-3">
        <FormError message={error} />
        <label className={LABEL}>Plan <span className="text-rose-500">*</span>
          <select className={INPUT} value={planId} onChange={(e) => setPlanId(e.target.value)} aria-label="Plan">
            {plans.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.code})</option>)}
          </select>
        </label>

        <div>
          <span className={LABEL}>Employee <span className="text-rose-500">*</span></span>
          {employee ? (
            <div className="mt-1 flex items-center justify-between rounded-lg border border-slate-200 px-3 py-2 text-sm dark:border-white/[0.08]">
              <span className="text-slate-800 dark:text-slate-100"><span className="font-semibold">{employee.fullName}</span> <span className="font-mono text-xs text-slate-400">{employee.employeeCode}</span></span>
              <button type="button" onClick={() => { setEmployee(null); setSearch(''); }} className="text-xs font-semibold text-sapphire hover:underline">Change</button>
            </div>
          ) : (
            <div className="relative mt-1">
              <Search className="pointer-events-none absolute start-3 top-2.5 h-4 w-4 text-slate-400" />
              <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search by name or employee code"
                aria-label="Search employee" className={`${INPUT} mt-0 ps-9`} />
              {(results.length > 0 || searching) && (
                <ul className="absolute z-10 mt-1 max-h-56 w-full overflow-y-auto rounded-lg border border-slate-200 bg-white shadow-lg dark:border-white/[0.08] dark:bg-[#0c1120]" role="listbox">
                  {searching && <li className="px-3 py-2 text-xs text-slate-400">Searching…</li>}
                  {results.map((r) => (
                    <li key={r.id}>
                      <button type="button" role="option" aria-selected={false} onClick={() => { setEmployee(r); setResults([]); }}
                        className="flex w-full items-center justify-between px-3 py-2 text-start text-sm hover:bg-slate-50 dark:hover:bg-white/[0.04]">
                        <span className="text-slate-800 dark:text-slate-100">{r.fullName}</span>
                        <span className="font-mono text-xs text-slate-400">{r.employeeCode} · {r.department}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>

        <div className="grid grid-cols-3 gap-3">
          <label className={LABEL}>Coverage tier
            <select className={INPUT} value={tier} onChange={(e) => setTier(e.target.value)}>
              {COVERAGE_TIERS.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
          </label>
          <label className={LABEL}>Start date <span className="text-rose-500">*</span>
            <input type="date" required className={INPUT} value={from} onChange={(e) => setFrom(e.target.value)} aria-label="Start date" />
          </label>
          <label className={LABEL}>End date
            <input type="date" className={INPUT} value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
        </div>

        <EligibilityPanel employeeChosen={!!employee} checking={checking} check={check} error={checkError} />

        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
          <button type="submit" disabled={saving || !check?.eligible || checking} className={PRIMARY}>
            {saving ? 'Enrolling…' : 'Enrol'}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function EligibilityPanel({ employeeChosen, checking, check, error }: {
  employeeChosen: boolean; checking: boolean; check: BenefitEligibilityCheck | null; error: string | null;
}) {
  if (!employeeChosen) {
    return <p className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">Choose an employee to check eligibility before enrolling.</p>;
  }
  if (checking && !check) {
    return <p className="flex items-center gap-2 rounded-xl border border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400"><Loader2 className="h-3.5 w-3.5 animate-spin" /> Checking eligibility…</p>;
  }
  if (error) return <FormError message={error} />;
  if (!check) return null;
  return (
    <div data-testid="eligibility-result" data-eligible={check.eligible ? 'true' : 'false'}
      className={`rounded-xl border p-3 ${check.eligible
        ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-500/20 dark:bg-emerald-500/[0.06]'
        : 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/[0.06]'}`}>
      <p className={`flex items-center gap-1.5 text-sm font-bold ${check.eligible ? 'text-emerald-800 dark:text-emerald-300' : 'text-rose-800 dark:text-rose-300'}`}>
        {check.eligible ? <ShieldCheck className="h-4 w-4" /> : <XCircle className="h-4 w-4" />}
        {check.eligible ? 'Eligible for this plan' : 'Not eligible for this plan'}
      </p>
      <p className="mt-0.5 text-[11px] text-slate-600 dark:text-slate-300">
        {check.employeeName} · {check.companyName ?? 'No company'} · {check.gradeName ?? 'No grade'} · from {check.effectiveFrom}
      </p>
      <ul className="mt-2 space-y-1">
        {check.checks.map((c) => (
          <li key={c.key} className="flex items-start gap-1.5 text-xs">
            {c.passed ? <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600 dark:text-emerald-400" /> : <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-rose-600 dark:text-rose-400" />}
            <span><span className="font-semibold text-slate-800 dark:text-slate-100">{c.label}.</span> <span className="text-slate-600 dark:text-slate-300">{c.detail}</span></span>
          </li>
        ))}
      </ul>
      {check.alreadyEnrolled && (
        <p className="mt-2 flex items-center gap-1.5 text-xs font-medium text-amber-800 dark:text-amber-300">
          <AlertTriangle className="h-3.5 w-3.5" /> This employee already has an active enrolment in this plan on that date.
        </p>
      )}
    </div>
  );
}

// ── enrolments ──────────────────────────────────────────────────────────────────

function EnrollmentList({ enrollments, plans, companies, companyName, canEnroll, onEnroll, onOpen }: {
  enrollments: BenefitEnrollment[]; plans: BenefitPlan[]; companies: { id: string; name: string }[];
  companyName: (id: string | null) => string; canEnroll: boolean; onEnroll: () => void; onOpen: (id: string) => void;
}) {
  const [planId, setPlanId] = useState('');
  const [status, setStatus] = useState('');
  const [companyId, setCompanyId] = useState('');
  const [q, setQ] = useState('');
  const planName = (id: string) => plans.find((p) => p.id === id)?.name ?? 'Plan';
  const statuses = [...new Set(enrollments.map((e) => e.status))].sort();

  const rows = enrollments.filter((e) =>
    (!planId || e.benefitPlanId === planId) &&
    (!status || e.status === status) &&
    (!companyId || e.companyId === companyId) &&
    (!q.trim() || e.employeeName.toLowerCase().includes(q.trim().toLowerCase())));

  if (enrollments.length === 0) {
    return (
      <div data-testid="enrollments-empty" className={`${CARD} flex flex-col items-center gap-3 p-10 text-center`}>
        <UserPlus className="h-10 w-10 text-slate-300 dark:text-slate-600" />
        <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">No one is enrolled yet</p>
        <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">Enrol an employee in a plan. Their eligibility is checked before you submit.</p>
        {canEnroll && <button type="button" className={PRIMARY} onClick={onEnroll}><UserPlus className="h-3.5 w-3.5" /> Enrol employee</button>}
      </div>
    );
  }

  const sel = 'rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 dark:border-white/[0.08] dark:bg-[#0c1120] dark:text-slate-200';
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative">
          <Search className="pointer-events-none absolute start-2.5 top-2 h-3.5 w-3.5 text-slate-400" />
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Employee name" aria-label="Filter by employee"
            className="rounded-lg border border-slate-200 bg-white py-1.5 ps-8 pe-3 text-xs text-slate-700 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-200" />
        </div>
        <select className={sel} value={planId} onChange={(e) => setPlanId(e.target.value)} aria-label="Filter by plan">
          <option value="">All plans</option>
          {plans.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <select className={sel} value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Filter by status">
          <option value="">All statuses</option>
          {statuses.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
        {companies.length > 1 && (
          <select className={sel} value={companyId} onChange={(e) => setCompanyId(e.target.value)} aria-label="Filter by company">
            <option value="">All companies</option>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        )}
        <span className="text-xs text-slate-500 dark:text-slate-400">{rows.length} of {enrollments.length}</span>
      </div>
      <div className={`${CARD} overflow-x-auto`}>
        <table className="w-full min-w-[720px] text-start text-sm" data-testid="enrollments-table">
          <thead>
            <tr className="border-b border-slate-100 text-[11px] uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
              <th className="px-4 py-2.5 font-semibold">Employee</th>
              <th className="px-4 py-2.5 font-semibold">Plan</th>
              <th className="px-4 py-2.5 font-semibold">Coverage</th>
              <th className="px-4 py-2.5 font-semibold">Company</th>
              <th className="px-4 py-2.5 font-semibold">Effective</th>
              <th className="px-4 py-2.5 font-semibold">Status</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr><td colSpan={6} className="px-4 py-6 text-center text-xs text-slate-500 dark:text-slate-400">No enrolments match these filters.</td></tr>
            ) : rows.map((e) => (
              <tr key={e.id} onClick={() => onOpen(e.id)} className="cursor-pointer border-b border-slate-50 last:border-0 hover:bg-slate-50 dark:border-white/[0.03] dark:hover:bg-white/[0.02]">
                <td className="px-4 py-2.5 font-medium text-slate-800 dark:text-slate-100">{e.employeeName}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{planName(e.benefitPlanId)}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{e.coverageTier}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{companyName(e.companyId)}</td>
                <td className="px-4 py-2.5 text-slate-500 dark:text-slate-400">{range(e.effectiveFrom, e.effectiveTo)}</td>
                <td className="px-4 py-2.5"><StatusPill active={e.status === 'Active'} label={e.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function EnrollmentDrawer({ enrollmentId, plans, canRecord, onClose }: {
  enrollmentId: string; plans: BenefitPlan[]; canRecord: boolean; onClose: () => void;
}) {
  const toast = useAppToast();
  const [detail, setDetail] = useState<BenefitEnrollmentDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [candidates, setCandidates] = useState<BenefitDeductionCandidate[] | null>(null);
  const [contrib, setContrib] = useState({ employeeAmount: '', employerAmount: '', frequency: 'Monthly', payrollComponentCode: '', effectiveFrom: today() });
  const [link, setLink] = useState({ contributionId: '', deductionId: '', amount: '' });
  const [busy, setBusy] = useState<'contrib' | 'link' | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [d, c] = await Promise.all([benefitsApi.getEnrollment(enrollmentId), benefitsApi.deductionCandidates(enrollmentId)]);
      setDetail(d); setCandidates(c);
      setLink((l) => ({ ...l, contributionId: l.contributionId || d.contributions[0]?.id || '' }));
    } catch (e) { setError(benefitsErrorMessage(e, 'Could not load the enrolment.')); }
  }, [enrollmentId]);
  useEffect(() => { void load(); }, [load]);

  const plan = detail ? plans.find((p) => p.id === detail.enrollment.benefitPlanId) : null;
  const currency = plan?.currency ?? '';

  const addContribution = async (e: React.FormEvent) => {
    e.preventDefault();
    const ee = Number(contrib.employeeAmount || 0), er = Number(contrib.employerAmount || 0);
    if (ee < 0 || er < 0) { setFormError('Contribution amounts cannot be negative.'); return; }
    setBusy('contrib'); setFormError(null);
    try {
      await benefitsApi.addContribution(enrollmentId, {
        employeeAmount: ee, employerAmount: er, frequency: contrib.frequency, payrollComponentCode: contrib.payrollComponentCode || null,
        effectiveFrom: contrib.effectiveFrom, effectiveTo: null, isActive: true,
      });
      toast.success('Contribution recorded.');
      setContrib((c) => ({ ...c, employeeAmount: '', employerAmount: '' }));
      void load();
    } catch (err) { setFormError(benefitsErrorMessage(err, 'Could not record the contribution.')); }
    finally { setBusy(null); }
  };

  const linkDeduction = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!link.contributionId || !link.deductionId) return;
    setBusy('link'); setFormError(null);
    try {
      await benefitsApi.linkDeduction(enrollmentId, {
        benefitContributionId: link.contributionId, payrollDeductionId: link.deductionId,
        linkedAmount: link.amount === '' ? null : Number(link.amount),
      });
      toast.success('Payroll deduction linked.');
      setLink((l) => ({ ...l, deductionId: '', amount: '' }));
      void load();
    } catch (err) { setFormError(benefitsErrorMessage(err, 'Could not link the deduction.')); }
    finally { setBusy(null); }
  };

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/40" role="dialog" aria-modal="true" aria-label="Enrolment detail">
      <div className="h-full w-full max-w-xl overflow-y-auto border-s border-slate-200 bg-white p-5 shadow-2xl dark:border-white/[0.08] dark:bg-[#0c1120]">
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">Enrolment</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg p-1 text-slate-400 hover:bg-slate-100 dark:hover:bg-white/[0.06]"><X className="h-4 w-4" /></button>
        </div>
        {error ? (
          <div className="space-y-2"><FormError message={error} /><button type="button" className={SECONDARY} onClick={() => void load()}><RefreshCw className="h-3.5 w-3.5" /> Retry</button></div>
        ) : !detail ? (
          <div className="space-y-3" aria-busy="true"><div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" /><div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" /></div>
        ) : (
          <div className="space-y-5">
            <div>
              <p className="text-base font-bold text-slate-800 dark:text-slate-100">{detail.enrollment.employeeName}</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">{plan?.name ?? 'Plan'} · {detail.enrollment.coverageTier} · {range(detail.enrollment.effectiveFrom, detail.enrollment.effectiveTo)}</p>
            </div>
            <FormError message={formError} />

            <section>
              <h3 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">Contributions</h3>
              {detail.contributions.length === 0 ? (
                <p className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">No contributions recorded. Record the employee and employer share below.</p>
              ) : (
                <table className="w-full text-start text-xs" data-testid="contributions-table">
                  <thead><tr className="text-[10px] uppercase tracking-wide text-slate-500 dark:text-slate-400">
                    <th className="py-1 font-semibold">From</th><th className="py-1 text-end font-semibold">Employee</th><th className="py-1 text-end font-semibold">Employer</th><th className="py-1 font-semibold">Frequency</th><th className="py-1 font-semibold">Pay code</th>
                  </tr></thead>
                  <tbody>{detail.contributions.map((c) => (
                    <tr key={c.id} className="border-t border-slate-100 dark:border-white/[0.04]">
                      <td className="py-1.5 text-slate-600 dark:text-slate-300">{c.effectiveFrom}</td>
                      <td className="py-1.5 text-end font-mono text-slate-800 dark:text-slate-100">{money(c.employeeAmount)} {currency}</td>
                      <td className="py-1.5 text-end font-mono text-slate-800 dark:text-slate-100">{money(c.employerAmount)} {currency}</td>
                      <td className="py-1.5 text-slate-600 dark:text-slate-300">{c.frequency}</td>
                      <td className="py-1.5 font-mono text-slate-500 dark:text-slate-400">{c.payrollComponentCode || '—'}</td>
                    </tr>))}
                  </tbody>
                </table>
              )}
              {canRecord && (
                <form onSubmit={addContribution} aria-label="Record contribution" className="mt-3 grid grid-cols-2 gap-2 rounded-xl border border-slate-200 p-3 dark:border-white/[0.06]">
                  <label className={LABEL}>Employee share
                    <input type="number" min={0} step="0.01" required className={INPUT} value={contrib.employeeAmount} onChange={(e) => setContrib((c) => ({ ...c, employeeAmount: e.target.value }))} aria-label="Employee share" />
                  </label>
                  <label className={LABEL}>Employer share
                    <input type="number" min={0} step="0.01" required className={INPUT} value={contrib.employerAmount} onChange={(e) => setContrib((c) => ({ ...c, employerAmount: e.target.value }))} aria-label="Employer share" />
                  </label>
                  <label className={LABEL}>Frequency
                    <select className={INPUT} value={contrib.frequency} onChange={(e) => setContrib((c) => ({ ...c, frequency: e.target.value }))}>
                      {['Monthly', 'Quarterly', 'Annual', 'One-off'].map((f) => <option key={f} value={f}>{f}</option>)}
                    </select>
                  </label>
                  <label className={LABEL}>Payroll component code
                    <input className={INPUT} value={contrib.payrollComponentCode} placeholder="MED-EE" onChange={(e) => setContrib((c) => ({ ...c, payrollComponentCode: e.target.value }))} />
                  </label>
                  <label className={LABEL}>Effective from
                    <input type="date" required className={INPUT} value={contrib.effectiveFrom} onChange={(e) => setContrib((c) => ({ ...c, effectiveFrom: e.target.value }))} />
                  </label>
                  <div className="flex items-end justify-end"><button type="submit" disabled={busy !== null} className={PRIMARY}>{busy === 'contrib' ? 'Saving…' : 'Record contribution'}</button></div>
                </form>
              )}
            </section>

            <section>
              <h3 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">Payroll deduction links</h3>
              {detail.links.length === 0 ? (
                <p className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">No payroll deductions linked yet.</p>
              ) : (
                <ul className="space-y-1.5" data-testid="links-list">
                  {detail.links.map((l) => (
                    <li key={l.id} className="flex items-center justify-between rounded-xl border border-slate-200 px-3 py-2 text-xs dark:border-white/[0.06]">
                      <span className="flex items-center gap-1.5 text-slate-600 dark:text-slate-300"><Link2 className="h-3.5 w-3.5" /> Run {l.payrollRunId.slice(0, 8)}</span>
                      <span className="font-mono font-semibold text-slate-800 dark:text-slate-100">{money(l.linkedAmount)} {currency}</span>
                    </li>
                  ))}
                </ul>
              )}
              {canRecord && (
                detail.contributions.length === 0 ? (
                  <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">Record a contribution first; a deduction is linked to a contribution.</p>
                ) : candidates && candidates.length === 0 ? (
                  <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">This employee has no unlinked, non-statutory payroll deductions to link. Deductions appear here once a payroll run includes one.</p>
                ) : (
                  <form onSubmit={linkDeduction} aria-label="Link payroll deduction" className="mt-3 grid grid-cols-2 gap-2 rounded-xl border border-slate-200 p-3 dark:border-white/[0.06]">
                    <label className={LABEL}>Contribution
                      <select className={INPUT} value={link.contributionId} onChange={(e) => setLink((l) => ({ ...l, contributionId: e.target.value }))}>
                        {detail.contributions.map((c) => <option key={c.id} value={c.id}>From {c.effectiveFrom} · EE {money(c.employeeAmount)}</option>)}
                      </select>
                    </label>
                    <label className={LABEL}>Payroll deduction
                      <select className={INPUT} required value={link.deductionId} onChange={(e) => setLink((l) => ({ ...l, deductionId: e.target.value }))}>
                        <option value="">Select…</option>
                        {(candidates ?? []).map((d) => <option key={d.id} value={d.id}>{d.year}-{String(d.month).padStart(2, '0')} · {d.componentName || d.componentCode} · {money(d.amount)}</option>)}
                      </select>
                    </label>
                    <label className={LABEL}>Linked amount (optional)
                      <input type="number" min={0} step="0.01" className={INPUT} placeholder="Defaults to deduction amount" value={link.amount} onChange={(e) => setLink((l) => ({ ...l, amount: e.target.value }))} />
                    </label>
                    <div className="flex items-end justify-end"><button type="submit" disabled={busy !== null || !link.deductionId} className={PRIMARY}>{busy === 'link' ? 'Linking…' : 'Link deduction'}</button></div>
                  </form>
                )
              )}
            </section>
          </div>
        )}
      </div>
    </div>
  );
}
