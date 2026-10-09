'use client';

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, ArrowLeft, ArrowRight, Building2, CheckCircle2, Eye, Info, Pencil, ShieldCheck, Trash2, Wand2 } from 'lucide-react';
import Link from 'next/link';
import { SetupPolicyEditor } from './SetupPolicyEditor';
import type { CompanyDto } from '../api/organization';
import { tenantAdminApi } from '../api/intelligence';
import { useT } from '../hooks/useT';
import { useReleaseA } from '../lib/releaseA';
import { setupAssistantApi, type CompanyProfile, type SetupDraft, type SetupConfiguration } from '../api/setupAssistant';

const COUNTRIES = [
  { code: 'SA', label: 'Saudi Arabia' }, { code: 'AE', label: 'United Arab Emirates' },
  { code: 'QA', label: 'Qatar' }, { code: 'KW', label: 'Kuwait' },
  { code: 'BH', label: 'Bahrain' }, { code: 'OM', label: 'Oman' },
  { code: 'EG', label: 'Egypt' }, { code: 'IN', label: 'India' }, { code: 'GB', label: 'United Kingdom' }, { code: 'US', label: 'United States' },
];
const SIZES = ['1-50', '51-200', '201-500', '500+'];

// Every option below writes to a column the product already reads. The label is what a business
// would say about itself; the value is what the entity stores.
const WORK_PATTERNS: [string, string][] = [
  ['SingleDayShift', 'One day shift'],
  ['TwoShifts', 'Two shifts (day + evening)'],
  ['ContinuousThreeShifts', 'Round the clock (three shifts)'],
  ['FieldRoster', 'Field crews on a roster'],
];
// The REST days, because that is how people describe their week. The working week is the complement.
const WEEKEND_PATTERNS: [string, string][] = [
  ['CountryDefault', 'Use the country default'],
  ['Fri-Sat', 'Friday & Saturday'],
  ['Sat-Sun', 'Saturday & Sunday'],
  ['Fri', 'Friday only'],
  ['Sun', 'Sunday only'],
];
const WORKFORCE_MIX: [string, string][] = [
  ['MostlyNational', 'Mostly nationals'],
  ['Mixed', 'Mixed nationals and expatriates'],
  ['MostlyExpat', 'Mostly expatriates'],
];
const OVERTIME_HANDLING: [string, string][] = [
  ['PaidOvertime', 'Paid overtime'],
  ['CompensatoryOff', 'Time off in lieu'],
  ['NotApplicable', 'We do not pay overtime'],
];
const ATTENDANCE_CAPTURE: [string, string][] = [
  ['BiometricDevice', 'Biometric device'],
  ['MobileGeofence', 'Mobile app with location'],
  ['WebCheckIn', 'Web check-in'],
  ['Manual', 'Entered by hand'],
];
const PAY_CYCLES: [string, string][] = [
  ['Monthly', 'Monthly'],
];
const LANGUAGES: [string, string][] = [
  ['en', 'English'],
  ['ar', 'Arabic'],
  ['bilingual', 'Both (English first, Arabic available)'],
];
const TIMEZONES = [
  '', 'Asia/Riyadh', 'Asia/Dubai', 'Asia/Qatar', 'Asia/Kuwait', 'Asia/Bahrain', 'Asia/Muscat',
  'Africa/Cairo', 'Asia/Kolkata', 'Europe/London', 'America/New_York', 'UTC',
];
const CURRENCIES = ['SAR', 'AED', 'QAR', 'KWD', 'BHD', 'OMR', 'USD', 'EUR', 'GBP', 'INR', 'EGP'];

type SectionKey = 'entity' | 'org' | 'leave' | 'leavePolicies' | 'shifts' | 'attendance' | 'payroll' | 'holidays' | 'governance' | 'localization' | 'benefits';


const SETUP_STEPS = [
  { title: 'Company details', description: 'Your organization at a glance', heading: 'Start with your company', help: 'Tell us about your organization. Your legal entity, country, currency and industry are required to prepare your draft.' },
  { title: 'Working week', description: 'Schedules, attendance and leave', heading: 'How does your team work?', help: 'Choose the working arrangements your team uses. You can review the proposed policies before applying them.' },
  { title: 'People & pay', description: 'Pay structure and approvals', heading: 'Set your people and pay preferences', help: 'These choices shape your draft. Check them against your company policies before continuing.' },
  { title: 'Grades & benefits', description: 'Salary bands and benefit plans', heading: 'Configure your grades and benefits', help: 'Use your established company policy or review an AI-assisted grade draft.' },
  { title: 'Review & create', description: 'Choose, preview and apply', heading: 'Choose what to include', help: 'Generate a draft, review the proposed records, then apply when you are ready.' },
];
const APPROVAL_MODELS = [['DepartmentHead', 'Department head, then HR'], ['SupervisorFirst', 'Supervisor, department head, then HR']];

