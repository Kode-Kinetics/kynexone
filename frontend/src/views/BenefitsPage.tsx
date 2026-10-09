'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle, CheckCircle2, CircleSlash, HeartPulse, Loader2, Pencil, Plus, RefreshCw, Search, ShieldCheck, UserPlus, XCircle,
} from 'lucide-react';
import {
  benefitsApi, benefitsErrorMessage,
  type BenefitEligibilityCheck, type BenefitEligibilityRule,
  type BenefitEnrollment, type BenefitPlan,
} from '@/src/api/benefits';
import { employeesApi, type EmployeeListItem } from '@/src/api/employees';
import { gradesApi, type GradeDto } from '@/src/api/organization';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useReleaseA } from '@/src/lib/releaseA';
import Link from 'next/link';
import type { BenefitEmployee } from '@/src/components/benefits/AdditionalBenefitForm';
import { BenefitChecklist } from '@/src/components/benefits/BenefitChecklist';
import { AdditionalBenefitRequestDetail } from '@/src/components/benefits/AdditionalBenefitRequestDetail';
import { AdditionalBenefitApprovalSetup } from '@/src/components/benefits/AdditionalBenefitApprovalSetup';
import { BenefitPaymentPolicyEditor, BenefitPolicySummary, DEFAULT_PAYMENT_POLICY } from '@/src/components/benefits/BenefitPaymentPolicy';
import { AssignmentLabel, EnrollmentDrawer } from '@/src/components/benefits/BenefitEnrollmentDrawer';
import { INPUT, LABEL, PRIMARY, SECONDARY, CARD, COVERAGE_TIERS, today, enrollmentStatus, StatusPill, Modal, FormError } from '@/src/components/benefits/benefitUi';

// ── shared styling ──────────────────────────────────────────────────────────────

const PLAN_TYPES = ['Medical', 'Dental', 'Life', 'Vision', 'Pension', 'Education', 'Housing', 'Transport', 'Allowance', 'Reimbursement', 'Custom'];
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const range = (from: string, to: string | null) => `${from} → ${to ?? 'open-ended'}`;

// ── page ────────────────────────────────────────────────────────────────────────

type Tab = 'plans' | 'enrollments';

