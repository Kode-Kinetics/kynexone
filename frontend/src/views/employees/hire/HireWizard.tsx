'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { flushSync } from 'react-dom';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useNavigationGuard } from '@/src/contexts/NavigationGuardContext';
import { useTenantSettings } from '@/src/contexts/TenantSettingsContext';
import { branchesApi, companiesApi, departmentsApi, designationsApi, gradesApi } from '@/src/api/organization';
import { employeesApi } from '@/src/api/employees';
import { employeeFieldCatalogApi } from '@/src/api/employeeFieldCatalog';
import { employeeHireApi, type ApiNationality } from '@/src/api/employeeHire';
import { EMPTY_EMPLOYEE_REFERENCE_LISTS, loadEmployeeReferenceLists } from '@/src/hooks/useReferenceLists';
import { createAutosave, type AutosaveState } from '@/src/lib/hire/autosave';
import { dateLocale } from '@/src/lib/format';
import { DEFAULT_ZONE, todayInZone } from '@/src/lib/hire/dates';
import { emptyHireForm, hasContent, type HireForm } from '@/src/lib/hire/hireForm';
import { stepOf, validateHire, type FieldIssue } from '@/src/lib/hire/hireValidation';
import { draftLoadProblem, formFromEmployee, isResumable } from '@/src/lib/hire/draftHydrate';
import type { SaBank } from '@/src/lib/hire/iban';
import { defaultIdType, type CountryOption } from '@/src/lib/hire/nationality';
import { buildCreateRequest, newIdempotencyKey, type AcknowledgedWarning } from '@/src/lib/hire/payload';
import { parseHireProblem, serverFieldIssue, type ServerWarning } from '@/src/lib/hire/problem';
import { serverFieldToForm } from '@/src/lib/hire/serverFields';
import { ErrorSummary } from './fields/ErrorSummary';
import { DiscardDialog, WarningAckDialog } from './fields/Dialogs';
import { DuplicatePanel, type MatchCard } from './fields/DuplicatePanel';
import { fieldId } from './fields/FieldShell';
import { HireBasicsStep } from './steps/HireBasicsStep';
import type { AppliedTemplate } from './TemplatePicker';
import { planTemplate, planUndo } from '@/src/lib/hire/hireTemplate';
import type { HireTemplate } from '@/src/api/onboardingTemplates';
import { JobPayDocsStep } from './steps/JobPayDocsStep';
import { PersonContactStep } from './steps/PersonContactStep';
import { ReviewStep } from './steps/ReviewStep';
import { SuccessStep } from './steps/SuccessStep';
import { issueText } from './issueText';
import type { Lookups, StepProps } from './types';
import { normalizeCountryCode } from '@/src/api/employeeFieldCatalog';

const STEP_LABELS = ['Hire basics', 'Person and contact', 'Job, pay and documents', 'Review and create'] as const;
type Step = 1 | 2 | 3 | 4;
const ID_KEY: Record<string, string> = { nationalId: 'id_number', iqama: 'iqama_number', passport: 'passport_number', gccId: 'civil_id' };

const EMPTY_LOOKUPS: Lookups = { companies: [], branches: [], departments: [], designations: [], grades: [], managers: [], refs: EMPTY_EMPLOYEE_REFERENCE_LISTS };

type Notice = { key: string; params?: Record<string, string>; action?: 'reload' } | null;

function clock(iso: string, zone: string, locale: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return new Intl.DateTimeFormat(dateLocale(locale), { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: zone }).format(d);
}