export function AiSetupAssistant({ companies = [] }: { companies?: CompanyDto[]; }) {
  // Release A: grade allowances and benefits are set in Benefits by grade, so the draft's legacy grade pay lines are
  // neither shown nor sent (the server would skip them and say so).
  const releaseA = useReleaseA();
  const t = useT();
  // Blank until the workspace answers. These used to be hardcoded 'SA' and 'SAR', which meant the
  // assistant asked an admin to re-key what the workspace already knew and, worse, quietly priced
  // the draft in whatever the boxes happened to say. A currency is not visibly wrong on screen —
  // 3,000 reads the same in riyals and dollars — so it is never pre-filled with a guess.
  const [country, setCountry] = useState('');
  const [industry, setIndustry] = useState('');
  const [size, setSize] = useState('51-200');
  const [currency, setCurrency] = useState('');
  const [profileSource, setProfileSource] = useState<'loading' | 'workspace' | 'unstated'>('loading');
  const [policyGuideOpen, setPolicyGuideOpen] = useState(false);
  const [legalEntityName, setLegalEntityName] = useState('');
  const [branchCity, setBranchCity] = useState('');
  const [operatingModel, setOperatingModel] = useState('Functional');
  const payrollModel = 'GradeBased';
  const [approvalModel, setApprovalModel] = useState('DepartmentHead');
  const [strictEntityScope, setStrictEntityScope] = useState(true);
  const [requireCostCenterForPayroll, setRequireCostCenterForPayroll] = useState(true);
  const [requireGradeForApprovalPolicy, setRequireGradeForApprovalPolicy] = useState(true);
  const [notes, setNotes] = useState('');
  // Operating choices. Each default is the most common answer, never a silent assumption the
  // draft hides: whatever is selected here is what the generated configuration says.
  const [workPattern, setWorkPattern] = useState('SingleDayShift');
  const [weekendPattern, setWeekendPattern] = useState('CountryDefault');
  const [leaveYearBasis, setLeaveYearBasis] = useState('Calendar');
  const [probationMonths, setProbationMonths] = useState(3);
  const [noticePeriodDays, setNoticePeriodDays] = useState(30);
  const [workforceMix, setWorkforceMix] = useState('Mixed');
  const [overtimeHandling, setOvertimeHandling] = useState('PaidOvertime');
  const [attendanceCapture, setAttendanceCapture] = useState('WebCheckIn');
  const [payCycle, setPayCycle] = useState('Monthly');
  const [timeZone, setTimeZone] = useState('');
  const [defaultLanguage, setDefaultLanguage] = useState('en');
  const [sections, setSections] = useState<Record<SectionKey, boolean>>({
    entity: true, org: true, leave: true, leavePolicies: true, shifts: true,
    attendance: true, payroll: true, holidays: true, governance: true, localization: true, benefits: true,
  });

  const [configuration, setConfiguration] = useState<SetupConfiguration>({ attendanceMethods: ['WebCheckIn'], overtimeModes: ['PaidOvertime'], grades: null, leavePolicies: null, benefitPlans: [] });
  const [step, setStep] = useState(0);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const errorRef = useRef<HTMLParagraphElement>(null);
  const profileRevision = useRef(0);
  const [loading, setLoading] = useState(false);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState('');
  const [engine, setEngine] = useState('');
  const [genNotes, setGenNotes] = useState<string[]>([]);
  const [draft, setDraft] = useState<SetupDraft | null>(null);
  const [done, setDone] = useState<{ applied: Record<string, number>; total: number; skipped?: Record<string, { count: number; reasonCode: string; reason: string; }>; } | null>(null);

  // The workspace's own country and currency. Setup - Localization is where a tenant states these;
  // reading them here is what stops the draft disagreeing with the rest of the product. An empty
  // field from the API means "not stated" — it is left empty here too, and the admin is asked.
  useEffect(() => {
    let cancelled = false;
    tenantAdminApi.getLocalization()
      .then(loc => {
        if (cancelled) return;
        const c = (loc?.countryCode ?? '').trim().toUpperCase();
        const cur = (loc?.currencyCode ?? '').trim().toUpperCase();
        if (c) setCountry(current => current || c);
        if (cur) setCurrency(current => current || cur);
        setProfileSource(c || cur ? 'workspace' : 'unstated');
      })
      .catch(() => { if (!cancelled) setProfileSource('unstated'); });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    profileRevision.current += 1;
    setDraft(null);
    setError('');
  }, [country, currency, industry, size, legalEntityName, branchCity, operatingModel, payrollModel,
    approvalModel, strictEntityScope, requireCostCenterForPayroll, requireGradeForApprovalPolicy,
    notes, workPattern, weekendPattern, leaveYearBasis, probationMonths, noticePeriodDays,
    workforceMix, overtimeHandling, attendanceCapture, payCycle, timeZone, defaultLanguage, sections, configuration]);

  useEffect(() => { if (error) errorRef.current?.focus(); }, [error]);

  const goToStep = (next: number) => {
    if (loading || applying) return;
    if (next > 0 && (!legalEntityName.trim() || !industry.trim() || !country || !currency)) {
      setError('Add your legal entity name, industry, country and currency to continue.');
      setStep(0);
      return;
    }
    setError('');
    setStep(next);
    requestAnimationFrame(() => headingRef.current?.focus());
  };

  const toggle = (k: SectionKey) => setSections(s => ({ ...s, [k]: !s[k] }));
  const selectedCount = Object.values(sections).filter(Boolean).length;
  // Full Record<SectionKey, boolean> literal — an Object.fromEntries shortcut would widen
  // to { [k: string]: boolean } and fail the strict Record<SectionKey, boolean> assignment.
  const setAllSections = (value: boolean) =>
    setSections({
      entity: value, org: value, leave: value, leavePolicies: value, shifts: value,
      attendance: value, payroll: value, holidays: value, governance: value, localization: value, benefits: value,
    });
  const sectionCount = 11;

  const policyProfile: CompanyProfile = {
    countryCode: country, industry, companySize: size, currencyCode: currency,
    legalEntityName, branchCity, operatingModel, payrollModel, approvalModel,
    strictEntityScope, requireCostCenterForPayroll, requireGradeForApprovalPolicy,
    notes, sections, workPattern, weekendPattern, leaveYearBasis, probationMonths,
    noticePeriodDays, workforceMix, overtimeHandling, attendanceCapture, payCycle,
    timeZone, defaultLanguage, configuration,
  };
  const acceptProfileSuggestions = (patch: Partial<CompanyProfile>) => {
    if (patch.countryCode !== undefined) setCountry(patch.countryCode);
    if (patch.industry !== undefined) setIndustry(patch.industry);
    if (patch.companySize !== undefined) setSize(patch.companySize);
    if (patch.currencyCode !== undefined) setCurrency(patch.currencyCode);
    if (patch.legalEntityName !== undefined) setLegalEntityName(patch.legalEntityName);
    if (patch.branchCity !== undefined) setBranchCity(patch.branchCity);
    if (patch.operatingModel !== undefined) setOperatingModel(patch.operatingModel);
    if (patch.approvalModel !== undefined) setApprovalModel(patch.approvalModel);
    if (patch.leaveYearBasis !== undefined) setLeaveYearBasis(patch.leaveYearBasis);
    if (patch.workPattern !== undefined) setWorkPattern(patch.workPattern);
    if (patch.weekendPattern !== undefined) setWeekendPattern(patch.weekendPattern);
    if (patch.probationMonths !== undefined) setProbationMonths(patch.probationMonths);
    if (patch.noticePeriodDays !== undefined) setNoticePeriodDays(patch.noticePeriodDays);
    if (patch.workforceMix !== undefined) setWorkforceMix(patch.workforceMix);
    if (patch.overtimeHandling !== undefined) setOvertimeHandling(patch.overtimeHandling);
    if (patch.attendanceCapture !== undefined) setAttendanceCapture(patch.attendanceCapture);
    if (patch.payCycle !== undefined) setPayCycle(patch.payCycle);
    if (patch.timeZone !== undefined) setTimeZone(patch.timeZone);
    if (patch.defaultLanguage !== undefined) setDefaultLanguage(patch.defaultLanguage);
  };

  const generate = async () => {
    if (!configuration.attendanceMethods?.length || !configuration.overtimeModes?.length) {
      setError(t('Select at least one time-recording method and overtime option.')); setStep(1); return;
    }
    if (configuration.grades?.some(g => !g.code.trim() || !g.name.trim() || !Number.isFinite(g.minSalary + g.midSalary + g.maxSalary) || g.minSalary < 0 || g.minSalary > g.midSalary || g.midSalary > g.maxSalary)) {
      setError(t('Complete each grade and keep minimum salary at or below midpoint and maximum.')); setStep(3); return;
    }
    if (configuration.benefitPlans?.some(b => !b.code.trim() || !b.name.trim() || !b.planType.trim() || !b.effectiveFrom || (b.effectiveTo && b.effectiveTo < b.effectiveFrom))) {
      setError(t('Complete each benefit plan and check its effective dates.')); setStep(3); return;
    }
    if (configuration.leavePolicies?.some(p => !p.name.trim() || !p.leaveTypeCode.trim() || !Number.isFinite(p.annualEntitlementDays) || p.annualEntitlementDays < 0)) {
      setError(t('Complete each leave policy name, type and entitlement.')); setStep(1); return;
    }
    if (!legalEntityName.trim()) { setError('Choose an existing legal entity or enter its registered name.'); return; }
    if (!industry.trim()) { setError('Tell me your industry so the suggestions fit.'); return; }
    if (!country) { setError('Pick the country this workspace operates in — the statutory defaults, holidays and working week all follow it.'); return; }
    // Generating without one would price every salary band in a currency nobody chose, and a band
    // in the wrong currency looks exactly like a band in the right one.
    if (!currency) { setError('Pick the currency before generating — salary bands are drafted in it, and a wrong currency is not visible on the figures.'); return; }
    if (selectedCount === 0) { setError('Select at least one section to include.'); return; }
    const revision = profileRevision.current;
    setLoading(true); setError(''); setDone(null);
    try {
      const profile: CompanyProfile = {
        countryCode: country, industry: industry.trim(), companySize: size, currencyCode: currency,
        legalEntityName: legalEntityName.trim() || undefined,
        branchCity: branchCity.trim() || undefined,
        operatingModel,
        payrollModel,
        approvalModel,
        strictEntityScope,
        requireCostCenterForPayroll,
        requireGradeForApprovalPolicy,
        notes: notes.trim() || undefined,
        sections,
        workPattern,
        weekendPattern,
        leaveYearBasis,
        probationMonths,
        noticePeriodDays,
        workforceMix,
        overtimeHandling,
        attendanceCapture,
        payCycle,
        timeZone: timeZone || undefined,
        defaultLanguage,
        configuration: { ...configuration, grades: configuration.grades?.map(g => ({ ...g, currency })) ?? configuration.grades, benefitPlans: configuration.benefitPlans?.map(b => ({ ...b, currency, gradeCodes: b.gradeCodes.map(code => code.trim()).filter(Boolean) })) },
      };
      const r = await setupAssistantApi.preview(profile);
      if (revision !== profileRevision.current) return;
      if (r.configurationVersion !== 1) { setError(t('The setup service needs an update to support these policy settings. Your entries are preserved; no configuration has been applied.')); return; }
      setDraft(r.draft); setEngine(r.engine); setGenNotes(r.notes);
      requestAnimationFrame(() => headingRef.current?.focus());
    } catch (e: unknown) {
      const err = e as { response?: { status?: number; data?: { message?: string; }; }; };
      if (err?.response?.status === 403) {
        setError("You don't have access to the setup assistant (Admin or HR Manager role required).");
      } else {
        setError(err?.response?.data?.message ?? 'Could not generate a setup. Try again.');
      }
    } finally { setLoading(false); }
  };

  const apply = async () => {
    if (!draft || !legalEntityName.trim()) return;
    setApplying(true); setError('');
    try {
      const r = await setupAssistantApi.apply(releaseA ? { ...draft, gradePayComponents: [] } : draft, country, currency, legalEntityName.trim() || undefined);
      setDone(r);
    } catch (e: unknown) {
      // Keep the draft in state on 403 so an admin can apply the exact reviewed draft.
      // The axios interceptor already fires a global access-denied toast; add only the
      // inline, domain-specific message here (no second toast).
      const err = e as { response?: { status?: number; data?: { message?: string; }; }; };
      if (err?.response?.status === 403) {
        setError("Applying requires the 'organization.setup.apply' permission. Your account can preview a setup but not commit it — ask an administrator to apply this reviewed draft, or to grant you the permission.");
      } else {
        setError(err?.response?.data?.message ?? 'Could not apply the setup.');
      }
    } finally { setApplying(false); }
  };

  /** Edit one row of one draft list in place. The cast is confined here: SetupDraft's list members
      have no common base, and the alternative is fourteen near-identical setters. */
  function patch<K extends keyof SetupDraft>(key: K, idx: number, changes: Record<string, unknown>) {
    setDraft(d => {
      if (!d) return d;
      const list = d[key];
      if (!Array.isArray(list)) return d;
      return { ...d, [key]: list.map((row, i) => (i === idx ? { ...(row as object), ...changes } : row)) };
    });
  }

  /** Edit a single-object draft member (working week, ID rule, attendance, localization…). */
  function patchOne<K extends keyof SetupDraft>(key: K, changes: Record<string, unknown>) {
    setDraft(d => (d && d[key] ? { ...d, [key]: { ...(d[key] as object), ...changes } } : d));
  }

  // remove a row from a draft list
  function removeAt<K extends keyof SetupDraft>(key: K, idx: number) {
    setDraft(d => {
      if (!d) return d;
      const list = d[key];
      if (!Array.isArray(list)) return d;
      return { ...d, [key]: list.filter((_, i) => i !== idx) };
    });
  }

  const totalItems = draft
    ? draft.departments.length + draft.designations.length + draft.grades.length +
    draft.branches.length + draft.costCenters.length + (releaseA ? 0 : draft.gradePayComponents.length) +
    draft.leaveTypes.length + draft.shifts.length + draft.payComponents.length +
    draft.statutoryRules.length + (draft.workingWeek ? 1 : 0) +
    (draft.employeeIdRule ? 1 : 0) + (draft.hrConfig ? 1 : 0) +
    draft.leavePolicies.length + (draft.holidayCalendar?.holidays.length ?? 0) +
    (draft.attendancePolicy ? 1 : 0) +
    // The multipliers are rows of their own once applied, so they count as items here too —
    // otherwise the button promises fewer than the apply writes.
    (draft.overtimePolicy ? 1 + draft.overtimePolicy.multipliers.length : 0) +
    (draft.localization ? 1 : 0) + (draft.benefitPlans?.length ?? 0)
    : 0;

  if (done) {
    return (
      <div className="mx-auto max-w-md rounded-2xl border border-slate-200 bg-white p-8 text-center dark:border-white/10 dark:bg-white/[0.03]">
        <CheckCircle2 className="mx-auto mb-4 h-14 w-14 text-emerald-500" />
        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Setup Applied</h3>
        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
          Created <span className="font-semibold text-sapphire dark:text-cyanAccent">{done.total}</span> item(s):
        </p>
        <div className="mt-3 flex flex-wrap justify-center gap-1.5">
          {Object.entries(done.applied ?? {}).map(([k, n]) => (
            <span key={k} className="rounded-full bg-slate-100 px-2.5 py-1 text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{k}: {n}</span>
          ))}
        </div>
        {(done.skipped?.gradePayComponents?.count ?? 0) > 0 && <p role="status" className="mt-3 text-xs text-amber-800 dark:text-amber-200">
          {t('{count} grade pay line(s) were not saved: grade allowances and benefits are set in Benefits by grade.', { count: done.skipped!.gradePayComponents.count })}
        </p>}
        <button type="button" className="btn-primary mt-6 w-full" onClick={() => { setDone(null); setDraft(null); setPolicyGuideOpen(false); setStep(0); }}>Start another setup</button>
      </div>
    );
  }

  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-white/10 dark:bg-[#0d1225]">
      <div className="grid lg:grid-cols-[200px_minmax(0,1fr)]">
        <aside className="border-b border-slate-200 bg-slate-50/80 p-4 dark:border-white/10 dark:bg-white/[0.02] lg:border-b-0 lg:border-e lg:p-4">
          <nav aria-label="Company setup steps">
            <ol className="grid grid-cols-5 gap-1.5 lg:grid-cols-1">
              {SETUP_STEPS.map((item, index) => (
                <li key={t(item.title)}>
                  <button type="button" onClick={() => goToStep(index)} disabled={loading || applying}
                    aria-current={step === index ? 'step' : undefined}
                    className={`flex w-full items-center justify-center rounded-lg px-2 py-3 text-start lg:items-start lg:justify-start lg:gap-3 lg:px-3 lg:py-2 transition-colors disabled:opacity-60 ${step === index ? 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-blue-300' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5'}`}>
                    <span className={`mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full border text-xs font-semibold ${step === index ? 'border-sapphire bg-sapphire text-white' : 'border-slate-300 dark:border-slate-600'}`}>{index + 1}</span>
                    <span className="sr-only lg:not-sr-only"><span className="block text-sm font-semibold">{t(item.title)}</span><span className={`mt-0.5 hidden text-xs leading-5 ${step === index ? 'lg:block' : ''} ${step === index ? 'text-blue-700 dark:text-blue-200' : 'text-slate-500 dark:text-slate-400'}`}>{t(item.description)}</span></span>
                  </button>
                </li>
              ))}
            </ol>
            <p className="mt-2 text-sm font-medium text-slate-700 dark:text-slate-200 lg:hidden">{step + 1} / {SETUP_STEPS.length} · {t(SETUP_STEPS[step].title)}</p>
          </nav>
          <div className="mt-4 hidden border-t border-slate-200 pt-4 dark:border-white/10 lg:block">
            <ShieldCheck className="mb-2 h-5 w-5 text-slate-500 dark:text-slate-400" aria-hidden="true" />
            <p className="text-sm font-medium text-slate-800 dark:text-slate-200">You stay in control</p>
            <p className="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-400">The assistant prepares a draft. Nothing changes in your workspace until you review and apply it.</p>
          </div>
        </aside>
        <div className="min-w-0 p-5">
          <div hidden={step === 0 && policyGuideOpen} className="mb-4">
            <h2 ref={headingRef} tabIndex={-1} className="text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white">{step === 4 && draft ? 'Review your setup draft' : t(SETUP_STEPS[step].heading)}</h2>
            <p className={`mt-1 text-sm leading-5 text-slate-600 dark:text-slate-400 ${step === 0 ? 'xl:sr-only' : ''}`}>{step === 4 && draft ? `${totalItems} proposed items. Open any record to edit it, or remove what you do not need.` : t(SETUP_STEPS[step].help)}</p>
          </div>
          {error && <p ref={errorRef} tabIndex={-1} role="alert" className="mb-5 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700 outline-none dark:bg-red-500/10 dark:text-red-300">{error}</p>}
          <fieldset disabled={loading || applying} className="min-w-0">
            <div hidden={step !== 0}>
              <SetupPolicyEditor profile={policyProfile} onProfileChange={acceptProfileSuggestions} onSourceOpenChange={setPolicyGuideOpen} area="source" value={configuration} onChange={setConfiguration} currency={currency} />
              <div className={policyGuideOpen ? 'hidden' : 'grid gap-x-5 gap-y-4 sm:grid-cols-2 xl:grid-cols-4'}>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Legal entity name <span aria-hidden="true" className="text-slate-500">*</span></span>
                  <input className="input w-full" list="setup-legal-entities" required value={legalEntityName} onChange={e => {
                    const name = e.target.value;
                    setLegalEntityName(name);
                    const existing = companies.find(company => company.legalNameEn.trim().toLowerCase() === name.trim().toLowerCase());
                    if (existing) { setCountry(existing.countryCode || ''); setCurrency(existing.defaultCurrency || ''); }
                    setDraft(null);
                  }} placeholder="Choose or enter the registered name" />
                  <datalist id="setup-legal-entities">{companies.map(company => <option key={company.id} value={company.legalNameEn} />)}</datalist>
                  <span className="mt-1 block text-xs leading-5 text-slate-500 dark:text-slate-400">Choose an existing company or a new entity (permission and plan limits apply).</span>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Industry <span aria-hidden="true" className="text-slate-500">*</span></span>
                  <input className="input w-full" value={industry} onChange={e => { setIndustry(e.target.value); setDraft(null); }} placeholder="e.g. Construction, Retail, Healthcare" />
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Country <span aria-hidden="true" className="text-slate-500">*</span></span>
                  <select className="select w-full" value={country} onChange={e => { setCountry(e.target.value); setDraft(null); }}>
                    <option value="">{profileSource === 'loading' ? 'Reading your workspace…' : 'Select a country…'}</option>
                    {COUNTRIES.map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
                  </select>
                  {profileSource === 'workspace' && country && (
                    <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">Used to propose statutory defaults and public holidays.</span>
                  )}
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Currency <span aria-hidden="true" className="text-slate-500">*</span></span>
                  <select className="select w-full" value={currency} onChange={e => { setCurrency(e.target.value); setDraft(null); }}>
                    <option value="">{profileSource === 'loading' ? 'Reading your workspace…' : 'Select a currency…'}</option>
                    {CURRENCIES.map(c => <option key={c} value={c}>{c}</option>)}
                  </select>
                  <span className={`mt-1 block text-[11px] ${currency ? 'text-slate-400' : 'text-amber-600 dark:text-amber-400'}`}>
                    {currency
                      ? 'Every salary band is drafted in this currency.'
                      : profileSource === 'loading' ? 'Loading workspace defaults…' : 'Select the currency used for your salary bands.'}
                  </span>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Company size</span>
                  <select className="select w-full" value={size} onChange={e => setSize(e.target.value)}>
                    {SIZES.map(s => <option key={s} value={s}>{s} employees</option>)}
                  </select>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Suggested head office city (optional)</span>
                  <input className="input w-full" placeholder="e.g. Riyadh" value={branchCity} onChange={e => { setBranchCity(e.target.value); setDraft(null); }} />
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Working language</span>
                  <select className="select w-full" value={defaultLanguage} onChange={e => { setDefaultLanguage(e.target.value); setDraft(null); }}>
                    {LANGUAGES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                  </select>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Time zone</span>
                  <select className="select w-full" value={timeZone} onChange={e => { setTimeZone(e.target.value); setDraft(null); }}>
                    {TIMEZONES.map(tz => <option key={tz || 'auto'} value={tz}>{tz || 'Match the country'}</option>)}
                  </select>
                  <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">Used for dates and times across your workspace.</span>
                </label>
              </div>
            </div>
            <div hidden={step !== 1}>
              <div className="grid gap-x-6 gap-y-5 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">How do people work?</span>
                  <select className="select w-full" value={workPattern} onChange={e => { setWorkPattern(e.target.value); setDraft(null); }}>
                    {WORK_PATTERNS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                  </select>
                  <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">Sets the shifts and the standard working day.</span>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Weekend (days off)</span>
                  <select className="select w-full" value={weekendPattern} onChange={e => { setWeekendPattern(e.target.value); setDraft(null); }}>
                    {WEEKEND_PATTERNS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                  </select>
                  <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">Every leave day and overtime hour is counted against this.</span>
                </label>
              </div>
              <div className="mt-4"><SetupPolicyEditor area="work" value={configuration} onChange={setConfiguration} currency={currency} /></div>
            </div>
            <div hidden={step !== 2}>
              <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2 xl:grid-cols-[1fr_1fr_1.4fr]">

                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Workforce</span>
                  <select className="select w-full" value={workforceMix} onChange={e => { setWorkforceMix(e.target.value); setDraft(null); }}>
                    {WORKFORCE_MIX.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                  </select>
                  <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">Helps tailor suggested allowances to your workforce.</span>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Pay cycle</span>
                  <select className="select w-full" value={payCycle} onChange={e => { setPayCycle(e.target.value); setDraft(null); }}>
                    {PAY_CYCLES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                  </select>
                  <span className="mt-1 block text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Salary amounts currently use a monthly basis. Custom pay calendars require payroll conversion support and cannot be activated here yet.')}</span>
                </label>

                <div className="grid grid-cols-2 gap-3">
                  <p className="col-span-2 text-xs leading-5 text-slate-600 dark:text-slate-400">Probation and notice lengths are reference preferences. Set each employee’s actual terms on their record; the draft also uses probation to propose leave eligibility.</p>
                  <label className="block">
                    <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Probation preference (months)</span>
                    <input type="number" min={0} max={24} className="input w-full" value={probationMonths}
                      onChange={e => { setProbationMonths(Math.max(0, Math.min(24, Number(e.target.value) || 0))); setDraft(null); }} />
                  </label>
                  <label className="block">
                    <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Notice preference (days)</span>
                    <input type="number" min={0} max={365} className="input w-full" value={noticePeriodDays}
                      onChange={e => { setNoticePeriodDays(Math.max(0, Math.min(365, Number(e.target.value) || 0))); setDraft(null); }} />
                  </label>
                </div>
              </div>
              <details className="mt-4 border-t border-slate-200 pt-4 dark:border-white/10">
                <summary className="cursor-pointer text-sm font-medium text-slate-800 dark:text-slate-200">Planning preferences (optional)</summary>
                <p className="mb-3 mt-2 text-xs leading-5 text-slate-600 dark:text-slate-400">These preferences are saved for reference. They do not activate approval workflows or enforce payroll and access rules.</p>
                <div className="mb-4 grid gap-4 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Management structure preference</span>
                  <select className="select w-full" value={operatingModel} onChange={e => setOperatingModel(e.target.value)}>
                    {[['Functional', 'Department reporting'], ['Matrix', 'Matrix reporting']].map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                  </select>
                </label>
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Approval preference</span>
                  <select className="select w-full" value={approvalModel} onChange={e => setApprovalModel(e.target.value)}>
                    {APPROVAL_MODELS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                  </select>
                </label>
                </div>
                <div className="grid gap-2 rounded-lg border border-slate-200 p-3 dark:border-white/10">
                  {[
                    ['Prefer managers within their department and location', strictEntityScope, setStrictEntityScope],
                    ['Plan to use cost centers for payroll', requireCostCenterForPayroll, setRequireCostCenterForPayroll],
                    ['Plan to use grades for approval policies', requireGradeForApprovalPolicy, setRequireGradeForApprovalPolicy],
                  ].map(([label, value, setter]) => (
                    <label key={String(label)} className="flex items-center gap-2 text-xs text-slate-700 dark:text-slate-300">
                      <input type="checkbox" checked={Boolean(value)} onChange={e => (setter as (v: boolean) => void)(e.target.checked)} className="h-4 w-4 accent-sapphire" />
                      {String(label)}
                    </label>
                  ))}
                </div>
              </details>
              <SetupPolicyEditor area="governance" value={configuration} onChange={setConfiguration} currency={currency} />
            </div>
            <div hidden={step !== 3}><SetupPolicyEditor area="rewards" value={configuration} onChange={setConfiguration} currency={currency} releaseA={releaseA} /></div>
            <div hidden={step !== 4}>
              <div className="mb-3 flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 pb-3 dark:border-white/10">
                <div className="flex items-start gap-3">
                  <Building2 className="mt-1 h-5 w-5 shrink-0 text-slate-500" aria-hidden="true" />
                  <div><p className="font-semibold text-slate-900 dark:text-white">{legalEntityName || industry || 'Your company'}</p><p className="mt-1 text-sm text-slate-600 dark:text-slate-400">{COUNTRIES.find(c => c.code === country)?.label} · {size} employees · {currency}</p></div>
                </div>
                <button type="button" className="text-sm font-medium text-sapphire hover:underline dark:text-blue-300" onClick={() => goToStep(0)}>Edit company details</button>
              </div>
              <details open={!draft} className="mb-4">
                <summary className="mb-2 cursor-pointer text-sm font-medium text-slate-700 dark:text-slate-200">Draft choices · {selectedCount} sections included</summary>
                <fieldset role="group" aria-label="Sections to include in the draft" className="min-w-0">
                  <div className="mb-1.5 flex flex-wrap items-center justify-between gap-2">
                    <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Sections to include in the draft</span>
                    <div className="flex flex-wrap items-center gap-3">
                      <span className="text-xs text-slate-600 dark:text-slate-400">{selectedCount} of {sectionCount} selected</span>
                      <button type="button" className="text-[11px] text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setAllSections(true)}>Select all</button>
                      <button type="button" className="text-[11px] text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setAllSections(false)}>Clear all</button>
                    </div>
                  </div>
                  <p className="mb-2 text-xs text-slate-600 dark:text-slate-400">Uncheck anything you already have or prefer to configure yourself.</p>
                  <div className="grid gap-x-4 sm:grid-cols-2 xl:grid-cols-3">
                    {([
                      ['entity', 'Entity & cost centers'], ['org', 'Org structure'],
                      ['leave', 'Leave types'], ['leavePolicies', 'Leave entitlement'],
                      ['shifts', 'Shifts & working week'], ['attendance', 'Attendance & overtime'],
                      ['payroll', 'Payroll & statutory'], ['holidays', 'Public holidays'],
                      ['governance', 'Governance & IDs'], ['localization', 'Language & time zone'], ['benefits', 'Benefits'],
                    ] as [SectionKey, string][]).map(([k, label]) => (
                      <label key={k}
                        className={`flex cursor-pointer items-center gap-2 border-b border-slate-100 py-2 text-sm transition dark:border-white/10 ${sections[k]
                            ? 'text-slate-900 dark:text-white'
                            : 'text-slate-600 dark:text-slate-300'
                          }`}>
                        <input type="checkbox" checked={sections[k]} onChange={() => toggle(k)} aria-label={`Include ${label} in the generated draft`} className="h-4 w-4 accent-sapphire" />
                        {label}
                      </label>
                    ))}
                  </div>
                </fieldset>
                <div className="mt-3"><label className="block sm:col-span-2">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Anything specific? (optional)</span>
                  <input className="input w-full" value={notes} onChange={e => setNotes(e.target.value)} placeholder="e.g. we run 24/7 operations with field crews" />
                </label>
                </div>
              </details>
            </div>
          </fieldset>
          {/* Preview */}
          {step === 4 && draft && (
            <fieldset disabled={applying || loading} className="mt-6 min-w-0 space-y-4">
              {/* Draft-only banner */}
              <div className="flex items-start gap-3 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]">
                <Eye className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
                <p className="text-xs text-slate-600 dark:text-slate-300">
                  Company-specific records will use <span className="font-semibold text-slate-900 dark:text-white">{legalEntityName}</span>. Shared policies and master data apply across the workspace. Review all {totalItems} proposed items before applying.
                </p>
              </div>

              {!!draft.benefitPlans?.length && <section className="rounded-lg border border-slate-200 p-4 dark:border-white/10" aria-label={t('Benefits')}><h3 className="text-base font-semibold">{t('Benefits')}</h3><div className="mt-3 space-y-3">{draft.benefitPlans.map((plan, i) => <div key={i} className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 py-3 dark:border-white/10"><div><p className="text-sm font-semibold"><bdi>{plan.name}</bdi></p><p className="text-xs text-slate-500"><bdi>{plan.code}</bdi> · <bdi>{plan.currency}</bdi> · <bdi>{plan.effectiveFrom}</bdi>{plan.effectiveTo && <> – <bdi>{plan.effectiveTo}</bdi></>} · <bdi>{plan.gradeCodes.join(', ') || t('All grades')}</bdi></p></div><button type="button" className="btn-secondary" onClick={() => removeAt('benefitPlans', i)}>{t('Remove benefit')}</button></div>)}</div></section>}

              {/* Provenance: how the draft was generated (surfaces engine + genNotes, incl. the deterministic-template note) */}
              <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex flex-wrap items-center gap-2">
                  <Info className="h-4 w-4 text-slate-500 dark:text-slate-400" />
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">How this draft was generated</p>
                  {engine && <span className="rounded-full bg-sapphire/10 px-2.5 py-1 text-xs font-medium text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent">{engine}</span>}
                </div>
                {genNotes.length > 0 && (
                  <div className="mt-3 space-y-2">
                    {genNotes.map((n, i) => (
                      <p key={i} className="flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-200">
                        <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />{n}
                      </p>
                    ))}
                  </div>
                )}
              </div>

              <DraftSection title="Branches"
                rows={draft.branches.map((x, i) => ({
                  code: x.code,
                  desc: `${x.nameEn} · ${x.city}${x.isHeadOffice ? ' · Head office' : ''}`,
                  fields: [
                    txt('Name', x.nameEn, v => patch('branches', i, { nameEn: v })),
                    txt('City', x.city, v => patch('branches', i, { city: v })),
                    boolf('Head office', x.isHeadOffice, v => patch('branches', i, { isHeadOffice: v })),
                  ],
                }))}
                onRemove={i => removeAt('branches', i)} />

              <DraftSection title="Departments"
                rows={draft.departments.map((x, i) => ({
                  code: x.code, desc: x.nameEn,
                  fields: [txt('Name', x.nameEn, v => patch('departments', i, { nameEn: v }))],
                }))}
                onRemove={i => removeAt('departments', i)} />

              <DraftSection title="Cost Centers"
                rows={draft.costCenters.map((x, i) => ({
                  code: x.code,
                  desc: `${x.name}${x.departmentCode ? ` · ${x.departmentCode}` : ''}`,
                  fields: [txt('Name', x.name, v => patch('costCenters', i, { name: v }))],
                }))}
                onRemove={i => removeAt('costCenters', i)} />

              <DraftSection title="Designations"
                rows={draft.designations.map((x, i) => ({
                  code: x.code,
                  desc: `${x.titleEn}${x.departmentCode ? ` · ${x.departmentCode}` : ''}${x.gradeCode ? ` · ${x.gradeCode}` : ''}${x.isManagerRole ? ' · Manager' : ''}`,
                  fields: [
                    txt('Title', x.titleEn, v => patch('designations', i, { titleEn: v })),
                    selectf('Grade', x.gradeCode, draft.grades.map(g => g.code), v => patch('designations', i, { gradeCode: v })),
                    boolf('Manager role', x.isManagerRole, v => patch('designations', i, { isManagerRole: v })),
                  ],
                }))}
                onRemove={i => removeAt('designations', i)} />

              <DraftSection title="Grades"
                rows={draft.grades.map((x, i) => ({
                  code: x.code,
                  desc: `${x.name} (L${x.level}) · ${x.currency} ${x.minSalary}-${x.maxSalary}`,
                  // The currency is shown but not editable: it is the workspace's, set once at the top,
                  // and letting a single band drift to another currency is the bug this screen just fixed.
                  fields: [
                    txt('Name', x.name, v => patch('grades', i, { name: v })),
                    numf(`Min (${x.currency})`, x.minSalary, v => patch('grades', i, { minSalary: v })),
                    numf(`Mid (${x.currency})`, x.midSalary, v => patch('grades', i, { midSalary: v })),
                    numf(`Max (${x.currency})`, x.maxSalary, v => patch('grades', i, { maxSalary: v })),
                  ],
                }))}
                onRemove={i => removeAt('grades', i)} />

              {releaseA
                ? <p className="rounded-xl border border-slate-200 p-3 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">
                  {t('Grade allowances and benefits are set in Benefits by grade, so this draft has no grade pay lines.')}{' '}
                  <Link href="/benefits/by-grade" className="text-sapphire underline">{t('Open Benefits by grade')}</Link>
                </p>
                : <DraftSection title="Grade Pay Components"
                  rows={draft.gradePayComponents.map((x, i) => ({
                    code: x.componentCode,
                    desc: `${x.gradeCode} · ${x.componentName} · ${x.calculationType === 'PercentOfBasic' ? `${x.percentage}%` : x.amount}`,
                    fields: [
                      txt('Name', x.componentName, v => patch('gradePayComponents', i, { componentName: v })),
                      ...(x.calculationType === 'PercentOfBasic'
                        ? [numf('Percent of basic', x.percentage, v => patch('gradePayComponents', i, { percentage: v }), 0, 100)]
                        : [numf('Amount', x.amount, v => patch('gradePayComponents', i, { amount: v }))]),
                      boolf('Taxable', x.isTaxable, v => patch('gradePayComponents', i, { isTaxable: v })),
                    ],
                  }))}
                  onRemove={i => removeAt('gradePayComponents', i)} />}

              <DraftSection title="Leave Types"
                rows={draft.leaveTypes.map((x, i) => ({
                  code: x.code,
                  desc: `${x.nameEn} · ${x.isPaid ? 'Paid' : 'Unpaid'} · max ${x.maxConsecutiveDays}d`,
                  fields: [
                    txt('Name', x.nameEn, v => patch('leaveTypes', i, { nameEn: v })),
                    numf('Max consecutive days', x.maxConsecutiveDays, v => patch('leaveTypes', i, { maxConsecutiveDays: v }), 0, 365),
                    boolf('Paid', x.isPaid, v => patch('leaveTypes', i, { isPaid: v })),
                    boolf('Attachment required', x.requiresAttachment, v => patch('leaveTypes', i, { requiresAttachment: v })),
                  ],
                }))}
                onRemove={i => removeAt('leaveTypes', i)} />

              <DraftSection title="Leave Entitlement"
                rows={draft.leavePolicies.map((x, i) => ({
                  code: x.leaveTypeCode,
                  desc: `${x.name} · ${[x.gradeCode, x.departmentCode, x.employmentType].filter(Boolean).join(' / ') || t('All employees in this company')} · ${x.proratePartialMonths && x.accrualMethod === 'Monthly' ? t('Partial months prorated') + ' · ' : ''}${x.annualEntitlementDays} day(s)/year · ${x.accrualMethod === 'Monthly' ? 'accrues monthly' : 'granted yearly'}` +
                    ` · ${x.payrollImpact === 'Unpaid' ? 'unpaid' : 'full pay'}` +
                    `${x.noticeRequiredDays > 0 ? ` · ${x.noticeRequiredDays}d notice` : ''}` +
                    `${x.appliesOnProbation ? ' · available on probation' : ''}`,
                  fields: [
                    txt(t('Policy name'), x.name, v => patch('leavePolicies', i, { name: v })),
                    txt(t('Grade code (optional)'), x.gradeCode ?? '', v => patch('leavePolicies', i, { gradeCode: v })),
                    txt(t('Department code (optional)'), x.departmentCode ?? '', v => patch('leavePolicies', i, { departmentCode: v })),
                    txt(t('Employment type (optional)'), x.employmentType ?? '', v => patch('leavePolicies', i, { employmentType: v })),
                    ...(x.accrualMethod === 'Monthly' ? [boolf(t('Prorate partial months by calendar days employed'), x.proratePartialMonths ?? false, v => patch('leavePolicies', i, { proratePartialMonths: v }))] : []),
                    numf('Days per year', x.annualEntitlementDays, v => patch('leavePolicies', i, { annualEntitlementDays: v }), 0, 365, 0.5),
                    selectf('Accrual', x.accrualMethod, ['Yearly', 'Monthly'], v => patch('leavePolicies', i, { accrualMethod: v, proratePartialMonths: v === 'Monthly' && x.proratePartialMonths })),
                    numf('Notice days', x.noticeRequiredDays, v => patch('leavePolicies', i, { noticeRequiredDays: v }), 0, 365),
                    numf('Max per request', x.maximumDaysPerRequest, v => patch('leavePolicies', i, { maximumDaysPerRequest: v }), 0, 365),
                    boolf('Encashable', x.encashmentAllowed, v => patch('leavePolicies', i, { encashmentAllowed: v })),
                    boolf('On probation', x.appliesOnProbation, v => patch('leavePolicies', i, { appliesOnProbation: v })),
                  ],
                }))}
                onRemove={i => removeAt('leavePolicies', i)} />

              <DraftSection title="Shifts"
                rows={draft.shifts.map((x, i) => ({
                  code: x.code,
                  desc: `${x.name} · ${x.start}–${x.end}`,
                  fields: [
                    txt('Name', x.name, v => patch('shifts', i, { name: v })),
                    txt('Start (HH:mm)', x.start, v => patch('shifts', i, { start: v })),
                    txt('End (HH:mm)', x.end, v => patch('shifts', i, { end: v })),
                    numf('Break minutes', x.breakMinutes, v => patch('shifts', i, { breakMinutes: v }), 0, 240),
                  ],
                }))}
                onRemove={i => removeAt('shifts', i)} />

              {draft.workingWeek && (
                <DraftSection title="Working Week"
                  rows={[{
                    code: 'WEEK',
                    desc: `${draft.workingWeek.workWeek} · starts ${draft.workingWeek.weekStartDay}`,
                    fields: [
                      selectf('Working week', draft.workingWeek.workWeek, ['Sun-Thu', 'Mon-Fri', 'Mon-Sat', 'Sat-Thu'], v => patchOne('workingWeek', { workWeek: v })),
                      selectf('Week starts', draft.workingWeek.weekStartDay, ['Sunday', 'Monday', 'Saturday'], v => patchOne('workingWeek', { weekStartDay: v })),
                    ],
                  }]}
                  onRemove={() => setDraft(d => d ? { ...d, workingWeek: null } : d)} />
              )}

              <DraftSection title="Payroll Components"
                rows={draft.payComponents.map((x, i) => ({
                  code: x.code,
                  desc: `${x.name} · ${x.componentType} · ${x.calculationType === 'Percentage' ? `${x.percentage}%` : x.amount}`,
                  fields: [
                    txt('Name', x.name, v => patch('payComponents', i, { name: v })),
                    ...(x.calculationType === 'Percentage'
                      ? [numf('Percentage', x.percentage, v => patch('payComponents', i, { percentage: v }), 0, 100)]
                      : [numf('Amount', x.amount, v => patch('payComponents', i, { amount: v }))]),
                    boolf('Taxable', x.isTaxable, v => patch('payComponents', i, { isTaxable: v })),
                  ],
                }))}
                onRemove={i => removeAt('payComponents', i)} />

              <DraftSection title="Statutory Rules"
                rows={draft.statutoryRules.map((x, i) => ({
                  code: x.ruleKey,
                  desc: `${x.ruleValue} — ${x.description}`,
                  // The VALUE is editable; the key is not. A renamed key is a rule nothing reads.
                  fields: [txt('Value', x.ruleValue, v => patch('statutoryRules', i, { ruleValue: v }))],
                }))}
                onRemove={i => removeAt('statutoryRules', i)} />

              {draft.employeeIdRule && (
                <DraftSection title="Employee ID Rule"
                  rows={[{
                    code: 'ID',
                    desc: `${draft.employeeIdRule.companyPrefix} · pad ${draft.employeeIdRule.paddingLength} · next ${draft.employeeIdRule.nextSequence}`,
                    fields: [
                      txt('Prefix', draft.employeeIdRule.companyPrefix, v => patchOne('employeeIdRule', { companyPrefix: v })),
                      numf('Padding', draft.employeeIdRule.paddingLength, v => patchOne('employeeIdRule', { paddingLength: v }), 1, 12),
                      numf('Next sequence', draft.employeeIdRule.nextSequence, v => patchOne('employeeIdRule', { nextSequence: v }), 1),
                      boolf('Allow manual override', draft.employeeIdRule.allowManualOverride, v => patchOne('employeeIdRule', { allowManualOverride: v })),
                    ],
                  }]}
                  onRemove={() => setDraft(d => d ? { ...d, employeeIdRule: null } : d)} />
              )}

              {draft.attendancePolicy && (
                <DraftSection title="Attendance Policy"
                  rows={[{
                    code: draft.attendancePolicy.code,
                    desc: `${draft.attendancePolicy.graceMinutes}min grace · late after ${draft.attendancePolicy.lateThresholdMinutes}min` +
                      ` · ${Math.round(draft.attendancePolicy.standardWorkMinutes / 60 * 10) / 10}h day` +
                      ` · ${draft.attendancePolicy.breakMinutes}min break` +
                      ` · rounded to the ${draft.attendancePolicy.roundingRule === 'NearestMinute' ? 'minute' : 'quarter-hour'}`,
                    fields: [
                      numf('Grace minutes', draft.attendancePolicy.graceMinutes, v => patchOne('attendancePolicy', { graceMinutes: v }), 0, 120),
                      numf('Late after (min)', draft.attendancePolicy.lateThresholdMinutes, v => patchOne('attendancePolicy', { lateThresholdMinutes: v }), 0, 480),
                      numf('Standard day (min)', draft.attendancePolicy.standardWorkMinutes, v => patchOne('attendancePolicy', { standardWorkMinutes: v }), 60, 960),
                      numf('Break (min)', draft.attendancePolicy.breakMinutes, v => patchOne('attendancePolicy', { breakMinutes: v }), 0, 240),
                      // Only the two the overtime engine can evaluate; a third would be stored and ignored.
                      selectf('Rounding', draft.attendancePolicy.roundingRule, ['NearestMinute', 'Nearest15'], v => patchOne('attendancePolicy', { roundingRule: v })),
                    ],
                  }]}
                  onRemove={() => setDraft(d => d ? { ...d, attendancePolicy: null } : d)} />
              )}

              {draft.overtimePolicy && (
                <DraftSection title="Overtime Policy"
                  rows={[
                    {
                      code: draft.overtimePolicy.code,
                      desc: `${draft.overtimePolicy.standardMonthlyHours}h/month basis · min ${draft.overtimePolicy.minimumMinutes}min` +
                        ` · max ${Math.round(draft.overtimePolicy.maximumMinutesPerDay / 60)}h/day` +
                        `${draft.overtimePolicy.allowCompOffConversion ? ' · time off in lieu allowed' : ''}`,
                      fields: [
                        numf('Monthly hours basis', draft.overtimePolicy.standardMonthlyHours, v => patchOne('overtimePolicy', { standardMonthlyHours: v }), 1, 400),
                        numf('Minimum minutes', draft.overtimePolicy.minimumMinutes, v => patchOne('overtimePolicy', { minimumMinutes: v }), 0, 480),
                        numf('Max minutes/day', draft.overtimePolicy.maximumMinutesPerDay, v => patchOne('overtimePolicy', { maximumMinutesPerDay: v }), 0, 960),
                        boolf('Time off in lieu', draft.overtimePolicy.allowCompOffConversion, v => patchOne('overtimePolicy', { allowCompOffConversion: v })),
                      ],
                    },
                    ...draft.overtimePolicy.multipliers.map((m, mi) => ({
                      code: m.dayCategory,
                      desc: `×${m.multiplier} of the hourly rate`,
                      // Floored at 1: below that an overtime hour pays less than an ordinary one, and
                      // the payroll run would floor it at the statutory rate anyway.
                      fields: [numf('Multiplier', m.multiplier, v => setDraft(d => d?.overtimePolicy
                        ? { ...d, overtimePolicy: { ...d.overtimePolicy, multipliers: d.overtimePolicy.multipliers.map((x, n) => n === mi ? { ...x, multiplier: v } : x) } }
                        : d), 1, 5, 0.25)],
                    })),
                  ]}
                  onRemove={i => setDraft(d => {
                    if (!d?.overtimePolicy) return d;
                    if (i === 0) return { ...d, overtimePolicy: null };
                    return { ...d, overtimePolicy: { ...d.overtimePolicy, multipliers: d.overtimePolicy.multipliers.filter((_, n) => n !== i - 1) } };
                  })} />
              )}

              {draft.holidayCalendar && (
                <DraftSection title={`Public Holidays ${draft.holidayCalendar.calendarYear}`}
                  rows={draft.holidayCalendar.holidays.map((h, i) => ({
                    code: h.date,
                    desc: `${h.nameEn}${h.nameAr ? ` · ${h.nameAr}` : ''}${h.isOptional ? ' · optional' : ''}`,
                    fields: [
                      txt('Name', h.nameEn, v => setDraft(d => d?.holidayCalendar
                        ? { ...d, holidayCalendar: { ...d.holidayCalendar, holidays: d.holidayCalendar.holidays.map((x, n) => n === i ? { ...x, nameEn: v } : x) } } : d)),
                      txt('Date (YYYY-MM-DD)', h.date, v => setDraft(d => d?.holidayCalendar
                        ? { ...d, holidayCalendar: { ...d.holidayCalendar, holidays: d.holidayCalendar.holidays.map((x, n) => n === i ? { ...x, date: v } : x) } } : d)),
                      boolf('Optional', h.isOptional, v => setDraft(d => d?.holidayCalendar
                        ? { ...d, holidayCalendar: { ...d.holidayCalendar, holidays: d.holidayCalendar.holidays.map((x, n) => n === i ? { ...x, isOptional: v } : x) } } : d)),
                    ],
                  }))}
                  onRemove={i => setDraft(d => d && d.holidayCalendar
                    ? { ...d, holidayCalendar: { ...d.holidayCalendar, holidays: d.holidayCalendar.holidays.filter((_, n) => n !== i) } }
                    : d)} />
              )}

              {draft.localization && (
                <DraftSection title="Language & Time Zone"
                  rows={[{
                    code: 'LOCALE',
                    desc: `${draft.localization.defaultLanguage === 'ar' ? 'Arabic' : 'English'} · ${draft.localization.defaultTimezone}` +
                      ` · ${draft.localization.dateFormat}` +
                      `${draft.localization.rtlEnabled ? ' · right-to-left supported' : ''}` +
                      `${draft.localization.hijriDatesEnabled ? ' · Hijri dates shown' : ''}`,
                    fields: [
                      selectf('Language', draft.localization.defaultLanguage, ['en', 'ar'], v => patchOne('localization', { defaultLanguage: v })),
                      selectf('Time zone', draft.localization.defaultTimezone, TIMEZONES.filter(Boolean), v => patchOne('localization', { defaultTimezone: v })),
                      selectf('Date format', draft.localization.dateFormat, ['DD/MM/YYYY', 'MM/DD/YYYY', 'YYYY-MM-DD'], v => patchOne('localization', { dateFormat: v })),
                      boolf('Right-to-left', draft.localization.rtlEnabled, v => patchOne('localization', { rtlEnabled: v })),
                      boolf('Show Hijri dates', draft.localization.hijriDatesEnabled, v => patchOne('localization', { hijriDatesEnabled: v })),
                    ],
                  }]}
                  onRemove={() => setDraft(d => d ? { ...d, localization: null } : d)} />
              )}

              {draft.hrConfig && (
                <DraftSection title="Saved governance preferences"
                  rows={[{
                    code: 'GOV',
                    desc: 'Planning preferences only. Operational approval workflows, payroll checks and access rules are configured separately.',
                    fields: [
                      boolf('Preview before import', draft.hrConfig.requireImportPreviewBeforeCommit, v => patchOne('hrConfig', { requireImportPreviewBeforeCommit: v })),
                      boolf('Cost center for payroll', draft.hrConfig.requireCostCenterForPayroll, v => patchOne('hrConfig', { requireCostCenterForPayroll: v })),
                      boolf('Grade for approval policy', draft.hrConfig.requireGradeForApprovalPolicy, v => patchOne('hrConfig', { requireGradeForApprovalPolicy: v })),
                      boolf('Dept head approval', draft.hrConfig.useDeptHeadApproval, v => patchOne('hrConfig', { useDeptHeadApproval: v })),
                      boolf('HR final approval', draft.hrConfig.useHrFinalApproval, v => patchOne('hrConfig', { useHrFinalApproval: v })),
                    ],
                  }]}
                  onRemove={() => setDraft(d => d ? { ...d, hrConfig: null } : d)} />
              )}
            </fieldset>
          )}
          <div className={`${step === 0 && policyGuideOpen ? 'hidden' : 'flex'} mt-4 flex-wrap items-center justify-between gap-3 border-t border-slate-200 pt-4 xl:pe-40 dark:border-white/10`}>
            {step > 0 ? <button type="button" className="btn-secondary" onClick={() => goToStep(step - 1)} disabled={loading || applying}><ArrowLeft className="h-4 w-4 rtl:rotate-180" />Back</button> : <span className="text-xs text-slate-500 dark:text-slate-400">{t('Step 1 of 5')}</span>}
            {step < 4 ? <button type="button" className="btn-primary" onClick={() => goToStep(step + 1)}>Continue<ArrowRight className="h-4 w-4 rtl:rotate-180" /></button> : (
              <div className="flex flex-wrap items-center gap-3">
                <button type="button" className={draft ? 'btn-secondary' : 'btn-primary'} onClick={generate}
                  disabled={loading || applying || selectedCount === 0 || !legalEntityName.trim() || !industry.trim() || !country || !currency}>
                  <Wand2 className="h-4 w-4" />{loading ? 'Generating draft…' : draft ? 'Regenerate draft' : 'Generate draft'}
                </button>
                {draft && <button type="button" className="btn-primary" onClick={apply} disabled={applying || loading || totalItems === 0}>
                  <CheckCircle2 className="h-4 w-4" />{applying ? 'Applying…' : `Apply ${totalItems} item(s) to workspace`}
                </button>}
              </div>
            )}
          </div>
          {step === 4 && <p role="status" className="mt-3 text-xs text-slate-600 dark:text-slate-400">{selectedCount === 0 ? 'Select at least one section to generate a draft.' : draft && totalItems === 0 ? 'There are no items left to apply. Regenerate your draft to start again.' : 'Your workspace changes only when you choose Apply.'}</p>}

        </div>
      </div>
    </div>
  );
}

/**
 * One editable value on a draft row. The draft is a PROPOSAL, so every figure in it has to be
 * changeable before it is written — a starter configuration that can only be accepted or deleted
 * wholesale forces a customer to apply numbers they disagree with and then go and correct them in
 * eight different screens.
 */
type EditField =
  | { kind: 'text'; label: string; value: string; onChange: (v: string) => void; }
  | { kind: 'number'; label: string; value: number; onChange: (v: number) => void; min?: number; max?: number; step?: number; }
  | { kind: 'bool'; label: string; value: boolean; onChange: (v: boolean) => void; }
  | { kind: 'select'; label: string; value: string; options: string[]; onChange: (v: string) => void; };

type DraftRow = { code: string; desc: string; fields?: EditField[]; };

const txt = (label: string, value: string, onChange: (v: string) => void): EditField =>
  ({ kind: 'text', label, value, onChange });
const numf = (label: string, value: number, onChange: (v: number) => void, min = 0, max = 1_000_000_000, step = 1): EditField =>
  ({ kind: 'number', label, value, onChange, min, max, step });
const boolf = (label: string, value: boolean, onChange: (v: boolean) => void): EditField =>
  ({ kind: 'bool', label, value, onChange });
const selectf = (label: string, value: string, options: string[], onChange: (v: string) => void): EditField =>
  ({ kind: 'select', label, value, options, onChange });

function FieldInput({ field }: { field: EditField; }) {
  const base = 'w-full rounded-md border border-slate-200 bg-white px-2 py-1 text-xs text-slate-800 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-100';
  if (field.kind === 'bool') {
    return (
      <label className="flex items-center gap-2 text-xs text-slate-700 dark:text-slate-300">
        <input type="checkbox" className="h-3.5 w-3.5 accent-sapphire" checked={field.value}
          onChange={e => field.onChange(e.target.checked)} />
        {field.label}
      </label>
    );
  }
  return (
    <label className="block">
      <span className="mb-0.5 block text-xs font-medium text-slate-600 dark:text-slate-400">{field.label}</span>
      {field.kind === 'select' ? (
        <select className={base} value={field.value} onChange={e => field.onChange(e.target.value)}>
          {field.options.map(o => <option key={o} value={o}>{o}</option>)}
        </select>
      ) : field.kind === 'number' ? (
        <input type="number" className={base} value={field.value} min={field.min} max={field.max} step={field.step}
          // Empty and partial input must not become NaN mid-typing, which would blank the row.
          onChange={e => field.onChange(Number.isFinite(e.target.valueAsNumber) ? e.target.valueAsNumber : 0)} />
      ) : (
        <input type="text" className={base} value={field.value} onChange={e => field.onChange(e.target.value)} />
      )}
    </label>
  );
}

function DraftSection({ title, rows, onRemove }: { title: string; rows: DraftRow[]; onRemove: (idx: number) => void; }) {
  // Which rows are open for editing. Collapsed by default: 65 rows of input boxes is not a review
  // screen, it is a form. The summary stays readable and the fields appear on request.
  const [open, setOpen] = useState<Record<number, boolean>>({});
  if (rows.length === 0) return null;
  return (
    <div className="rounded-xl border border-slate-200 dark:border-white/10">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-2.5 dark:border-white/[0.06]">
        <h4 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h4>
        <span className="text-xs text-slate-400">{rows.length}</span>
      </div>
      <ul className="divide-y divide-slate-50 dark:divide-white/[0.04]">
        {rows.map((row, i) => (
          <li key={`${row.code}-${i}`} className="px-4 py-2 text-sm">
            <div className="flex flex-wrap items-center gap-3">
              <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{row.code}</span>
              <span className="text-slate-700 dark:text-slate-200">{row.desc}</span>
              <div className="ms-auto flex items-center gap-1">
                {row.fields && row.fields.length > 0 && (
                  <button type="button" aria-label={`Edit ${row.code}`} aria-expanded={Boolean(open[i])}
                    onClick={() => setOpen(o => ({ ...o, [i]: !o[i] }))}
                    className={`grid h-9 w-9 shrink-0 place-items-center rounded ${open[i] ? 'bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent' : 'text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-white/10'}`}>
                    <Pencil className="h-3.5 w-3.5" />
                  </button>
                )}
                <button type="button" aria-label={`Remove ${row.code}`} onClick={() => onRemove(i)}
                  className="grid h-9 w-9 shrink-0 place-items-center rounded text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </div>
            {open[i] && row.fields && (
              <div className="mt-2 grid gap-2 rounded-lg bg-slate-50 p-3 dark:bg-white/[0.03] sm:grid-cols-3">
                {row.fields.map((f, n) => <FieldInput key={n} field={f} />)}
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
