'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { ArrowLeft, ArrowRight, CheckCircle2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { msg } from '../../i18n/translations';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { rolesApi, type AccessCeiling } from '../../api/identity';
import {
  companySetupApi, httpStatus, type CompanySetupContext, type CompanySetupDraft, type CompanySetupDraftBody, type CompanySetupDraftSummary,
  type CompanySetupErrorBody, type CompanySetupPlan, type CompanySetupResult,
} from '../../api/companySetup';
import {
  GUIDED_SETUP_HREF, STEP_IDS, buildTree, canCommit, completeCompanyHref, emptyBody, firstBlockingStep, normalizeFieldPath, planIsNoChange, problemsByField,
  setupCodeCopy, stepCopy, stepState, validateStep, type FieldError, type StepId, type StepState, type WizardMode,
} from '../../lib/companySetupWizard';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { WizardRail } from './WizardRail';
import { StructureTree } from './StructureTree';
import { StartStep, type StartChoice } from './StartStep';
import { CompanyStep } from './CompanyStep';
import { BranchesStep } from './BranchesStep';
import { OrganisationStep } from './OrganisationStep';
import { RulesStep } from './RulesStep';
import { AdminStep } from './AdminStep';
import { ReviewStep } from './ReviewStep';
import { CompanyReadinessList } from './CompanyReadinessList';
import { CompanySummaryStep } from './CompanySummaryStep';
import { MoreSettingsLinks } from './MoreSettingsLinks';
import { Notice, WizardFormProvider, fieldId } from './wizardForm';
import { useDraftAutosave } from './useDraftAutosave';
import { ArabicSuggestionsProvider } from './ArabicSuggest';
import {
  SECTION_TITLE, STEP_SECTIONS, SectionSaveBar, SectionSaveProvider, SectionSavedStatus, sectionHeadingId, sectionOfField, sectionSnapshot,
  type SectionId, type SectionSaveValue, type SectionStatus,
} from './sectionSave';
import { useNavigationBlocker } from '../../hooks/useNavigationBlocker';

const STEP_HEADINGS: Record<StepId, { heading: string; help: string }> = {
  1: { heading: msg('Create a company'), help: msg('Each screen leads to the next. Save each section as you finish it. Nothing is created until you choose Create company at the end.') },
  2: { heading: msg('Company details'), help: msg('The legal entity. Fields marked * are needed to continue.') },
  3: { heading: msg('Branches & locations'), help: msg('Where this company’s people are based and punch in.') },
  4: { heading: msg('Organisation'), help: msg('Cost centres for this company, and the departments, designations and grades it uses from the group.') },
  5: { heading: msg('Work & pay rules'), help: msg('Confirm what the country pack sets up. Change only what is different for this company.') },
  6: { heading: msg('Company super admin'), help: msg('Choose who administers this company.') },
  7: { heading: msg('Review & create'), help: msg('This is exactly what Create will do. Nothing is created until you press Create company.') },
};

/** Existing mode (Addendum A) reads differently only where the journey differs: the summary and the review. */
const EXISTING_HEADINGS: Partial<Record<StepId, { heading: string; help: string }>> = {
  1: { heading: msg('Complete setup for {company}'), help: msg('See what this company has today, then go step by step. Save each section as you finish it. Nothing changes in the company until you choose Save all changes at the end.') },
  7: { heading: msg('Review & update'), help: msg('These are exactly the changes that will be saved. Nothing changes in the company until you choose Save all changes.') },
};

const fieldStep = (field: string): StepId => {
  if (field.startsWith('company.')) return 2;
  if (field.startsWith('branches') || field === 'locations') return 3;
  if (/^(costCentres|departments|designations|grades)/.test(field)) return 4;
  if (field.startsWith('rules.')) return 5;
  if (field.startsWith('companyAdmin')) return 6;
  return 1;
};

type LoadState = 'loading' | 'ready' | 'error' | 'forbidden';

/**
 * The company setup journey: one draft, seven steps, one commit. Drafts live on the server
 * (autosaved, versioned) so they survive sign-out and devices; the commit names the draft by id, which
 * is also the idempotency key, so a retry after a lost response returns the same result.
 *
 * One wizard, two modes (Addendum A): without `existingCompanyId` it creates a company; with it, it
 * completes setup for that active company — prefilled from what it has, adding and editing only.
 */