export function BenefitsPage() {
  const { t } = useLocale();
  const { hasRole, hasPermission } = useAuth();
  const { companies, companyVersion } = useCompany();
  const canManagePlans = hasRole('Admin') || hasRole('HR Manager');
  const canEnroll = canManagePlans || hasRole('HR Officer');
  const canRecordMoney = hasPermission('employees.approve');
  const releaseA = useReleaseA();
  const canApplyException = canRecordMoney && !releaseA;
  const canProposeAdditional = hasPermission('employees.write') && !releaseA;

  const [tab, setTab] = useState<Tab>('plans');
  const [planSearch, setPlanSearch] = useState('');
  const [plans, setPlans] = useState<BenefitPlan[]>([]);
  const [enrollments, setEnrollments] = useState<BenefitEnrollment[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [selectedPlanId, setSelectedPlanId] = useState<string | null>(null);
  const [planModal, setPlanModal] = useState<{ mode: 'create' } | { mode: 'edit'; plan: BenefitPlan } | null>(null);
  const [enrollFor, setEnrollFor] = useState<{ planId?: string } | null>(null);
  const [additionalFor, setAdditionalFor] = useState<{ employee?: BenefitEmployee; planId?: string } | null>(null);
  const [additionalRequestId, setAdditionalRequestId] = useState<string | null>(null);
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
    gradesApi.listAll().then(setGrades).catch(() => setGrades([]));
  }, []);

  const companyById = useMemo(() => new Map(companies.map((company) => [company.id, company.name])), [companies]);
  const gradeById = useMemo(() => new Map(grades.map((grade) => [grade.id, grade.name])), [grades]);
  const companyName = useCallback((id: string | null) =>
    id === null ? 'All companies' : (companyById.get(id) ?? 'Company'), [companyById]);
  const gradeName = useCallback((id: string | null) =>
    id === null ? 'Any grade' : (gradeById.get(id) ?? 'Grade'), [gradeById]);

  const selectedPlan = plans.find((p) => p.id === selectedPlanId) ?? null;
  const enrolledCount = useMemo(() => {
    const employeesByPlan = new Map<string, Set<number>>();
    for (const enrollment of enrollments) {
      if (!['Active', 'Scheduled'].includes(enrollmentStatus(enrollment))) continue;
      const employees = employeesByPlan.get(enrollment.benefitPlanId) ?? new Set<number>();
      employees.add(enrollment.employeeId);
      employeesByPlan.set(enrollment.benefitPlanId, employees);
    }
    return new Map([...employeesByPlan].map(([planId, employees]) => [planId, employees.size]));
  }, [enrollments]);
  const activePlans = plans.filter((p) => p.isActive);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Benefits Administration</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">{releaseA ? 'Plans, employee enrolments and contributions' : 'Grade benefits, employee enrolments, exceptions and contributions'}</p>
        </div>
        {!loading && !error && (plans.length > 0 || enrollments.length > 0) && (
          <div className="flex flex-wrap gap-2">
            {canProposeAdditional && <button type="button" className={SECONDARY} onClick={() => setAdditionalFor({})}><Plus className="h-3.5 w-3.5" />{t('Manage benefits')}</button>}
            {canEnroll && (
              <button type="button" className={SECONDARY} disabled={activePlans.length === 0} onClick={() => setEnrollFor({})}>
                <UserPlus className="h-3.5 w-3.5" /> Enrol employee
              </button>
            )}
            {canManagePlans && (
              <button type="button" className={PRIMARY} onClick={() => setPlanModal({ mode: 'create' })}>
                <Plus className="h-3.5 w-3.5" /> {t('Add benefit plan')}
              </button>
            )}
          </div>
        )}
      </div>

      {!releaseA && <div className="flex flex-wrap gap-2"><AdditionalBenefitApprovalSetup /><AdditionalBenefitApprovalSetup entityName="BenefitClaim" /></div>}

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
              <div className="space-y-3">
                <label className={LABEL}>{t('Find a benefit plan')}<input type="search" className={INPUT} value={planSearch} onChange={event => setPlanSearch(event.target.value)} placeholder={t('Search by name, code or category')} /></label>
                <PlanList plans={plans.filter(plan => !planSearch.trim() || [plan.name, plan.code, plan.planType].some(value => value.toLocaleLowerCase().includes(planSearch.trim().toLocaleLowerCase())))} selectedId={selectedPlanId} onSelect={setSelectedPlanId}
                  companyName={companyName} enrolledCount={enrolledCount} />
                {planSearch.trim() && !plans.some(plan => [plan.name, plan.code, plan.planType].some(value => value.toLocaleLowerCase().includes(planSearch.trim().toLocaleLowerCase()))) && <p role="status" className="p-3 text-sm text-slate-500">{t('No benefit plans match your search.')}</p>}
              </div>
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
          canApplyException={canProposeAdditional}
          onAdditional={(employee, planId) => { setEnrollFor(null); setAdditionalFor({ employee, planId }); }}
          plans={activePlans} initialPlanId={enrollFor.planId}
          onClose={() => setEnrollFor(null)}
          onEnrolled={() => { setEnrollFor(null); setTab('enrollments'); void load(); }}
        />
      )}
      {additionalFor && <BenefitChecklist plans={activePlans} employee={additionalFor.employee} initialPlanId={additionalFor.planId} onClose={() => setAdditionalFor(null)} onChanged={() => void load()} />}
      {additionalRequestId && <AdditionalBenefitRequestDetail requestId={additionalRequestId} onClose={() => setAdditionalRequestId(null)} />}
      {openEnrollmentId && (
        <EnrollmentDrawer key={openEnrollmentId} enrollmentId={openEnrollmentId} plans={plans} canRecord={canRecordMoney} canApplyException={canApplyException} onChanged={(id) => { setOpenEnrollmentId(id); void load(); }}
          onClose={() => setOpenEnrollmentId(null)} />
      )}
    </div>
  );
}

