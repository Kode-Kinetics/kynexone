'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle, CheckCircle2, HeartPulse, Loader2, Plus, RefreshCw, Search, ShieldCheck, UserPlus, XCircle,
} from 'lucide-react';
import {
  benefitsApi, benefitsErrorMessage,
  type BenefitEligibilityCheck,
  type BenefitEnrollment, type BenefitPlan,
} from '@/src/api/benefits';
import { employeesApi, type EmployeeListItem } from '@/src/api/employees';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useReleaseA } from '@/src/lib/releaseA';
import type { BenefitEmployee } from '@/src/components/benefits/AdditionalBenefitForm';
import { BenefitChecklist } from '@/src/components/benefits/BenefitChecklist';
import { AdditionalBenefitRequestDetail } from '@/src/components/benefits/AdditionalBenefitRequestDetail';
import { msg } from '@/src/i18n/translations';
import { ModuleSettingsLink } from '@/src/components/setup/ModuleSettingsLink';
import { AssignmentLabel, EnrollmentDrawer } from '@/src/components/benefits/BenefitEnrollmentDrawer';
import { INPUT, LABEL, PRIMARY, SECONDARY, CARD, COVERAGE_TIERS, today, enrollmentStatus, StatusPill, Modal, FormError, money, range } from '@/src/components/benefits/benefitUi';

// ── page ────────────────────────────────────────────────────────────────────────

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

  const [plans, setPlans] = useState<BenefitPlan[]>([]);
  const [enrollments, setEnrollments] = useState<BenefitEnrollment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

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

  // "Enrol in this plan" in Setup → Benefit plans arrives as /benefits?enrol=<plan>: open the enrolment form on that plan, once.
  const [enrolRequest, setEnrolRequest] = useState<string | null>(null);
  useEffect(() => { setEnrolRequest(new URLSearchParams(window.location.search).get('enrol')); }, []);
  useEffect(() => {
    if (!enrolRequest || loading || error) return;
    setEnrolRequest(null);
    if (canEnroll && plans.some(plan => plan.id === enrolRequest && plan.isActive)) setEnrollFor({ planId: enrolRequest });
  }, [enrolRequest, loading, error, canEnroll, plans]);

  const companyById = useMemo(() => new Map(companies.map((company) => [company.id, company.name])), [companies]);
  const companyName = useCallback((id: string | null) =>
    id === null ? 'All companies' : (companyById.get(id) ?? 'Company'), [companyById]);

  const activePlans = plans.filter((p) => p.isActive);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Benefits Administration</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">{releaseA ? 'Employee enrolments and contributions' : 'Employee enrolments, exceptions and contributions'}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {!loading && !error && (plans.length > 0 || enrollments.length > 0) && (
            <>
              {canProposeAdditional && <button type="button" className={SECONDARY} onClick={() => setAdditionalFor({})}><Plus className="h-3.5 w-3.5" />{t('Manage benefits')}</button>}
              {canEnroll && (
                <button type="button" className={SECONDARY} disabled={activePlans.length === 0} onClick={() => setEnrollFor({})}>
                  <UserPlus className="h-3.5 w-3.5" /> Enrol employee
                </button>
              )}
            </>
          )}
          <ModuleSettingsLink sections={['benefitPlans']} label={msg('Benefit settings')} />
        </div>
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
        <NoPlansYet />
      ) : (
        <EnrollmentList enrollments={enrollments} plans={plans} companies={companies} companyName={companyName}
          canEnroll={canEnroll && activePlans.length > 0} onEnroll={() => setEnrollFor({})} onOpen={setOpenEnrollmentId} />
      )}

      {enrollFor && (
        <EnrollModal
          canApplyException={canProposeAdditional}
          onAdditional={(employee, planId) => { setEnrollFor(null); setAdditionalFor({ employee, planId }); }}
          plans={activePlans} initialPlanId={enrollFor.planId}
          onClose={() => setEnrollFor(null)}
          onEnrolled={() => { setEnrollFor(null); void load(); }}
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

// ── empty state: no plans yet ───────────────────────────────────────────────────

/** Plans are configuration: an empty tenant is pointed to Setup, where the first plan is created. */
function NoPlansYet() {
  const { t } = useLocale();
  return (
    <div data-testid="benefits-empty" className={`${CARD} p-8`}>
      <div className="mx-auto max-w-xl text-center">
        <HeartPulse className="mx-auto h-10 w-10 text-sapphire" />
        <h2 className="mt-3 text-base font-bold text-slate-800 dark:text-slate-100">{t('No benefit plans yet')}</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t('Plans are set up in Setup. Once one exists, employees can be enrolled here.')}</p>
        <ModuleSettingsLink sections={['benefitPlans']} label={msg('Set up benefit plans')} className="mt-4" />
      </div>
    </div>
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