export function CreateCompanyWizard({ existingCompanyId }: { existingCompanyId?: string } = {}) {
  const mode: WizardMode = existingCompanyId ? 'existing' : 'create';
  const existing = mode === 'existing';
  const { t, locale } = useLocale();
  const fmt = useFormat();
  const router = useRouter();
  const searchParams = useSearchParams();
  const { hasRole } = useAuth();
  const requestedDraft = searchParams?.get('draft') ?? null;

  const [load, setLoad] = useState<LoadState>('loading');
  const [context, setContext] = useState<CompanySetupContext | null>(null);
  const [drafts, setDrafts] = useState<CompanySetupDraftSummary[]>([]);
  const [ceiling, setCeiling] = useState<AccessCeiling | null | 'loading'>('loading');
  const [draftId, setDraftId] = useState<string | null>(null);
  const [draftMeta, setDraftMeta] = useState<{ createdByName: string; createdAtUtc: string } | null>(null);
  const [body, setBody] = useState<CompanySetupDraftBody>(() => emptyBody());
  const [defaults, setDefaults] = useState<CompanySetupDraftBody | null>(null);
  const [step, setStep] = useState<StepId>(1);
  const [visited, setVisited] = useState<Set<StepId>>(() => new Set([1]));
  const [attempted, setAttempted] = useState<Set<StepId>>(() => new Set());
  const [touched, setTouched] = useState<Set<string>>(() => new Set());
  const [choice, setChoice] = useState<StartChoice>({ startFrom: 'defaults', countryCode: 'SA' });
  const [busy, setBusy] = useState<'' | 'starting' | 'resuming' | 'exiting' | 'creating' | 'reloading'>('');
  const [error, setError] = useState('');
  const [plan, setPlan] = useState<CompanySetupPlan | null>(null);
  const [planState, setPlanState] = useState<'idle' | 'loading' | 'error'>('idle');
  const [result, setResult] = useState<CompanySetupResult | null>(null);
  const [commitError, setCommitError] = useState<{ message: string; retry: boolean; step?: StepId } | null>(null);
  const [confirmation, setConfirmation] = useState<ConfirmState | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const focusField = useRef<string | null>(null);
  /** Per section: what its last explicit Save (or the draft as opened) held, and when it was saved by Save. */
  const [sectionRefs, setSectionRefs] = useState<Partial<Record<SectionId, { snapshot: string; at: string | null }>>>({});
  const [sectionAttempts, setSectionAttempts] = useState<Set<SectionId>>(() => new Set());
  const [sectionBusy, setSectionBusy] = useState<SectionId | null>(null);
  const [sectionProblems, setSectionProblems] = useState<Partial<Record<SectionId, string>>>({});
  const bodyRef = useRef(body);
  bodyRef.current = body;
  /** A draft as the server holds it: every section matches it. `at` is set when it was saved before (resumed). */
  const markSections = useCallback((saved: CompanySetupDraftBody, at: string | null) => {
    const refs: Partial<Record<SectionId, { snapshot: string; at: string | null }>> = {};
    for (const id of Object.values(STEP_SECTIONS).flat()) refs[id] = { snapshot: sectionSnapshot(saved, id), at };
    setSectionRefs(refs);
    setSectionAttempts(new Set());
    setSectionProblems({});
  }, []);

  const autosave = useDraftAutosave({ draftId, body, step });
  const errorBody = (err: unknown) => (err as { response?: { data?: Partial<CompanySetupErrorBody> } } | null)?.response?.data;
  /** The server's refusal in the reader's language, or ''. A known code reads as what to do next. */
  const serverText = (err: unknown) => {
    const data = errorBody(err);
    const copy = setupCodeCopy(data?.code);
    if (copy) return t(copy);
    return (locale === 'ar' ? data?.messageAr || data?.message : data?.message) ?? '';
  };

  // ── Who may appoint a company super admin ──────────────────────────────────
  // A group-scope Admin for any company; in existing mode also a company super admin for their own company,
  // who may add another company-scoped admin but never group scope (A5; the server enforces the ceiling).
  const groupAdmin = hasRole('Admin') && (!!context?.isGroupScope || existing);
  const manageable = existing ? context?.manageableCompanies?.find(c => c.id === existingCompanyId) ?? null : null;
  useEffect(() => {
    if (!groupAdmin) { setCeiling(null); return; }
    let active = true;
    rolesApi.ceiling().then(c => { if (active) setCeiling(c); }).catch(() => { if (active) setCeiling(null); });
    return () => { active = false; };
  }, [groupAdmin]);
  // The server decides when the ceiling is unknown (accessCeiling.ts): only a known refusal hides the step.
  const adminRole = ceiling && ceiling !== 'loading' ? ceiling.roles.find(r => r.name.toLowerCase() === 'admin') : undefined;
  // The server's own answer wins when it gives one; otherwise the role ceiling decides.
  const serverGrant = context?.canGrantCompanyAdmin;
  // Creating a company is group scope only, so a company-scoped caller never appoints there, whatever the
  // context-wide grant says (it describes their own company, i.e. existing mode).
  const adminStepVisible = !existing && !context?.isGroupScope ? false : typeof serverGrant === 'boolean'
    ? serverGrant
    : groupAdmin && ceiling !== 'loading' && (adminRole ? adminRole.canAssign : true);
  const adminDecided = !context || typeof serverGrant === 'boolean' || !groupAdmin || ceiling !== 'loading';
  const steps = useMemo(() => STEP_IDS.filter(s => s !== 6 || adminStepVisible), [adminStepVisible]);

  // A viewer who cannot grant the Admin role cannot leave an invite or grant in the draft.
  useEffect(() => {
    if (adminDecided && !adminStepVisible && draftId && body.companyAdmin.mode !== 'groupOnly')
      setBody(b => ({ ...b, companyAdmin: { mode: 'groupOnly' } }));
  }, [adminDecided, adminStepVisible, draftId, body.companyAdmin.mode]);

  // ── Load context, drafts and (when linked) the draft to resume ─────────────
  const draftAddress = useCallback((id: string) => (existingCompanyId ? completeCompanyHref(existingCompanyId, id) : `/setup/new-company?draft=${id}`), [existingCompanyId]);
  /** Resolves true when the draft is on screen. */
  const openDraft = useCallback(async (id: string): Promise<boolean> => {
    setBusy('resuming'); setError('');
    try {
      const draft = await companySetupApi.draft(id);
      if (draft.status !== 'Open') {
        setError(draft.status === 'Submitted'
          ? (existingCompanyId ? t('This draft was already used to save changes. Start again from Guided setup.') : t('This draft was already used to create a company.'))
          : (existingCompanyId ? t('This draft was discarded. Start again from Guided setup.') : t('This draft was discarded. Start a new company instead.')));
        return false;
      }
      // A draft opens only in its own journey: a create draft is not a change to this company, and vice versa.
      if ((draft.body.existingCompanyId ?? null) !== (existingCompanyId ?? null)) {
        setError(t('This draft belongs to a different setup. Open it from Guided setup.'));
        return false;
      }
      autosave.adopt(draft);
      setDraftId(draft.id);
      setDraftMeta({ createdByName: draft.createdByName, createdAtUtc: draft.createdAtUtc });
      setBody(draft.body);
      markSections(draft.body, draft.updatedAtUtc);
      const resumeStep = (Math.min(Math.max(draft.step, 1), 7)) as StepId;
      setStep(resumeStep);
      setVisited(new Set(STEP_IDS.filter(s => s <= resumeStep)));
      setChoice({ startFrom: draft.body.startFrom, countryCode: draft.body.company.countryCode, copyFromCompanyId: draft.body.copyFromCompanyId });
      router.replace(draftAddress(draft.id), { scroll: false });
      (existingCompanyId
        ? companySetupApi.existingDefaults(existingCompanyId)
        : companySetupApi.defaults(draft.body.company.countryCode || 'SA', draft.body.startFrom === 'copy' ? draft.body.copyFromCompanyId : undefined))
        .then(setDefaults).catch(() => setDefaults(null));
      return true;
    } catch (err) {
      setError(httpStatus(err) === 404 ? t('This draft no longer exists. It may have been discarded.') : t('The draft could not be opened. Check your connection and try again.'));
      return false;
    } finally { setBusy(''); }
  }, [autosave, router, t, draftAddress, existingCompanyId, markSections]);

  /** Existing mode before a draft: what the company has today, shown (never saved) until Start. */
  const loadPrefill = useCallback(async () => {
    if (!existingCompanyId) return;
    const prefill = await companySetupApi.existingDefaults(existingCompanyId);
    const shown = { ...prefill, existingCompanyId };
    setDefaults(shown);
    setBody(shown);
  }, [existingCompanyId]);

  useEffect(() => {
    let active = true;
    Promise.all([companySetupApi.context(), companySetupApi.drafts().catch(() => [] as CompanySetupDraftSummary[])])
      .then(([ctx, list]) => {
        if (!active) return;
        setContext(ctx);
        setDrafts(list);
        if (existingCompanyId) {
          // Only a company this caller may manage opens (A5); the server refuses the rest anyway.
          const target = ctx.manageableCompanies?.find(c => c.id === existingCompanyId);
          if (!target) { setLoad('forbidden'); return; }
          const resume = requestedDraft ?? target.openDraftId ?? null;
          // A draft that cannot be opened (discarded, gone) leaves the company's current setup on screen to start again.
          if (resume) {
            setLoad('ready');
            void openDraft(resume).then(ok => { if (!ok && active) loadPrefill().catch(() => { if (active) setLoad('error'); }); });
            return;
          }
          loadPrefill().then(() => { if (active) setLoad('ready'); }).catch(() => { if (active) setLoad('error'); });
          return;
        }
        if (ctx.sisterCompanies.length > 0 && !requestedDraft) setChoice(c => ({ ...c, countryCode: ctx.sisterCompanies[0].countryCode || c.countryCode }));
        setLoad('ready');
        if (requestedDraft && ctx.canCreate) void openDraft(requestedDraft);
      })
      .catch(err => { if (active) setLoad(httpStatus(err) === 403 ? 'forbidden' : 'error'); });
    return () => { active = false; };
    // Load once per visit; openDraft is stable enough for the initial deep link.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ── Validation and messages ────────────────────────────────────────────────
  const input = { body, context, adminStepVisible, baseline: existing ? defaults : null };
  const serverProblems = useMemo(() => problemsByField(plan?.problems ?? []), [plan]);
  const clientErrors = useMemo(() => {
    const map = new Map<string, FieldError>();
    for (const s of steps) for (const e of validateStep(s, { body, context, adminStepVisible, baseline: existing ? defaults : null })) if (!map.has(e.field)) map.set(e.field, e);
    return map;
  }, [steps, body, context, adminStepVisible, existing, defaults]);
  const errorFor = useCallback((field: string) => {
    const client = clientErrors.get(field);
    const section = sectionOfField(field);
    if (client && (touched.has(field) || attempted.has(fieldStep(field)) || (section && sectionAttempts.has(section)))) return t(client.message, client.params);
    const server = serverProblems.get(field);
    if (server) {
      const copy = setupCodeCopy(server.code);
      return copy ? t(copy) : locale === 'ar' ? server.messageAr || server.message : server.message;
    }
    return undefined;
  }, [clientErrors, touched, attempted, sectionAttempts, serverProblems, locale, t]);
  const states = useMemo(() => {
    const out = {} as Record<StepId, StepState>;
    for (const s of STEP_IDS) out[s] = s === 7 ? (plan ? (canCommit(plan) ? 'done' : 'warning') : visited.has(7) ? 'warning' : 'notStarted') : stepState(s, { ...input, visited, serverProblems: plan?.problems });
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [body, context, adminStepVisible, visited, plan, defaults]);
  const blocking = firstBlockingStep(steps.filter(s => s !== 1 || !draftId), input);
  const reachable = (s: StepId) => !!draftId && (blocking === null || steps.indexOf(s) <= steps.indexOf(blocking));

  // ── Navigation ─────────────────────────────────────────────────────────────
  useEffect(() => {
    if (focusField.current) {
      const el = document.getElementById(fieldId(focusField.current));
      focusField.current = null;
      if (el) { el.focus(); return; }
    }
    headingRef.current?.focus();
  }, [step, attempted]);

  const goTo = (target: StepId) => {
    if (target === step) return;
    setVisited(v => new Set([...v, target]));
    setStep(target);
    setError('');
    setCommitError(null);
  };

  /** From the review: open the step and focus the field a problem names. */
  const goToField = (target: StepId, field?: string) => {
    if (field) focusField.current = normalizeFieldPath(field);
    goTo(target);
  };

  const refreshPlan = useCallback(async (forBody: CompanySetupDraftBody) => {
    setPlanState('loading');
    try { const next = await companySetupApi.dryRun(forBody); planBody.current = forBody; setPlan(next); setPlanState('idle'); }
    catch { setPlanState('error'); }
  }, []);

  // A plan describes the body it was made for; once the body changes its problems no longer apply.
  // Step 5 keeps its preview while its own toggles change (the review checks again anyway).
  const planBody = useRef<CompanySetupDraftBody | null>(null);
  useEffect(() => {
    if (plan && planBody.current && planBody.current !== body && step !== 5) setPlan(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [body]);

  // Step 5 shows what the pack resolves to, and step 7 is the plan itself: both come from the dry-run.
  useEffect(() => {
    if (!draftId || (step !== 5 && step !== 7)) return;
    const timer = setTimeout(() => { void refreshPlan(body); }, step === 7 ? 0 : 400);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step, draftId]);

  const adoptNew = (draft: CompanySetupDraft) => {
    autosave.adopt(draft);
    setDraftId(draft.id);
    setDraftMeta({ createdByName: draft.createdByName, createdAtUtc: draft.createdAtUtc });
    setBody(draft.body);
    markSections(draft.body, null);
    setDrafts(list => [{ id: draft.id, legalNameEn: draft.body.company.legalNameEn, step: draft.step, updatedAtUtc: draft.updatedAtUtc, createdByName: draft.createdByName, existingCompanyId: draft.body.existingCompanyId ?? null }, ...list]);
    router.replace(draftAddress(draft.id), { scroll: false });
    goTo(2);
  };

  /** Existing mode: the first save makes the draft from the prefill. One open draft per company (A1). */
  const startExisting = async () => {
    if (!existingCompanyId || !defaults) return;
    setBusy('starting'); setError('');
    try {
      adoptNew(await companySetupApi.createDraft({ ...body, existingCompanyId }));
    } catch (err) {
      const status = httpStatus(err);
      if (status === 409) {
        // Someone opened a draft for this company meanwhile (existing_company_draft_open carries its id): continue
        // that one rather than start a second.
        let open = errorBody(err)?.draftId ?? null;
        if (!open) {
          const ctx = await companySetupApi.context().catch(() => null);
          open = ctx?.manageableCompanies?.find(c => c.id === existingCompanyId)?.openDraftId ?? null;
          if (ctx) setContext(ctx);
        }
        if (open) { setBusy(''); await openDraft(open); return; }
      }
      setError(status === 403 || status === 409
        ? (serverText(err) || t('Setup for this company cannot be changed right now.'))
        : t('The draft could not be started. Nothing was saved. Check your connection and try again.'));
    } finally { setBusy(''); }
  };

  const start = async () => {
    if (existing) { await startExisting(); return; }
    if (choice.startFrom === 'copy' && !choice.copyFromCompanyId) { setError(t('Choose the company to copy from.')); return; }
    if (choice.startFrom === 'defaults' && !choice.countryCode) { setError(t('Choose the country the company is registered in.')); return; }
    setBusy('starting'); setError('');
    try {
      const prepared = await companySetupApi.defaults(choice.countryCode, choice.startFrom === 'copy' ? choice.copyFromCompanyId : undefined);
      setDefaults(prepared);
      adoptNew(await companySetupApi.createDraft({ ...prepared, startFrom: choice.startFrom, copyFromCompanyId: choice.startFrom === 'copy' ? choice.copyFromCompanyId : undefined }));
    } catch (err) {
      const status = httpStatus(err);
      setError(status === 403 || status === 409
        ? (serverText(err) || t('A new company cannot be created right now.'))
        : t('The draft could not be started. Nothing was saved. Check your connection and try again.'));
    } finally { setBusy(''); }
  };

  const next = () => {
    if (step === 1 && !draftId) { void start(); return; }
    const errors = validateStep(step, input);
    if (errors.length) {
      focusField.current = errors[0].field;
      setAttempted(a => new Set([...a, step]));
      return;
    }
    const index = steps.indexOf(step);
    if (index < steps.length - 1) goTo(steps[index + 1]);
  };
  // Leaving a field shows its message, which can move these buttons between mousedown and mouseup and
  // swallow the click. Keep focus where it is on press; Next then focuses the first blocked field itself.
  const keepFocus = (event: React.MouseEvent) => event.preventDefault();
  const back = () => {
    const index = steps.indexOf(step);
    if (index > 0) goTo(steps[index - 1]);
  };

  const saveAndExit = async () => {
    setBusy('exiting'); setError('');
    const ok = await autosave.flush();
    setBusy('');
    if (ok) router.push(existing ? GUIDED_SETUP_HREF : '/setup?tab=companies');
    else if (autosave.state !== 'conflict') setError(t('The draft could not be saved, so you are still here. Check your connection and try again.'));
  };

  const reloadDraft = async () => {
    if (!draftId) return;
    setBusy('reloading');
    try {
      const draft = await companySetupApi.draft(draftId);
      autosave.adopt(draft);
      setBody(draft.body);
      markSections(draft.body, draft.updatedAtUtc);
      setStep((Math.min(Math.max(draft.step, 1), 7)) as StepId);
      setPlan(null);
    } catch { setError(t('The draft could not be reloaded. Check your connection and try again.')); }
    finally { setBusy(''); }
  };

  /** A9: take the current value / keep mine — change the draft (autosaved) and check the plan again. */
  const changeAndCheck = (change: (b: CompanySetupDraftBody) => CompanySetupDraftBody) => {
    const nextBody = change(body);
    setBody(nextBody);
    void refreshPlan(nextBody);
  };

  /** A9: a draft saved before baselines existed starts again from what the company has today (same draft, autosaved). */
  const reloadFromCompany = () => {
    if (!existingCompanyId) return;
    setConfirmation({
      title: t('Reload the company’s current setup?'),
      message: t('This draft was saved before KynexOne recorded what {company} had when it started, so it cannot tell your edits from changes made elsewhere. The draft is replaced with what the company has today; then make your changes again. Nothing has been changed yet.', { company: body.company.legalNameEn }),
      confirmLabel: t('Reload'),
      onConfirm: async () => {
        try {
          const prefill = await companySetupApi.existingDefaults(existingCompanyId);
          const shown = { ...prefill, existingCompanyId };
          setDefaults(shown);
          setBody(shown);
          setConfirmation(null);
          void refreshPlan(shown);
        } catch {
          setConfirmation(null);
          setError(t('The company could not be reloaded. Check your connection and try again.'));
        }
      },
    });
  };

  const discard = (summary: CompanySetupDraftSummary) => setConfirmation({
    title: t('Discard this draft?'),
    message: t('“{name}” will be removed from the list of drafts. No company was created from it.', { name: summary.legalNameEn || t('Untitled company') }),
    confirmLabel: t('Discard draft'),
    onConfirm: async () => {
      await companySetupApi.discardDraft(summary.id);
      setDrafts(list => list.filter(d => d.id !== summary.id));
      setConfirmation(null);
    },
  });

  /** Existing mode: drop this company's open draft and show what the company has today again. */
  const discardCurrent = () => {
    if (!draftId || !existingCompanyId) return;
    setConfirmation({
      title: t('Discard this draft?'),
      message: t('The changes in this draft are dropped. Nothing was changed for {company}.', { company: body.company.legalNameEn }),
      confirmLabel: t('Discard draft'),
      onConfirm: async () => {
        await companySetupApi.discardDraft(draftId);
        setConfirmation(null);
        setDraftId(null); setDraftMeta(null); setPlan(null);
        setVisited(new Set([1])); setAttempted(new Set()); setTouched(new Set()); setSectionRefs({});
        router.replace(completeCompanyHref(existingCompanyId), { scroll: false });
        await loadPrefill().catch(() => setError(t('The company could not be reloaded. Check your connection and try again.')));
      },
    });
  };

  const create = async () => {
    if (!draftId) return;
    setBusy('creating'); setCommitError(null);
    const saved = await autosave.flush();
    if (!saved) {
      setBusy('');
      setCommitError({ message: autosave.isConflict() ? t('This draft was changed elsewhere. Reload it, check the review again, then create.') : t('The latest changes could not be saved, so nothing was created. Check your connection and try again.'), retry: !autosave.isConflict() });
      return;
    }
    try {
      const res = await companySetupApi.commit(draftId, autosave.version());
      setResult(res);
      window.dispatchEvent(new Event('kynexone:companies-changed'));
    } catch (err) {
      const status = httpStatus(err);
      const data = errorBody(err);
      const serverMessage = serverText(err);
      if (status === 409 || status === 422 || status === 400) {
        const target = data?.step && steps.includes(data.step as StepId) ? data.step as StepId : data?.field ? fieldStep(data.field) : 2;
        setCommitError({ message: serverMessage || (existing
          ? t('The changes could not be saved because something in the draft clashes with an existing record. Nothing was changed.')
          : t('The company could not be created because something in the draft clashes with an existing record. Nothing was created.')), retry: false, step: target });
        void refreshPlan(body);
      } else if (status === 403) {
        setCommitError({ message: serverMessage || (existing ? t('You no longer have permission to change this company’s setup. Nothing was changed.') : t('You no longer have permission to create companies. Nothing was created.')), retry: false });
      } else {
        // Lost response: the commit may have gone through. Retrying with the same draft id returns the same result.
        setCommitError({ message: existing
          ? t('We could not confirm whether the changes were saved. Try again: retrying never applies them twice.')
          : t('We could not confirm whether the company was created. Try again: retrying never creates a second company.'), retry: true });
      }
    } finally { setBusy(''); }
  };

  // ── Per-section Save (on top of the background autosave) ───────────────────
  const stepSections = (s: StepId) => (steps.includes(s) ? STEP_SECTIONS[s] : []);
  const sectionStatus = (id: SectionId): SectionStatus => {
    const ref = sectionRefs[id];
    if (!ref) return { kind: 'notSaved' };
    if (sectionSnapshot(body, id) !== ref.snapshot) return { kind: 'unsaved' };
    return ref.at ? { kind: 'saved', at: ref.at } : { kind: 'notSaved' };
  };
  const saveSection = async (id: SectionId) => {
    if (!draftId || sectionBusy) return;
    setSectionProblems(p => ({ ...p, [id]: undefined }));
    // This section's own checks only; the rest of the step is checked by its own Save or by Next.
    const errors = validateStep(step, input).filter(e => sectionOfField(e.field) === id);
    if (errors.length) {
      setSectionAttempts(a => new Set([...a, id]));
      setSectionProblems(p => ({ ...p, [id]: t('{count, plural, one {# field needs} other {# fields need}} attention in this section. Nothing was saved.', { count: errors.length }) }));
      requestAnimationFrame(() => document.getElementById(fieldId(errors[0].field))?.focus());
      return;
    }
    setSectionBusy(id);
    const ok = await autosave.flush();
    setSectionBusy(null);
    if (!ok) {
      setSectionProblems(p => ({ ...p, [id]: autosave.isConflict()
        ? t('This draft was changed elsewhere, so this section was not saved. Reload the draft, then save the section again.')
        : t('This section could not be saved. Your changes are still here. Check your connection and try again.') }));
      return;
    }
    const saved = bodyRef.current;
    setSectionRefs(r => ({ ...r, [id]: { snapshot: sectionSnapshot(saved, id), at: new Date().toISOString() } }));
    const sections = stepSections(step);
    const nextSection = sections[sections.indexOf(id) + 1];
    if (nextSection) {
      const heading = document.getElementById(sectionHeadingId(nextSection));
      heading?.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
      heading?.focus({ preventScroll: true });
      return;
    }
    const index = steps.indexOf(step);
    if (index < steps.length - 1) goTo(steps[index + 1]);
  };
  /** The rail: a visited step with sections is Saved only when every section of it is. */
  const savedStates = {} as Record<StepId, 'saved' | 'unsaved' | null>;
  for (const s of STEP_IDS) {
    const ids = stepSections(s);
    savedStates[s] = !draftId || ids.length === 0 || !visited.has(s) ? null : ids.every(id => sectionStatus(id).kind === 'saved') ? 'saved' : 'unsaved';
  }
  const unsavedSections = draftId && !result ? steps.flatMap(stepSections).filter(id => sectionStatus(id).kind === 'unsaved') : [];
  // Leaving the wizard (sidebar, Cancel, Back in the browser) with a section changed since its Save: the app's guard asks.
  useNavigationBlocker({
    id: 'company-setup-wizard',
    when: unsavedSections.length > 0,
    message: t('These sections have changes you have not saved: {sections}. The automatic draft backup may already hold them. Leave anyway?', {
      sections: new Intl.ListFormat(locale, { type: 'conjunction' }).format(unsavedSections.map(id => t(SECTION_TITLE[id]))),
    }),
    onDiscard: () => {},
  });
  const sectionSave: SectionSaveValue = {
    status: sectionStatus,
    save: id => { void saveSection(id); },
    busy: sectionBusy,
    problem: id => sectionProblems[id] ?? null,
    isLast: id => { const ids = stepSections(STEP_IDS.find(s => STEP_SECTIONS[s].includes(id)) ?? 1); return ids[ids.length - 1] === id; },
    disabled: !draftId || autosave.state === 'conflict' || (existing ? false : !context?.canCreate),
  };

  // ── Render ─────────────────────────────────────────────────────────────────
  if (load === 'loading') return <p role="status" className="surface p-6 text-sm text-slate-600 dark:text-slate-300">{t('Opening company setup…')}</p>;
  if (load === 'forbidden' && existing) return (
    <Notice tone="warning" role="alert">
      <p className="font-semibold">{t('You cannot complete setup for this company.')}</p>
      <p className="mt-1">{t('It is not active, or it is not one of the companies you manage. Ask your group super admin.')}</p>
      <Link href={GUIDED_SETUP_HREF} className="btn-secondary mt-3">{t('Back to Guided setup')}</Link>
    </Notice>
  );
  if (load === 'forbidden') return (
    <Notice tone="warning" role="alert">
      <p className="font-semibold">{t('Only a group administrator or HR manager can create a company.')}</p>
      <p className="mt-1">{t('Ask your group super admin to create it, or to give you group access.')}</p>
      <Link href="/setup?tab=companies" className="btn-secondary mt-3">{t('Back to companies')}</Link>
    </Notice>
  );
  if (load === 'error' || !context) return (
    <Notice tone="error" role="alert">
      <p>{t('Company setup could not be opened. Check your connection and try again.')}</p>
      <button type="button" className="btn-secondary mt-3" onClick={() => window.location.reload()}>{t('Try again')}</button>
    </Notice>
  );

  if (result) {
    const name = (locale === 'ar' && body.company.legalNameAr?.trim()) || body.company.legalNameEn;
    return (
      <div className="surface space-y-6 p-5 sm:p-8" data-testid="create-company-success" data-mode={mode}>
        <div className="flex items-start gap-3">
          <CheckCircle2 className="mt-1 h-7 w-7 shrink-0 text-emerald-600" aria-hidden="true" />
          <div className="min-w-0">
            <h2 ref={headingRef} tabIndex={-1} className="text-xl font-semibold text-slate-950 outline-none dark:text-white"><bdi>{existing ? t('{name} is updated', { name }) : t('{name} is created', { name })}</bdi></h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{existing
              ? (result.replayed ? t('These changes were already saved from this draft; here is the same result.') : t('The changes were saved together. Finish the items below before the next payroll.'))
              : (result.replayed ? t('This company was already created from this draft; here is the same result.') : t('The company and its setup were created together. Finish the items below before the first payroll.'))}</p>
          </div>
        </div>
        <CompanyReadinessList items={result.readiness} />
        <details className="rounded-xl border border-slate-200 dark:border-white/10">
          <summary className="cursor-pointer px-4 py-3 text-sm font-semibold text-slate-900 dark:text-white">{t('More settings for this company')}</summary>
          <div className="px-4 pb-4"><MoreSettingsLinks companyId={result.companyId} /></div>
        </details>
        <div className="flex flex-wrap gap-2">
          {existing
            ? <Link href={GUIDED_SETUP_HREF} className="btn-primary">{t('Back to Guided setup')}</Link>
            : <Link href={`/setup?tab=aiSetup&path=configuration&companyId=${result.companyId}`} className="btn-primary">{t('Open the new company’s setup')}</Link>}
          <Link href="/setup?tab=companies" className="btn-secondary">{t('Back to companies')}</Link>
        </div>
      </div>
    );
  }

  const tree = buildTree(body, context);
  const position = steps.indexOf(step) + 1;
  const isLast = step === 7;
  const saveLabel = {
    // The background copy. Explicit Save, per section, is how progress is confirmed.
    idle: '', pending: t('Backing up the draft…'), saving: t('Backing up the draft…'),
    saved: autosave.savedAt ? t('Draft backed up {when}', { when: fmt.time(autosave.savedAt) }) : t('Draft backed up'),
    conflict: '', error: t('Not saved yet. Your changes are kept on this screen; we will try again when you next change something.'),
  }[autosave.state];
  const stepErrors = attempted.has(step) ? validateStep(step, input) : [];
  // Existing mode is not limited by the create gates (plan limit, creation mode): the company already exists.
  const locked = existing ? false : !context.canCreate;
  const nextLabel = step === 1 && !draftId ? t('Start') : t('Next');
  const noChange = existing && planIsNoChange(plan);
  const heading = (existing ? EXISTING_HEADINGS[step] : undefined) ?? STEP_HEADINGS[step];
  const headingParams = { company: (locale === 'ar' && body.company.legalNameAr?.trim()) || body.company.legalNameEn || manageable?.legalNameEn || '' };

  return (
    <div className="space-y-4" data-testid="create-company-wizard">
      <ConfirmDialog state={confirmation} onCancel={() => setConfirmation(null)} />
      <div className="surface overflow-clip">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3 dark:border-white/10 sm:px-5">
          <div className="min-w-0 text-xs text-slate-600 dark:text-slate-300">
            {draftId
              ? <p role="status" data-testid="autosave-status" data-state={autosave.state}>{saveLabel || t('Draft backed up')}{draftMeta && <span className="hidden sm:inline">{' · '}{t('Started by {name}', { name: draftMeta.createdByName })}</span>}</p>
              : <p>{existing ? t('A draft is saved as soon as you start, and kept until you save the changes or discard it.') : t('A draft is saved as soon as you start, and kept until you create the company or discard it.')}</p>}
          </div>
          {draftId && <button type="button" className="btn-secondary" disabled={!!busy} onClick={() => void saveAndExit()}>{busy === 'exiting' ? t('Saving…') : t('Save draft & exit')}</button>}
        </div>
        {autosave.state === 'conflict' && (
          <div className="border-b border-amber-200 px-4 py-3 dark:border-amber-400/30 sm:px-5">
            <Notice tone="warning" role="alert">
              <p className="font-semibold">{t('This draft was changed elsewhere — reload')}</p>
              <p className="mt-1">{t('Someone saved this draft in another tab or device after you opened it. Reload to see their version; changes made here since then are not saved.')}</p>
              <button type="button" className="btn-secondary mt-2" disabled={busy === 'reloading'} onClick={() => void reloadDraft()}>{t('Reload draft')}</button>
            </Notice>
          </div>
        )}
        <div className="grid lg:grid-cols-[13.5rem_minmax(0,1fr)] xl:grid-cols-[13.5rem_minmax(0,1fr)_17rem]">
          <aside className="border-b border-slate-200 bg-slate-50/80 p-3 dark:border-white/10 dark:bg-white/[0.02] lg:border-b-0 lg:border-e lg:p-4">
            <WizardRail steps={steps} current={step} states={states} saved={savedStates} reachable={reachable} onSelect={goTo} disabled={!!busy} mode={mode} />
          </aside>
          <div className={`min-w-0 p-4 sm:p-5 ${step === 7 ? 'xl:col-span-2' : ''}`}>
            <div className="mb-5">
              <p className="hidden text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400 lg:block">{t('Step {position} of {total}', { position, total: steps.length })}</p>
              <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1">
                <h2 ref={headingRef} tabIndex={-1} className="text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white"><bdi>{t(heading.heading, headingParams)}</bdi></h2>
                {draftId && stepSections(step).length === 1 && <SectionSaveProvider value={sectionSave}><SectionSavedStatus section={stepSections(step)[0]} /></SectionSaveProvider>}
              </div>
              <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400">{t(heading.help)}</p>
            </div>
            {error && <div className="mb-4"><Notice tone="error" role="alert">{error}</Notice></div>}
            {stepErrors.length > 0 && (
              <div className="mb-4"><Notice tone="error" role="alert">
                <p className="font-semibold">{t('{count, plural, one {# field needs} other {# fields need}} attention before you continue.', { count: stepErrors.length })}</p>
                <ul className="mt-1 list-disc ps-5">
                  {stepErrors.slice(0, 6).map(e => <li key={e.field}><a className="underline" href={`#${fieldId(e.field)}`} onClick={ev => { ev.preventDefault(); document.getElementById(fieldId(e.field))?.focus(); }}>{t(e.message, e.params)}</a></li>)}
                </ul>
              </Notice></div>
            )}
            <WizardFormProvider value={{ body, update: change => setBody(b => change(b)), errorFor, context, readOnly: locked || autosave.state === 'conflict', baseline: existing ? defaults : null }}>
              <ArabicSuggestionsProvider>
              <SectionSaveProvider value={sectionSave}>
              <div onBlurCapture={event => {
                const field = (event.target as HTMLElement).dataset?.field;
                if (field && !touched.has(field)) setTouched(prev => new Set([...prev, field]));
              }}>
                {step === 1 && existing && manageable && <CompanySummaryStep body={body} company={manageable} draft={draftId ? draftMeta : null} onDiscard={draftId ? discardCurrent : undefined} />}
                {step === 1 && existing && !manageable && <p role="status" className="text-sm text-slate-600 dark:text-slate-300">{t('Opening company setup…')}</p>}
                {step === 1 && !existing && <StartStep context={context} drafts={drafts.filter(d => !d.existingCompanyId)} currentDraftId={draftId} choice={choice} onChoice={setChoice}
                  onResume={id => void openDraft(id)} onDiscard={discard} started={!!draftId} />}
                {step === 2 && <CompanyStep />}
                {step === 3 && <BranchesStep defaults={defaults} />}
                {step === 4 && <OrganisationStep defaults={defaults} />}
                {step === 5 && <RulesStep defaults={defaults} preview={plan} previewState={planState} />}
                {step === 6 && adminStepVisible && <AdminStep />}
                {step === 7 && <ReviewStep plan={plan} state={planState} onRetry={() => void refreshPlan(body)} onGoTo={goToField} visibleSteps={steps}
                  onChangeAndCheck={changeAndCheck} onReloadPrefill={existing ? reloadFromCompany : undefined} />}
                {draftId && stepSections(step).length === 1 && <SectionSaveBar section={stepSections(step)[0]} />}
              </div>
              </SectionSaveProvider>
              </ArabicSuggestionsProvider>
            </WizardFormProvider>
            {commitError && (
              <div className="mt-4"><Notice tone="error" role="alert">
                <p>{commitError.message}</p>
                <div className="mt-2 flex flex-wrap gap-2">
                  {commitError.retry && <button type="button" className="btn-secondary" onClick={() => void create()}>{t('Try again')}</button>}
                  {commitError.step && <button type="button" className="btn-secondary" onClick={() => goTo(commitError.step!)}>{t('Go to {step}', { step: t(stepCopy(commitError.step, mode).title) })}</button>}
                  {autosave.state === 'conflict' && <button type="button" className="btn-secondary" onClick={() => void reloadDraft()}>{t('Reload draft')}</button>}
                </div>
              </Notice></div>
            )}
            <div className="mt-6 flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 pt-4 dark:border-white/10">
              {position > 1
                ? <button type="button" className="btn-secondary" onMouseDown={keepFocus} onClick={back} disabled={!!busy}><ArrowLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />{t('Back')}</button>
                : <Link href={existing ? GUIDED_SETUP_HREF : '/setup?tab=companies'} className="btn-secondary">{t('Cancel')}</Link>}
              {!isLast
                ? <button type="button" className="btn-primary disabled:cursor-not-allowed disabled:opacity-50" onMouseDown={keepFocus} onClick={next} disabled={!!busy || locked}>
                    {busy === 'starting' ? t('Preparing the draft…') : nextLabel}<ArrowRight className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />
                  </button>
                : <button type="button" className="btn-primary disabled:cursor-not-allowed disabled:opacity-50" onClick={() => void create()} disabled={!!busy || !canCommit(plan) || planState === 'loading' || autosave.state === 'conflict'}>
                    {busy === 'creating'
                      ? (existing ? t('Saving all changes…') : t('Creating the company…'))
                      : existing ? (noChange ? t('Finish without changes') : t('Save all changes')) : t('Create company')}
                  </button>}
            </div>
            {isLast && plan && !canCommit(plan) && <p className="mt-2 text-end text-xs text-rose-700 dark:text-rose-300">{existing ? t('Save all changes is available once every conflict above is resolved.') : t('Create is available once every conflict above is resolved.')}</p>}
          </div>
          {step !== 7 && (
            <aside className="border-t border-slate-200 p-4 dark:border-white/10 lg:col-span-2 xl:col-span-1 xl:border-s xl:border-t-0" aria-label={t('Company structure')}>
              <details open className="group xl:pointer-events-none" data-testid="structure-panel">
                <summary className="cursor-pointer list-none text-sm font-semibold text-slate-900 dark:text-white xl:cursor-default">{t('Company structure')}<span className="ms-1 text-xs font-normal text-slate-500 xl:hidden">{t('(tap to show or hide)')}</span></summary>
                <div className="mt-3 xl:pointer-events-auto"><StructureTree tree={tree} /></div>
              </details>
            </aside>
          )}
        </div>
      </div>
    </div>
  );
}