export function HireWizard() {
  const { t, locale } = useLocale();
  const router = useRouter();
  const guard = useNavigationGuard();
  const { hasPermission } = useAuth();
  const { selectedCompanyId } = useCompany();
  const tenant = useTenantSettings();
  const zone = tenant.defaultTimezone?.trim() || DEFAULT_ZONE;
  const today = todayInZone(zone);

  const [lookups, setLookups] = useState<Lookups>(EMPTY_LOOKUPS);
  const [loaded, setLoaded] = useState(false);
  const [loadFailed, setLoadFailed] = useState(false);
  const [countryOptions, setCountryOptions] = useState<CountryOption[] | undefined>(undefined);
  const [form, setForm] = useState<HireForm>(emptyHireForm);
  const initial = useRef<HireForm>(form);
  const [step, setStep] = useState<Step>(1);
  const [touched, setTouched] = useState<ReadonlySet<keyof HireForm>>(new Set());
  const [summaryFields, setSummaryFields] = useState<Array<keyof HireForm>>([]);
  const [serverIssues, setServerIssues] = useState<FieldIssue[]>([]);
  const [resident, setResident] = useState(true);
  const [notice, setNotice] = useState<Notice>(null);
  const [draft, setDraft] = useState<{ id: number; name: string } | null>(null);
  const [created, setCreated] = useState<{ id: number; name: string; rehired?: boolean } | null>(null);
  const [banks, setBanks] = useState<SaBank[] | null>(null);
  // CD26: /people/new?draft=<id> resumes a saved draft. 'idle' = a normal new hire.
  const [resume, setResume] = useState<{ state: 'idle' | 'loading' | 'unavailable' | 'network' | 'notDraft'; id?: number }>({ state: 'idle' });
  const [savedForm, setSavedForm] = useState<HireForm | null>(null);
  const [saveState, setSaveState] = useState<AutosaveState>('idle');
  const [savedAt, setSavedAt] = useState('');
  const [busy, setBusy] = useState(false);
  const [warnings, setWarnings] = useState<ServerWarning[] | null>(null);
  const [acks, setAcks] = useState<AcknowledgedWarning[]>([]);
  const [matches, setMatches] = useState<MatchCard[]>([]);
  const [differentReason, setDifferentReason] = useState<string | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const [leaving, setLeaving] = useState(false);
  const [, setTick] = useState(0);
  // P3: the hire template applied on step 1, and the fields the person has filled themselves (a template never overwrites those).
  const [applied, setApplied] = useState<AppliedTemplate | null>(null);
  const editedRef = useRef<Set<keyof HireForm>>(new Set());

  const busyRef = useRef(false);
  const keyRef = useRef(newIdempotencyKey());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const summaryRef = useRef<HTMLDivElement>(null);
  const pending = useRef<null | { kind: 'summary' } | { kind: 'field'; field: keyof HireForm } | { kind: 'heading' }>(null);
  const lastProbe = useRef('');

  const company = lookups.companies.find(c => c.id === form.companyId);
  const companyCountry = normalizeCountryCode(company?.countryCode) || 'SA';
  const currency = company?.defaultCurrency || 'SAR';
  const activeCompanies = lookups.companies.filter(c => c.isActive);
  const singleCompany = activeCompanies.length === 1;

  // ── data ────────────────────────────────────────────────────────────────
  useEffect(() => {
    let cancelled = false;
    const draftParam = new URLSearchParams(window.location.search).get('draft');
    const resumeId = draftParam && /^\d+$/.test(draftParam) ? Number(draftParam) : null;
    if (resumeId !== null) setResume({ state: 'loading', id: resumeId });
    Promise.all([
      companiesApi.listAll(), branchesApi.listAll(), departmentsApi.listAll(), designationsApi.listAll(), gradesApi.listAll(),
      employeesApi.listAll({ status: 'Active' }).catch(() => []), loadEmployeeReferenceLists(),
    ]).then(async ([companies, branches, departments, designations, grades, managers, refs]) => {
      if (cancelled) return;
      setLookups({ companies, branches, departments, designations, grades, managers, refs });
      if (resumeId !== null) {
        try {
          const d = await employeeHireApi.loadDraft(resumeId);
          if (cancelled) return;
          if (!isResumable(d, resumeId)) { setResume({ state: 'notDraft', id: resumeId }); }
          else {
            const co = companies.find(c => c.id === d.companyId);
            const hydrated = formFromEmployee(d, { emailDomain: co?.emailDomain || undefined, companyCountry: normalizeCountryCode(co?.countryCode) || 'SA' });
            initial.current = hydrated;
            setForm(hydrated);
            setSavedForm(hydrated);
            setDraft({ id: d.id, name: d.englishName || d.fullName || hydrated.englishName });
            autosave.current!.adopt(d.updatedAtUtc ?? '');
            setResume({ state: 'idle' });
          }
        } catch (e) { if (!cancelled) setResume({ state: draftLoadProblem(e), id: resumeId }); }
      } else {
        const open = companies.filter(c => c.isActive);
        const pick = open.length === 1 ? open[0].id : open.find(c => c.id === selectedCompanyId)?.id ?? '';
        if (pick) { initial.current = { ...initial.current, companyId: pick }; setForm(f => ({ ...f, companyId: f.companyId || pick })); }
      }
      setLoaded(true);
    }).catch(() => { if (!cancelled) { setLoadFailed(true); setLoaded(true); } });
    employeeHireApi.saBanks().then(list => { if (!cancelled && list) setBanks(list); });
    employeeHireApi.nationalities().then(list => {
      if (!cancelled && list?.length) setCountryOptions(list.map((n: ApiNationality) => ({ code: n.code, nameEn: n.nameEn, nameAr: n.nameAr })));
    });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ── validation ──────────────────────────────────────────────────────────
  const designationGrade = useMemo(() => {
    const g = lookups.designations.find(d => d.id === form.designationId)?.gradeId;
    const grade = g ? lookups.grades.find(x => x.id === g) : undefined;
    return grade ? { gradeId: grade.id, gradeCode: grade.code } : undefined;
  }, [lookups, form.designationId]);
  const ctx = useMemo(() => ({ today, emailDomain: company?.emailDomain || undefined, companyCountry, designationGrade }), [today, company, companyCountry, designationGrade]);
  const issues = useMemo(() => {
    const local = validateHire(form, ctx, 'create');
    return [...local, ...serverIssues.filter(s => !local.some(l => l.field === s.field))];
  }, [form, ctx, serverIssues]);

  const errorFor = useCallback((field: keyof HireForm) => {
    if (!touched.has(field) && !summaryFields.includes(field)) return undefined;
    const issue = issues.find(i => i.field === field);
    return issue ? issueText(issue, t, locale) : undefined;
  }, [issues, touched, summaryFields, t, locale]);

  const blur = useCallback((field: keyof HireForm) => setTouched(prev => (prev.has(field) ? prev : new Set(prev).add(field))), []);
  const set = useCallback(<K extends keyof HireForm>(key: K, value: HireForm[K]) => {
    editedRef.current.add(key);
    setForm(f => ({ ...f, [key]: value }));
    setServerIssues(s => (s.some(i => i.field === key) ? s.filter(i => i.field !== key) : s));
  }, []);

  const onApplyTemplate = useCallback((template: HireTemplate) => {
    const department = lookups.departments.find(d => d.id === (form.departmentId || template.defaults.departmentId));
    const plan = planTemplate(form, editedRef.current, template.defaults, department?.managerEmployeeId ?? null);
    setForm(f => ({ ...f, ...plan.patch }));
    setApplied({ template, applied: plan.patch, prior: plan.prior, kept: plan.kept });
  }, [form, lookups.departments]);
  const onUndoTemplate = useCallback(() => {
    if (!applied) return;
    const restore = planUndo(form, applied.applied, applied.prior);
    setForm(f => ({ ...f, ...restore }));
    setApplied(null);
  }, [applied, form]);

  const shownSummary = issues.filter(i => summaryFields.includes(i.field));

  // ── focus management ────────────────────────────────────────────────────
  useEffect(() => {
    const target = pending.current;
    pending.current = null;
    if (!target) return;
    if (target.kind === 'summary') summaryRef.current?.focus();
    else if (target.kind === 'heading') headingRef.current?.focus();
    else {
      const el = document.getElementById(fieldId(target.field));
      el?.closest('details')?.setAttribute('open', '');
      el?.focus();
    }
  });
  const goTo = useCallback((next: Step, focus: 'heading' | 'none' = 'heading') => {
    setStep(next);
    if (focus === 'heading') pending.current = { kind: 'heading' };
  }, []);
  const focusIssue = useCallback((issue: FieldIssue) => {
    pending.current = { kind: 'field', field: issue.field };
    if (issue.step !== step) setStep(issue.step); else setTick(n => n + 1);
  }, [step]);

  // ── autosave ────────────────────────────────────────────────────────────
  const latest = useRef({ ctx, companyCountry, currency, emailDomain: undefined as string | undefined, acks, draft, hireTemplateId: undefined as string | undefined });
  latest.current = { ctx, companyCountry, currency, emailDomain: company?.emailDomain || undefined, acks, draft, hireTemplateId: applied?.template.id };
  const autosave = useRef<ReturnType<typeof createAutosave<{ form: HireForm }>> | null>(null);
  if (!autosave.current) {
    autosave.current = createAutosave<{ form: HireForm }>({
      delayMs: 1200,
      save: async ({ form: f }, base) => {
        const l = latest.current;
        const res = await employeeHireApi.saveDraft(l.draft!.id, base, buildCreateRequest(f, { companyCountry: l.companyCountry, currency: l.currency, emailDomain: l.emailDomain }, { acknowledgedWarnings: l.acks, hireTemplateId: l.hireTemplateId }));
        setSavedForm(f);
        return res;
      },
      onState: (s, at) => { setSaveState(s); if (at) setSavedAt(at); if (s === 'conflict') setNotice({ key: 'This draft was changed somewhere else. Reload to continue.', action: 'reload' }); },
    });
  }
  useEffect(() => () => autosave.current?.dispose(), []);
  useEffect(() => {
    if (!draft || !savedForm || form === savedForm) return;
    if (validateHire(form, ctx, 'draft').length) return;
    autosave.current!.markDirty({ form });
  }, [form, draft, savedForm, ctx]);

  // ── leave guard (refresh, back, sidebar) ───────────────────────────────
  const unsaved = !leaving && !created && (draft ? form !== savedForm : hasContent(form, initial.current));
  useEffect(() => guard.register({ id: 'hire-wizard', when: unsaved, message: t('Leave this page and discard unsaved changes?'), onDiscard: () => {} }), [guard, unsaved, t]);

  // ── duplicate pre-check (blur, never keystroke) ─────────────────────────
  const runDuplicateCheck = useCallback(async () => {
    const name = form.englishName.trim();
    if (name.split(/\s+/).length < 2 || !form.companyId) return;
    const idKey = form.idType ? ID_KEY[form.idType] : '';
    const identity = idKey && form.idNumber.trim() ? [{ fieldKey: idKey, value: form.idNumber.trim() }] : [];
    const probe = { englishName: name, companyId: form.companyId, dateOfBirth: form.dob || undefined, identityValues: identity, excludeEmployeeId: draft?.id };
    const sig = JSON.stringify(probe);
    if (sig === lastProbe.current) return;
    lastProbe.current = sig;
    try {
      const res = await employeesApi.duplicateCheck(probe);
      setMatches((res.matches ?? []) as MatchCard[]);
    } catch (e) {
      const p = parseHireProblem(e);
      if (p.kind === 'rate_limited') setNotice({ key: 'Too many checks. Try again in a moment.' });
    }
  }, [form.englishName, form.companyId, form.idType, form.idNumber, form.dob, draft?.id]);

  // ── saving ──────────────────────────────────────────────────────────────
  const showIssues = useCallback((list: FieldIssue[], focus: 'summary' | 'field') => {
    setTouched(prev => new Set([...prev, ...list.map(i => i.field)]));
    setSummaryFields(list.map(i => i.field));
    if (!list.length) return;
    if (focus === 'field' && list.length === 1) { pending.current = { kind: 'field', field: list[0].field }; if (list[0].step !== step) setStep(list[0].step); else setTick(n => n + 1); }
    else { pending.current = { kind: 'summary' }; setTick(n => n + 1); }
  }, [step]);

  const handleFailure = useCallback((e: unknown) => {
    const p = parseHireProblem(e);
    switch (p.kind) {
      case 'validation': {
        const list: FieldIssue[] = [];
        for (const [field, codes] of Object.entries(p.fields)) {
          const f = serverFieldToForm(field);
          if (f && codes[0]) { const i = serverFieldIssue(f, codes[0]); list.push({ field: f, step: stepOf(f), key: i.key, params: i.params }); }
        }
        setServerIssues(list);
        showIssues(list, 'summary');
        if (!list.length) setNotice({ key: 'Some details could not be saved. Check the form and try again.' });
        break;
      }
      case 'warnings': setWarnings(p.warnings); break;
      case 'duplicate': setMatches(p.matches as MatchCard[]); setNotice({ key: 'This may be a person who is already on file.' }); break;
      case 'rate_limited': setNotice({ key: 'Too many checks. Try again in a moment.' }); break;
      case 'idempotency_mismatch': keyRef.current = newIdempotencyKey(); setNotice({ key: 'This request was already used for different details. Reload and try again.' }); break;
      case 'override_reason_required': setNotice({ key: 'Enter a reason of at least 10 characters.' }); break;
      case 'override_not_permitted': setNotice({ key: 'You do not have permission to override this. Ask an HR Manager.' }); break;
      case 'invalid_reference': setNotice({ key: 'This selection is not available. Choose another.' }); break;
      case 'draft_conflict': setNotice({ key: 'This draft was changed somewhere else. Reload to continue.', action: 'reload' }); break;
      default: setNotice({ key: 'The employee could not be saved. Check your connection and try again.' });
    }
  }, [showIssues]);

  /** Create the draft row (once), or flush the autosave when it exists. Resolves true when the server holds what is on screen. */
  const persist = useCallback(async (extraAcks?: AcknowledgedWarning[]): Promise<number | null> => {
    if (busyRef.current) return null;
    busyRef.current = true; setBusy(true); setNotice(null);
    try {
      if (draft) return (await autosave.current!.flush()) ? draft.id : null;
      const all = extraAcks ?? acks;
      const body = buildCreateRequest(form, { companyCountry, currency, emailDomain: company?.emailDomain || undefined }, { acknowledgedWarnings: all, duplicateOverrideReason: differentReason ?? undefined, hireTemplateId: applied?.template.id });
      const res = await employeeHireApi.create(body, keyRef.current);
      const version = res.updatedAtUtc ?? (res as { createdAtUtc?: string }).createdAtUtc ?? '';
      flushSync(() => { setDraft({ id: res.id, name: res.englishName || res.fullName || form.englishName.trim() }); setSavedForm(form); setAcks(all); });
      autosave.current!.adopt(version);
      // A refresh now resumes this draft instead of starting a blank form (and creating a second record). No navigation, no reload.
      window.history.replaceState(window.history.state, '', `/people/new?draft=${res.id}`);
      return res.id;
    } catch (e) {
      handleFailure(e);
      return null;
    } finally { busyRef.current = false; setBusy(false); }
  }, [draft, form, companyCountry, currency, company, acks, differentReason, handleFailure, applied]);

  const onSaveDraft = useCallback(async () => {
    const list = validateHire(form, ctx, 'draft');
    if (list.length) { showIssues(list, 'field'); return; }
    setSummaryFields([]);
    await persist();
  }, [form, ctx, persist, showIssues]);

  const onNext = useCallback(() => {
    const list = validateHire(form, ctx, 'create').filter(i => i.step === step);
    if (list.length) { showIssues(list, 'summary'); return; }
    setSummaryFields([]);
    goTo((step + 1) as Step);
  }, [form, ctx, step, showIssues, goTo]);

  const onCreate = useCallback(async () => {
    const list = validateHire(form, ctx, 'create');
    if (list.length) { showIssues(list, 'summary'); return; }
    setSummaryFields([]);
    const id = await persist();
    if (id !== null) setCreated({ id, name: form.englishName.trim() });
  }, [form, ctx, persist, showIssues]);

  const onRehire = useCallback(async (m: MatchCard, reason: string) => {
    if (!form.startDate) { showIssues([{ field: 'startDate', step: 1, key: 'Enter the start date.' }], 'field'); setStep(1); return; }
    try {
      await employeeHireApi.rehire(m.employeeId, { startDate: form.startDate, designationId: form.designationId || undefined, gradeId: form.gradeId || undefined, reason });
      setCreated({ id: m.employeeId, name: m.fullName, rehired: true });
    } catch (e) { handleFailure(e); }
  }, [form, showIssues, handleFailure]);

  const onNationality = useCallback((code: string) => {
    setForm(f => ({ ...f, idType: defaultIdType(code, resident) ?? '' }));
    void employeeFieldCatalogApi.get({ companyId: form.companyId, nationality: code });
  }, [resident, form.companyId]);

  const leave = useCallback(() => {
    flushSync(() => setLeaving(true));
    router.push('/people');
  }, [router]);

  const onCancel = () => { if (!draft && !hasContent(form, initial.current)) leave(); else setDiscardOpen(true); };
  const onDiscard = async () => {
    if (draft) { try { await employeeHireApi.discardDraft(draft.id); } catch { setNotice({ key: 'The draft could not be deleted. Try again.' }); setDiscardOpen(false); return; } }
    leave();
  };

  // ── render ──────────────────────────────────────────────────────────────
  const stepProps: StepProps = { form, set, errorFor, blur, company, companyCountry, currency, lookups, today, singleCompany, banks };
  const label = (s: number) => t(STEP_LABELS[s - 1]);
  const canOverride = hasPermission('employees.override_validation');
  const savedText = saveState === 'saving' || saveState === 'pending' ? t('Saving…') : saveState === 'saved' && savedAt ? t('Saved {time}', { time: clock(savedAt, zone, locale) }) : saveState === 'error' ? t('Not saved yet. It will retry on your next change.') : '';

  if (resume.state !== 'idle') {
    const msg = resume.state === 'unavailable' ? 'This draft could not be found, or you do not have access to it.'
      : resume.state === 'notDraft' ? 'This employee is no longer a draft.'
      : resume.state === 'network' ? 'The draft could not be loaded. Check your connection and try again.' : '';
    return (
      <div className="mx-auto w-full max-w-[720px] px-4 pb-4 pt-4">
        <h1 className="text-xl font-semibold text-slate-900 dark:text-white">{t('Add employee')}</h1>
        {resume.state === 'loading' ? <p className="mt-4 text-sm text-slate-500">{t('Loading the draft…')}</p> : (
          <>
            <p role="alert" className="mt-4 rounded-lg border border-red-300 bg-red-50 p-3 text-sm text-red-900 dark:border-red-400/40 dark:bg-red-500/10 dark:text-red-100">{t(msg)}</p>
            <div className="mt-4 flex flex-wrap gap-2">
              {resume.state === 'notDraft' && resume.id !== undefined && <Link className="btn-primary" href={`/people/${resume.id}`}>{t('Open the record')}</Link>}
              {resume.state === 'network' && <button type="button" className="btn-secondary" onClick={() => window.location.reload()}>{t('Reload')}</button>}
              <a className={resume.state === 'notDraft' ? 'btn-secondary' : 'btn-primary'} href="/people/new">{t('Start a new employee')}</a>
              <Link className="btn-secondary" href="/people">{t('Back to people')}</Link>
            </div>
          </>
        )}
      </div>
    );
  }

  if (created) return <div className="mx-auto w-full max-w-[720px] p-4"><SuccessStep id={created.id} name={created.name} rehired={created.rehired} /></div>;

  return (
    <div className="mx-auto w-full max-w-[720px] px-4 pb-4 pt-4">
      <h1 className="text-xl font-semibold text-slate-900 dark:text-white">{t('Add employee')}</h1>
      <nav aria-label={t('Employee setup progress')} className="mt-4">
        <p className="text-sm font-medium text-slate-600 sm:hidden dark:text-slate-300">{t('Step {n} of {total}: {label}', { n: String(step), total: '4', label: label(step) })}</p>
        <ol aria-label={t('Progress')} className="hidden gap-2 sm:grid sm:grid-cols-4">
          {STEP_LABELS.map((_, i) => (
            <li key={i} aria-current={step === i + 1 ? 'step' : undefined} className={`rounded-lg border px-3 py-2 text-sm ${step === i + 1 ? 'border-sapphire bg-sapphire/5 font-semibold text-sapphire' : i + 1 < step ? 'border-emerald-300 text-emerald-800 dark:text-emerald-200' : 'border-slate-200 text-slate-500 dark:border-white/10'}`}>
              <span aria-hidden="true" className="me-1.5 tabular-nums">{i + 1}.</span>{label(i + 1)}
            </li>
          ))}
        </ol>
        <p aria-live="polite" className="sr-only">{t('Step {n} of {total}: {label}', { n: String(step), total: '4', label: label(step) })}</p>
      </nav>

      <div className="mt-5">
        {loadFailed && <p role="alert" className="mb-3 rounded-lg border border-red-300 bg-red-50 p-3 text-sm text-red-900">{t('Could not load the company and organization lists. Reload the page.')}</p>}
        {notice && (
          <p role="alert" className="mb-3 flex flex-wrap items-center gap-3 rounded-lg border border-red-300 bg-red-50 p-3 text-sm text-red-900 dark:border-red-400/40 dark:bg-red-500/10 dark:text-red-100">
            <span>{t(notice.key, notice.params)}</span>
            {notice.action === 'reload' && <button type="button" className="btn-secondary" onClick={() => window.location.reload()}>{t('Reload')}</button>}
          </p>
        )}
        <ErrorSummary ref={summaryRef} issues={shownSummary} onGo={focusIssue} />
        <DuplicatePanel matches={matches} companies={lookups.companies} differentReason={differentReason} onDifferent={r => setDifferentReason(r)} onRehire={onRehire} />

        <h2 ref={headingRef} tabIndex={-1} className="mb-4 text-lg font-semibold text-slate-900 outline-none dark:text-white">{label(step)}</h2>
        {!loaded ? <p className="text-sm text-slate-500">{t('Loading…')}</p> : (
          <>
            {step === 1 && <HireBasicsStep {...stepProps} onNameBlur={runDuplicateCheck} applied={applied} onApplyTemplate={onApplyTemplate} onUndoTemplate={onUndoTemplate} />}
            {step === 2 && <PersonContactStep {...stepProps} countryOptions={countryOptions} onNationality={onNationality} onDobBlur={runDuplicateCheck} />}
            {step === 3 && <JobPayDocsStep {...stepProps} canOverrideChecksum={canOverride} resident={resident}
              onResident={v => { setResident(v); setForm(f => ({ ...f, idType: defaultIdType(f.nationalityCode, v) ?? f.idType })); }} onIdBlur={runDuplicateCheck} />}
            {step === 4 && <ReviewStep {...stepProps} onEdit={s => goTo(s)} hireTemplate={applied?.template ?? null} />}
          </>
        )}
      </div>

      <div data-testid="action-bar" className="sticky bottom-[calc(4.5rem+env(safe-area-inset-bottom))] z-30 -mx-4 mt-6 border-t border-slate-200 bg-white px-4 pb-3 pt-3 lg:bottom-0 lg:pb-[max(0.75rem,env(safe-area-inset-bottom))] dark:border-white/10 dark:bg-slate-900">
        <div className="flex w-full flex-wrap items-center gap-2">
          <button type="button" className="btn-secondary" onClick={onCancel}>{t('Cancel')}</button>
          <button type="button" className="btn-secondary" disabled={busy} onClick={onSaveDraft}>{t('Save draft')}</button>
          <span data-testid="saved-indicator" role="status" aria-live="polite" className="text-xs text-slate-500">{savedText}</span>
          <span className="ms-auto flex gap-2">
            {step > 1 && <button type="button" className="btn-secondary" onClick={() => goTo((step - 1) as Step)}>{t('Back')}</button>}
            {step < 4 ? <button type="button" className="btn-primary" onClick={onNext}>{t('Next')}</button>
              : <button type="button" className="btn-primary" disabled={busy} onClick={onCreate}>{t('Create employee draft')}</button>}
          </span>
        </div>
      </div>

      {warnings && <WarningAckDialog warnings={warnings} canOverride={canOverride} onCancel={() => setWarnings(null)} onConfirm={a => { setWarnings(null); setAcks(a); void persist(a); }} />}
      {discardOpen && <DiscardDialog hasDraft={!!draft} canSave={validateHire(form, ctx, 'draft').length === 0} onKeep={() => setDiscardOpen(false)}
        onSaveLeave={async () => { setDiscardOpen(false); if ((await persist()) !== null) leave(); }} onLeaveKeep={() => { setDiscardOpen(false); leave(); }} onDiscard={onDiscard} />}
    </div>
  );
}
