'use client';

import { pickable } from '../../lib/pickable';
import { useCallback, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { AlertTriangle, CircleSlash, HeartPulse, Pencil, Plus, RefreshCw, UserPlus } from 'lucide-react';
import {
  benefitsApi, benefitsErrorMessage,
  type BenefitEligibilityRule, type BenefitEnrollment, type BenefitLimitPeriod, type BenefitPlan,
} from '@/src/api/benefits';
import { educationRulesProblem } from '@/src/lib/benefitEducation';
import { gradesApi, type GradeDto } from '@/src/api/organization';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useReleaseA } from '@/src/lib/releaseA';
import { AdditionalBenefitApprovalSetup } from '@/src/components/benefits/AdditionalBenefitApprovalSetup';
import { BenefitPaymentPolicyEditor, BenefitPolicySummary, DEFAULT_PAYMENT_POLICY } from '@/src/components/benefits/BenefitPaymentPolicy';
import { INPUT, LABEL, PRIMARY, SECONDARY, CARD, enrollmentStatus, StatusPill, Modal, FormError, money, range } from '@/src/components/benefits/benefitUi';

const PLAN_TYPES = ['Medical', 'Dental', 'Life', 'Vision', 'Pension', 'Education', 'Housing', 'Transport', 'Allowance', 'Reimbursement', 'Custom'];

/**
 * The benefit plans and their eligibility rules: configuration, so it lives in Setup (Setup → Benefit plans). Enrolling
 * employees in a plan stays on the Benefits page. Editing is for Admin and HR Manager (the API's plan writes); a
 * release_a tenant sets who gets which benefit in Benefits by grade, so its rules are read-only here.
 */
export function BenefitPlansPanel() {
  const { t } = useLocale();
  const { hasRole } = useAuth();
  const { companies, companyVersion } = useCompany();
  const canManagePlans = hasRole('Admin') || hasRole('HR Manager');
  const canEnroll = canManagePlans || hasRole('HR Officer');
  const releaseA = useReleaseA();

  const [planSearch, setPlanSearch] = useState('');
  const [plans, setPlans] = useState<BenefitPlan[]>([]);
  const [enrollments, setEnrollments] = useState<BenefitEnrollment[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedPlanId, setSelectedPlanId] = useState<string | null>(null);
  const [planModal, setPlanModal] = useState<{ mode: 'create' } | { mode: 'edit'; plan: BenefitPlan } | null>(null);

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
  const matches = (plan: BenefitPlan) => !planSearch.trim()
    || [plan.name, plan.code, plan.planType].some(value => value.toLocaleLowerCase().includes(planSearch.trim().toLocaleLowerCase()));

  return (
    <div className="space-y-4">
      {!loading && !error && plans.length > 0 && canManagePlans && (
        <div className="flex justify-end">
          <button type="button" className={PRIMARY} onClick={() => setPlanModal({ mode: 'create' })}>
            <Plus className="h-3.5 w-3.5" /> {t('Add benefit plan')}
          </button>
        </div>
      )}

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
        <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
          <div className="space-y-3">
            <label className={LABEL}>{t('Find a benefit plan')}<input type="search" className={INPUT} value={planSearch} onChange={event => setPlanSearch(event.target.value)} placeholder={t('Search by name, code or category')} /></label>
            <PlanList plans={plans.filter(matches)} selectedId={selectedPlanId} onSelect={setSelectedPlanId}
              companyName={companyName} enrolledCount={enrolledCount} />
            {planSearch.trim() && !plans.some(matches) && <p role="status" className="p-3 text-sm text-slate-500">{t('No benefit plans match your search.')}</p>}
          </div>
          {selectedPlan ? (
            <PlanDetail key={selectedPlan.id} plan={selectedPlan} companyName={companyName} gradeName={gradeName}
              companies={companies} grades={grades} canManage={canManagePlans} canEnroll={canEnroll}
              onEdit={() => setPlanModal({ mode: 'edit', plan: selectedPlan })} />
          ) : (
            <div className={`${CARD} flex flex-col items-center justify-center gap-2 p-10 text-center`}>
              <HeartPulse className="h-8 w-8 text-slate-300 dark:text-slate-600" />
              <p className="text-sm font-medium text-slate-600 dark:text-slate-300">Select a plan</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">View and edit its details and eligibility rules.</p>
            </div>
          )}
        </div>
      )}

      {planModal && (
        <PlanModal
          initial={planModal.mode === 'edit' ? planModal.plan : null}
          companies={companies}
          onClose={() => setPlanModal(null)}
          onSaved={(p) => { setPlanModal(null); setSelectedPlanId(p.id); void load(); }}
        />
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
        <h2 className="mt-3 text-base font-semibold text-slate-800 dark:text-slate-100">Set up your first benefit plan</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">No benefit plans exist yet. Three steps get employees covered.</p>
      </div>
      <ol className="mx-auto mt-6 grid max-w-3xl gap-3 sm:grid-cols-3">
        {steps.map((s) => (
          <li key={s.n} className="rounded-xl border border-slate-200 p-4 dark:border-white/[0.06]">
            <span className="flex h-6 w-6 items-center justify-center rounded-full bg-sapphire text-xs font-semibold text-white">{s.n}</span>
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
            <p className="mt-0.5 text-caption text-slate-500 dark:text-slate-400">
              <span className="font-mono">{p.code}</span> · {p.planType} · {p.classification} · {p.currency} · {companyName(p.companyId)}
            </p>
            <p className="text-caption text-slate-400">{range(p.effectiveFrom, p.effectiveTo)}</p>
          </div>
          <div className="flex shrink-0 flex-col items-end gap-1">
            <StatusPill active={p.isActive} />
            <span className="text-caption text-slate-500 dark:text-slate-400">{enrolledCount.get(p.id) ?? 0} enrolled</span>
          </div>
        </button>
      ))}
    </div>
  );
}