// ── empty state: guided first plan ──────────────────────────────────────────────

function FirstPlanSetup({ canManage, onCreate }: { canManage: boolean; onCreate: () => void }) {
  const releaseA = useReleaseA();
  const steps = [
    { n: 1, title: 'Create a plan', body: 'Medical, dental, life or any other benefit, with its currency and effective dates.' },
    { n: 2, title: 'Set grade eligibility', body: 'Choose one or more grades. A plan cannot enrol employees until an effective grade rule exists.' },
    { n: 3, title: 'Assign by grade', body: releaseA ? 'Configure employee package defaults in Benefits by grade.' : 'New employees receive eligible benefits for their grade. Authorized HR can record individual exceptions later.' },
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
          <p className="text-xs text-slate-500 dark:text-slate-400">Ask your client Admin to create the first plan and configure its grade entitlements.</p>
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
              <span className="font-mono">{p.code}</span> · {p.planType} · {p.classification} · {p.currency} · {companyName(p.companyId)}
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
  const { t } = useLocale();
  // Release A (R1): which grade gets a benefit is set in Benefits by grade. For a release_a tenant the eligibility rules
  // are read-only here (the API refuses writes with 409); existing rules stay listed and are shown in the matrix import.
  const rulesFrozen = useReleaseA();
  const canEditRules = canManage && !rulesFrozen;
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
          <p className="text-xs text-slate-500 dark:text-slate-400"><span className="font-mono">{plan.code}</span> · {plan.planType} · {plan.classification} · {plan.currency}</p>
          <BenefitPolicySummary policy={plan.paymentPolicy} currency={plan.currency} />
        </div>
        <div className="flex gap-2">
          {canManage && <button type="button" className={SECONDARY} onClick={onEdit}><Pencil className="h-3.5 w-3.5" /> Edit plan</button>}
          {canEnroll && plan.isActive && <button type="button" className={PRIMARY} onClick={onEnroll}><UserPlus className="h-3.5 w-3.5" /> Enrol in this plan</button>}
        </div>
      </div>
      <dl className="grid grid-cols-2 gap-3 text-xs">
        <div><dt className="text-slate-500 dark:text-slate-400">Company scope</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{companyName(plan.companyId)}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Effective</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{range(plan.effectiveFrom, plan.effectiveTo)}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Enrolment</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{rulesFrozen ? 'Employee package' : 'Automatic by grade on employee creation'}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Policy class</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{plan.classification}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">Status</dt><dd><StatusPill active={plan.isActive} /></dd></div>
      </dl>

      <section>
        <div className="mb-2 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">Grade benefits</h3>
          {canEditRules && !adding && (
            <button type="button" className={SECONDARY} onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> Add rule</button>
          )}
        </div>
        <p className="mb-2 text-[11px] text-slate-500 dark:text-slate-400">
          {plan.classification === 'Mandatory'
            ? 'Mandatory minimum coverage cannot be denied by grade. Grade rules may still define enhanced tiers above that floor.'
            : rulesFrozen ? 'Configure grade defaults in Benefits by grade. Existing rules remain available for reference.' : 'New employees automatically receive the benefits configured for their grade, subject to the effective dates and eligibility conditions below.'}
        </p>
        {rulesFrozen && (
          <p data-testid="rules-moved" className="mb-2 rounded-xl border border-sapphire/30 bg-sapphire/[0.04] px-3 py-2 text-[11px] text-slate-600 dark:text-slate-300">
            {t('Which grades get a benefit is now set in Benefits by grade. These rules are kept for reference.')}{' '}
            <Link href="/benefits/by-grade" className="font-semibold text-sapphire underline">{t('Open Benefits by grade')}</Link>
          </p>
        )}
        {adding && canEditRules && (
          <AddRuleForm planId={plan.id} planFrom={plan.effectiveFrom} companies={companies} grades={grades}
            onCancel={() => setAdding(false)} onAdded={() => { setAdding(false); void loadRules(); }} />
        )}
        {rules === null ? (
          <div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />
        ) : rulesError ? (
          <FormError message={rulesError} />
        ) : rules.length === 0 ? (
          <div data-testid="rules-empty" className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">
            {plan.classification === 'Mandatory'
              ? 'No grade enhancement rules. Employees remain eligible for the mandatory minimum.'
              : 'No grade eligibility rules. Add at least one grade before employees can be enrolled in this plan.'}
          </div>
        ) : (
          <ul className="space-y-1.5" data-testid="rules-list">
            {rules.map((r) => (
              <li key={r.id} className={`flex items-center justify-between gap-2 rounded-xl border px-3 py-2 text-xs ${r.isActive
                ? 'border-slate-200 dark:border-white/[0.06]' : 'border-dashed border-slate-200 opacity-60 dark:border-white/[0.06]'}`}>
                <div>
                  <p className="font-semibold text-slate-800 dark:text-slate-100">{r.tierName || gradeName(r.gradeId)}</p>
                  <p className="text-slate-500 dark:text-slate-400">{companyName(r.companyId)} · {gradeName(r.gradeId)}{r.gradeMatchMode === 'LevelAndAbove' ? ' and above' : ''} · {range(r.effectiveFrom, r.effectiveTo)}</p>
                  <p className="text-slate-500 dark:text-slate-400">{r.maxBenefitAmount === null ? 'No monetary cap' : `Up to ${money(r.maxBenefitAmount)} ${plan.currency}`} · {r.limitPeriod.replace(/([A-Z])/g, ' $1').trim()}</p>
                  {(r.minimumServiceMonths > 0 || r.requireProbationCompleted) && <p className="text-slate-500 dark:text-slate-400">{r.minimumServiceMonths > 0 ? `${r.minimumServiceMonths} months service` : ''}{r.minimumServiceMonths > 0 && r.requireProbationCompleted ? ' · ' : ''}{r.requireProbationCompleted ? 'Probation completed' : ''}</p>}
                  {r.customCriteriaNote && <p className="mt-1 text-slate-500 dark:text-slate-400">{r.customCriteriaNote}</p>}
                </div>
                <div className="flex items-center gap-2">
                  <StatusPill active={r.isActive} />
                  {canEditRules && r.isActive && (
                    <button type="button" onClick={() => void deactivate(r)} className="rounded-lg p-1 text-rose-500 hover:bg-rose-50 hover:text-rose-700 dark:text-rose-400 dark:hover:bg-rose-500/[0.08] dark:hover:text-rose-300" aria-label="Deactivate rule">
                      <CircleSlash className="h-3.5 w-3.5" />
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
        {activeRules.length > 0 && (
          <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">{activeRules.length} active grade assignment(s). {!rulesFrozen && 'Authorized HR can apply individual exceptions after enrolment.'}</p>
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
  const [gradeMatchMode, setGradeMatchMode] = useState<'Exact' | 'LevelAndAbove'>('Exact');
  const [tierName, setTierName] = useState('');
  const [maxBenefitAmount, setMaxBenefitAmount] = useState('');
  const [limitPeriod, setLimitPeriod] = useState<'PerEnrollment' | 'Monthly' | 'Annual' | 'Lifetime'>('PerEnrollment');
  const [minimumServiceMonths, setMinimumServiceMonths] = useState('0');
  const [requireProbationCompleted, setRequireProbationCompleted] = useState(false);
  const [customCriteriaNote, setCustomCriteriaNote] = useState('');
  const [from, setFrom] = useState(planFrom);
  const [to, setTo] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!gradeId) { setError('Select a grade. Benefit eligibility must be grade-based.'); return; }
    if (maxBenefitAmount !== '' && Number(maxBenefitAmount) <= 0) { setError('Maximum amount must be greater than zero.'); return; }
    setSaving(true); setError(null);
    try {
      await benefitsApi.addRule(planId, {
        companyId: companyId || null,
        gradeId,
        gradeMatchMode,
        tierName: tierName.trim() || null,
        maxBenefitAmount: maxBenefitAmount === '' ? null : Number(maxBenefitAmount),
        limitPeriod,
        minimumServiceMonths: Number(minimumServiceMonths) || 0,
        requireProbationCompleted,
        customCriteriaNote: customCriteriaNote.trim() || null,
        effectiveFrom: from,
        effectiveTo: to || null,
        isActive: true,
      });
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
          <select required className={INPUT} value={gradeId} onChange={(e) => setGradeId(e.target.value)} aria-label="Rule grade">
            <option value="">Select grade…</option>
            {[...grades].sort((a, b) => a.level - b.level).map((g) => <option key={g.id} value={g.id}>{g.name} ({g.code}) · level {g.level}</option>)}
          </select>
        </label>
        <label className={LABEL}>Grade coverage
          <select className={INPUT} value={gradeMatchMode} onChange={(e) => setGradeMatchMode(e.target.value as 'Exact' | 'LevelAndAbove')}>
            <option value="Exact">Selected grade only</option>
            <option value="LevelAndAbove">Selected grade and above</option>
          </select>
        </label>
        <label className={LABEL}>Tier name
          <input className={INPUT} maxLength={120} value={tierName} onChange={(e) => setTierName(e.target.value)} placeholder="Vehicle Starter" />
        </label>
        <label className={LABEL}>Maximum amount (optional)
          <input type="number" min="0.01" step="0.01" className={INPUT} value={maxBenefitAmount} onChange={(e) => setMaxBenefitAmount(e.target.value)} placeholder="500.00" />
        </label>
        <label className={LABEL}>Limit period
          <select className={INPUT} value={limitPeriod} onChange={(e) => setLimitPeriod(e.target.value as typeof limitPeriod)}>
            <option value="PerEnrollment">Per enrollment</option>
            <option value="Monthly">Monthly</option>
            <option value="Annual">Annual</option>
            <option value="Lifetime">Lifetime</option>
          </select>
        </label>
        <label className={LABEL}>Minimum service (months)
          <input type="number" min="0" max="600" step="1" className={INPUT} value={minimumServiceMonths} onChange={(e) => setMinimumServiceMonths(e.target.value)} />
        </label>
        <label className={LABEL}>Effective from
          <input type="date" required className={INPUT} value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className={LABEL}>Effective to
          <input type="date" className={INPUT} value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>
      <label className="flex items-center gap-2 text-xs font-medium text-slate-600 dark:text-slate-300">
        <input type="checkbox" checked={requireProbationCompleted} onChange={(e) => setRequireProbationCompleted(e.target.checked)} /> Require completed probation
      </label>
      <label className={LABEL}>Custom policy note
        <textarea className={INPUT} rows={2} maxLength={1000} value={customCriteriaNote} onChange={(e) => setCustomCriteriaNote(e.target.value)} placeholder="Visible policy detail, approval condition, or benefit-specific instruction" />
      </label>
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
  const [paymentPolicy, setPaymentPolicy] = useState(initial?.paymentPolicy ?? DEFAULT_PAYMENT_POLICY);
  const initialUsesCustomType = !!initial && !PLAN_TYPES.includes(initial.planType);
  const [customPlanType, setCustomPlanType] = useState(initialUsesCustomType ? initial!.planType : '');
  const [form, setForm] = useState({
    companyId: initial?.companyId ?? '',
    code: initial?.code ?? '',
    name: initial?.name ?? '',
    planType: initialUsesCustomType ? 'Custom' : (initial?.planType ?? 'Medical'),
    classification: initial?.classification ?? 'Discretionary',
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
    if (form.planType === 'Custom' && !customPlanType.trim()) { setError('Enter a custom benefit type.'); return; }
    setSaving(true); setError(null);
    try {
      const body = {
        name: form.name.trim(), planType: form.planType === 'Custom' ? customPlanType.trim() : form.planType, classification: form.classification, currency: form.currency.trim().toUpperCase(),
        effectiveFrom: form.effectiveFrom, effectiveTo: form.effectiveTo || null,
        requiresEnrollment: form.requiresEnrollment, isActive: form.isActive,
        paymentPolicy, expectedPolicyVersion: initial?.policyVersion ?? 0,
      };
      const saved = editing
        ? await benefitsApi.updatePlan(initial!.id, body)
        : await benefitsApi.createPlan({ ...body, companyId: form.companyId || null, code: form.code.trim().toUpperCase() });
      toast.success(editing ? 'Plan updated.' : form.classification === 'Mandatory'
        ? 'Mandatory plan created. Grade rules can define optional enhancements.'
        : 'Plan created. Add at least one grade eligibility rule before enrolling employees.');
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
          {form.planType === 'Custom' && <label className={LABEL}>Custom benefit type
            <input required className={INPUT} maxLength={120} value={customPlanType} onChange={(e) => setCustomPlanType(e.target.value)} placeholder="e.g. Home office allowance" />
          </label>}
          <label className={LABEL}>Policy class
            <select className={INPUT} value={form.classification} onChange={(e) => set('classification', e.target.value as typeof form.classification)}>
              <option value="Discretionary">Discretionary — grade eligibility</option>
              <option value="Contractual">Contractual — grade eligibility</option>
              <option value="Mandatory">Mandatory minimum — no grade exclusion</option>
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
        <BenefitPaymentPolicyEditor value={paymentPolicy} onChange={setPaymentPolicy} currency={form.currency} editing={editing} paymentMethodLocked={editing && (initial?.policyVersion ?? 0) > 0} />
        {editing && <p className="text-[11px] text-slate-500 dark:text-slate-400">Code and company scope identify the plan to existing enrolments and cannot be changed.</p>}
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

function EnrollModal({ plans, initialPlanId, canApplyException, onAdditional, onClose, onEnrolled }: {
  onAdditional: (employee: BenefitEmployee, planId: string) => void; canApplyException: boolean; plans: BenefitPlan[]; initialPlanId?: string; onClose: () => void; onEnrolled: () => void;
}) {
  const { t } = useLocale();
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
  const [requestedAmount, setRequestedAmount] = useState('');
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
      employeesApi.list({ search: q, pageSize: 8 })
        .then((r) => setResults((r.items ?? []).filter(item => !['Offboarded', 'Terminated', 'Deleted', 'Exited'].includes(item.status))))
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

  const canGrantException = !!check && canApplyException && !check.alreadyEnrolled
    && ['plan_active', 'company_scope', 'plan_window'].every(key => check.checks.some(item => item.key === key && item.passed));
  const canSubmit = !!check && !check.alreadyEnrolled && check.eligible;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!employee || !canSubmit || !check) return;
    const parsedAmount = requestedAmount.trim() ? Number(requestedAmount) : null;
    if (parsedAmount !== null && (!Number.isFinite(parsedAmount) || parsedAmount <= 0)) {
      setError('Requested benefit amount must be greater than zero.');
      return;
    }
    if (parsedAmount !== null && check.maximumBenefitAmount !== null && parsedAmount > check.maximumBenefitAmount) {
      setError(`Requested benefit amount cannot exceed ${money(check.maximumBenefitAmount)} ${check.currency}.`);
      return;
    }
    setSaving(true); setError(null);
    try {
      await benefitsApi.enroll({
        benefitPlanId: planId,
        employeeId: employee.id,
        coverageTier: tier,
        effectiveFrom: from,
        effectiveTo: to || null,
        requestedBenefitAmount: parsedAmount,
      });
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
                        <span className="font-mono text-xs text-slate-400">{r.employeeCode} · {t(r.status)} · {r.department}</span>
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

        {canGrantException && !check?.eligible && employee && <div className="space-y-2 rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs dark:border-amber-500/20 dark:bg-amber-500/5">
          <p className="text-slate-600 dark:text-slate-300">{t('Additional benefits require a separate request and independent approval.')}</p>
          <button type="button" className={SECONDARY} onClick={() => onAdditional({ id: employee.id, fullName: employee.fullName }, planId)}>{t('Manage benefits')}</button>
        </div>}

        {check && check.eligible && (
          <label className={LABEL}>Enrollment value (optional)
            <input
              type="number"
              min="0.01"
              max={check.maximumBenefitAmount ?? undefined}
              step="0.01"
              className={INPUT}
              value={requestedAmount}
              onChange={(e) => setRequestedAmount(e.target.value)}
              placeholder={check.maximumBenefitAmount === null ? 'Enter value if applicable' : `Up to ${money(check.maximumBenefitAmount)} ${check.currency}`}
            />
            <span className="mt-1 block text-[11px] font-normal text-slate-500 dark:text-slate-400">
              Records the value approved for this employee without changing the client-configured grade limit.
            </span>
          </label>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
          <button type="submit" disabled={saving || !canSubmit || checking} className={PRIMARY}>
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
      {check.eligible && (check.tierName || check.maximumBenefitAmount !== null) && (
        <div className="mt-2 rounded-lg border border-emerald-200/70 bg-white/70 px-3 py-2 text-xs dark:border-emerald-500/20 dark:bg-white/[0.03]">
          <p className="font-semibold text-slate-800 dark:text-slate-100">Resolved entitlement: {check.tierName || 'Standard tier'}</p>
          <p className="mt-0.5 text-slate-600 dark:text-slate-300">{check.maximumBenefitAmount === null ? 'No monetary cap configured' : `Maximum ${money(check.maximumBenefitAmount)} ${check.currency}`} {check.limitPeriod ? `· ${check.limitPeriod.replace(/([A-Z])/g, ' $1').trim()}` : ''}</p>
          {check.customCriteriaNote && <p className="mt-1 text-slate-500 dark:text-slate-400">{check.customCriteriaNote}</p>}
        </div>
      )}
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
  const statuses = [...new Set(enrollments.map(enrollmentStatus))].sort();

  const rows = enrollments.filter((e) =>
    (!planId || e.benefitPlanId === planId) &&
    (!status || enrollmentStatus(e) === status) &&
    (!companyId || e.companyId === companyId) &&
    (!q.trim() || e.employeeName.toLowerCase().includes(q.trim().toLowerCase())));

  if (enrollments.length === 0) {
    return (
      <div data-testid="enrollments-empty" className={`${CARD} flex flex-col items-center gap-3 p-10 text-center`}>
        <UserPlus className="h-10 w-10 text-slate-300 dark:text-slate-600" />
        <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">No one is enrolled yet</p>
        <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">Eligible grade benefits are assigned when an employee is created. You can also enrol existing employees here.</p>
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
              <th className="px-4 py-2.5 font-semibold">Assignment</th>
              <th className="px-4 py-2.5 font-semibold">Coverage</th>
              <th className="px-4 py-2.5 font-semibold">Company</th>
              <th className="px-4 py-2.5 font-semibold">Effective</th>
              <th className="px-4 py-2.5 font-semibold">Status</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr><td colSpan={7} className="px-4 py-6 text-center text-xs text-slate-500 dark:text-slate-400">No enrolments match these filters.</td></tr>
            ) : rows.map((e) => (
              <tr key={e.id} onClick={() => onOpen(e.id)} className="cursor-pointer border-b border-slate-50 last:border-0 hover:bg-slate-50 dark:border-white/[0.03] dark:hover:bg-white/[0.02]">
                <td className="px-4 py-2.5 font-medium text-slate-800 dark:text-slate-100">{e.employeeName}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{planName(e.benefitPlanId)}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300"><AssignmentLabel enrollment={e} /></td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{e.coverageTier}</td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{companyName(e.companyId)}</td>
                <td className="px-4 py-2.5 text-slate-500 dark:text-slate-400">{range(e.effectiveFrom, e.effectiveTo)}</td>
                <td className="px-4 py-2.5"><StatusPill active={enrollmentStatus(e) === 'Active'} label={enrollmentStatus(e)} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