function PlanDetail({ plan, companyName, gradeName, companies, grades, canManage, canEnroll, onEdit }: {
  plan: BenefitPlan; companyName: (id: string | null) => string; gradeName: (id: string | null) => string;
  companies: { id: string; name: string }[]; grades: GradeDto[];
  canManage: boolean; canEnroll: boolean; onEdit: () => void;
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
          <h2 className="text-base font-semibold text-slate-800 dark:text-slate-100">{plan.name}</h2>
          <p className="text-xs text-slate-500 dark:text-slate-400"><span className="font-mono">{plan.code}</span> · {plan.planType} · {plan.classification} · {plan.currency}</p>
          <BenefitPolicySummary policy={plan.paymentPolicy} currency={plan.currency} />
        </div>
        <div className="flex gap-2">
          {canManage && <button type="button" className={SECONDARY} onClick={onEdit}><Pencil className="h-3.5 w-3.5" /> Edit plan</button>}
          {canEnroll && plan.isActive && <Link href={`/benefits?enrol=${plan.id}`} className={PRIMARY}><UserPlus className="h-3.5 w-3.5" /> Enrol in this plan</Link>}
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
        <p className="mb-2 text-caption text-slate-500 dark:text-slate-400">
          {plan.classification === 'Mandatory'
            ? 'Mandatory minimum coverage cannot be denied by grade. Grade rules may still define enhanced tiers above that floor.'
            : rulesFrozen ? 'Configure grade defaults in Benefits by grade. Existing rules remain available for reference.' : 'New employees automatically receive the benefits configured for their grade, subject to the effective dates and eligibility conditions below.'}
        </p>
        {rulesFrozen && (
          <p data-testid="rules-moved" className="mb-2 rounded-xl border border-sapphire/30 bg-sapphire/[0.04] px-3 py-2 text-caption text-slate-600 dark:text-slate-300">
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
          <p className="mt-2 text-caption text-slate-500 dark:text-slate-400">{activeRules.length} active grade assignment(s). {!rulesFrozen && 'Authorized HR can apply individual exceptions after enrolment.'}</p>
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
  const { t } = useLocale();
  const [companyId, setCompanyId] = useState('');
  const [gradeId, setGradeId] = useState('');
  const [gradeMatchMode, setGradeMatchMode] = useState<'Exact' | 'LevelAndAbove'>('Exact');
  const [tierName, setTierName] = useState('');
  const [maxBenefitAmount, setMaxBenefitAmount] = useState('');
  const [limitPeriod, setLimitPeriod] = useState<BenefitLimitPeriod>('PerEnrollment');
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
            {[...pickable(grades)].sort((a, b) => a.level - b.level).map((g) => <option key={g.id} value={g.id}>{g.name} ({g.code}) · level {g.level}</option>)}
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
            <option value="ContractYear">{t('Contract year')}</option>
          </select>
          {limitPeriod === 'ContractYear' && <span className="mt-1 block font-normal text-slate-500">{t('Starts on the employee’s contract start date and follows renewals, not January to December.')}</span>}
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
  const { t } = useLocale();
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
    const rulesProblem = educationRulesProblem(paymentPolicy);
    if (rulesProblem) { setError(t(rulesProblem.key, rulesProblem.params)); return; }
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
        <BenefitPaymentPolicyEditor value={paymentPolicy} onChange={setPaymentPolicy} planName={form.name} planCode={form.code} currency={form.currency} editing={editing} paymentMethodLocked={editing && (initial?.policyVersion ?? 0) > 0} />
        {editing && <p className="text-caption text-slate-500 dark:text-slate-400">Code and company scope identify the plan to existing enrolments and cannot be changed.</p>}
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
