'use client';

import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { ArrowLeft, ArrowRight, Check, CheckCircle2, ChevronDown, Loader2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { msg } from '../../i18n/translations';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { rolesApi, type AccessCeiling } from '../../api/identity';
import {
  companySetupApi, httpStatus, type CompanySetupContext, type CompanySetupDraft, type CompanySetupDraftBody, type CompanySetupDraftSummary,
  type CompanySetupErrorBody, type CompanySetupPlan, type CompanySetupResult, type CompanySetupRowState, type OrgSection, type Problem,
} from '../../api/companySetup';
import { orgRowsApi, type OrgRowKind } from '../../api/orgRows';
import {
  GUIDED_SETUP_HREF, PART_COPY, STEP_IDS, buildTree, canCommit, completeCompanyHref, emptyBody, firstBlockingPosition, isOrgPart, normalizeFieldPath,
  partState, partsOf, planIsNoChange, positionKey, positionOfField, problemsByField, resumePart, sectionChanges, setupCodeCopy, stepCopy,
  stepIssueCounts, stepState, stepStateFromParts, validatePosition, withPart, wizardPositions,
  type FieldError, type PartId, type PartState, type StepId, type StepState, type WizardMode, type WizardPosition,
} from '../../lib/companySetupWizard';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { RovingTabList } from '../ui/RovingTabs';
import { WizardRail } from './WizardRail';
import { StructureTree } from './StructureTree';
import { StartStep, type StartChoice } from './StartStep';
import { CompanyStep } from './CompanyStep';
import { BranchesStep } from './BranchesStep';
import { OrganisationStep } from './OrganisationStep';
import { RulesStep } from './RulesStep';
import type { RulesPartId } from './rules/rulesShared';
import { AdminStep } from './AdminStep';
import { ReviewStep } from './ReviewStep';
import { CompanyReadinessList } from './CompanyReadinessList';
import { CompanySummaryStep } from './CompanySummaryStep';
import { MoreSettingsLinks } from './MoreSettingsLinks';
import { Notice, TagPill, WizardFormProvider, fieldId } from './wizardForm';
import { useDraftAutosave } from './useDraftAutosave';
import { ArabicSuggestionsProvider } from './ArabicSuggest';
import {
  SECTION_TITLE, STEP_SECTIONS, SectionSaveBar, SectionSaveProvider, SectionSavedStatus, sectionHeadingId, sectionOfField, sectionSnapshot,
  type SectionId, type SectionSaveValue, type SectionStatus as ExplicitStatus,
} from './sectionSave';
import { useNavigationBlocker } from '../../hooks/useNavigationBlocker';

/** Which Setup kind each Organisation section's rows are (Restore calls the same endpoint Setup does). */
const ORG_KIND: Record<OrgSection, OrgRowKind> = { costCentres: 'costCenter', departments: 'department', grades: 'grade', designations: 'designation' };

const STEP_HEADINGS: Record<StepId, { heading: string; help: string }> = {
  1: { heading: msg('Create a company'), help: msg('Each screen leads to the next. Save each section as you finish it. Nothing is created until you choose Create company at the end.') },
  2: { heading: msg('Company details'), help: msg('The legal entity. Fields marked * are needed to continue.') },
  3: { heading: msg('Branches & locations'), help: msg('Where this company’s people are based and punch in.') },
  4: { heading: msg('Organisation'), help: msg('This company’s own cost centres, departments, grades and designations. They are created with the company when you press Create company.') },
  5: { heading: msg('Work & pay rules'), help: msg('Confirm what the country pack sets up. Change only what is different for this company.') },
  6: { heading: msg('Company super admin'), help: msg('Choose who administers this company.') },
  7: { heading: msg('Review & create'), help: msg('This is exactly what Create will do. Nothing is created until you press Create company.') },
};

/** Existing mode (Addendum A) reads differently only where the journey differs: the summary, Organisation and the review. */
const EXISTING_HEADINGS: Partial<Record<StepId, { heading: string; help: string }>> = {
  1: { heading: msg('Complete setup for {company}'), help: msg('See what this company has today, then go step by step. Save each section as you finish it. Organisation parts are saved to the company when you press Save & next; everything else is applied by Save all changes.') },
  4: { heading: msg('Organisation'), help: msg('This company’s own cost centres, departments, grades and designations. Each part is saved to {company} when you press Save & next.') },
  7: { heading: msg('Review & update'), help: msg('These are exactly the changes Save all changes will make. Organisation rows you saved in Step 4 are already in the company.') },
};

/** Existing mode, Step 4 with changes: the primary action writes to the company now (B2), and says so. */
const SAVE_PART_LABEL: Record<OrgSection, string> = {
  costCentres: msg('Save cost centres & next'), departments: msg('Save departments & next'), grades: msg('Save grades & next'), designations: msg('Save designations & next'),
};
const SAVE_PART_ONLY: Record<OrgSection, string> = {
  costCentres: msg('Save cost centres'), departments: msg('Save departments'), grades: msg('Save grades'), designations: msg('Save designations'),
};
const COUNT_LABEL: Record<OrgSection, string> = {
  costCentres: msg('{count, plural, one {# cost centre} other {# cost centres}}'),
  departments: msg('{count, plural, one {# department} other {# departments}}'),
  grades: msg('{count, plural, one {# grade} other {# grades}}'),
  designations: msg('{count, plural, one {# designation} other {# designations}}'),
};

/** Step and part content rise in over 180 ms; never on keyboard navigation, never under reduced motion. */
const STEP_ENTER = 'motion-safe:animate-[fade-in_180ms_cubic-bezier(0.2,0,0,1)_both]';

type LoadState = 'loading' | 'ready' | 'error' | 'forbidden';
type SectionStatus =
  | { kind: 'saved'; part: OrgSection; created: number; updated: number; replayed: boolean }
  | { kind: 'kept'; part: OrgSection }
  | { kind: 'problems'; part: OrgSection; text: string }
  | { kind: 'network'; part: OrgSection; advance: boolean }
  | { kind: 'stale'; part: OrgSection };

const isMac = () => typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

/** The final layout, drawn before the data arrives, so nothing jumps when it does. */
function WizardSkeleton({ label }: { label: string }) {
  const bar = 'rounded-lg bg-slate-100 motion-safe:animate-pulse dark:bg-white/5';
  return (
    <div className="surface overflow-clip" role="status" aria-label={label} data-testid="wizard-skeleton">
      <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3 dark:border-white/10 sm:px-6"><div className={`h-4 w-40 ${bar}`} /><div className={`h-9 w-36 ${bar}`} /></div>
      <div className="grid lg:grid-cols-[13.5rem_minmax(0,1fr)] xl:grid-cols-[13.5rem_minmax(0,1fr)_17rem]">
        <div className="space-y-1 border-b border-slate-200 bg-slate-50/80 p-3 dark:border-white/10 dark:bg-white/[0.02] lg:border-b-0 lg:border-e lg:p-4">
          {Array.from({ length: 7 }, (_, i) => <div key={i} className={`h-11 ${bar}`} />)}
        </div>
        <div className="space-y-4 p-4 sm:p-6">
          <div className={`h-3 w-24 ${bar}`} /><div className={`h-6 w-48 ${bar}`} /><div className={`h-4 w-2/3 ${bar}`} />
          {Array.from({ length: 3 }, (_, i) => <div key={i} className={`h-9 ${bar}`} />)}
        </div>
        <div className="hidden space-y-2 border-s border-slate-200 p-4 dark:border-white/10 xl:block">
          {Array.from({ length: 6 }, (_, i) => <div key={i} className={`h-4 ${bar}`} />)}
        </div>
      </div>
    </div>
  );
}

/**
 * The company setup journey: one draft, seven steps, one commit. Drafts live on the server
 * (autosaved, versioned) so they survive sign-out and devices; the commit names the draft by id, which
 * is also the idempotency key, so a retry after a lost response returns the same result.
 *
 * One wizard, two modes (Addendum A): without `existingCompanyId` it creates a company; with it, it
 * completes setup for that active company — prefilled from what it has, adding and editing only.
 *
 * Steps 4 and 5 go one part at a time (Addendum B4). In existing mode each Organisation part is written to the
 * company by Save & next (B2); in create mode it stays in the draft until Create.
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
  const requestedPart = searchParams?.get('part') ?? null;

  const [load, setLoad] = useState<LoadState>('loading');
  const [context, setContext] = useState<CompanySetupContext | null>(null);
  const [drafts, setDrafts] = useState<CompanySetupDraftSummary[]>([]);
  const [ceiling, setCeiling] = useState<AccessCeiling | null | 'loading'>('loading');
  const [draftId, setDraftId] = useState<string | null>(null);
  const [draftMeta, setDraftMeta] = useState<{ createdByName: string; createdAtUtc: string } | null>(null);
  const [body, setBody] = useState<CompanySetupDraftBody>(() => emptyBody());
  const [defaults, setDefaults] = useState<CompanySetupDraftBody | null>(null);
  // Existing mode: per existing Organisation row, whether it is on and whether anything uses it (the server computes it on every read).
  const [rowStates, setRowStates] = useState<Record<string, CompanySetupRowState>>({});
  const [step, setStep] = useState<StepId>(1);
  const [part, setPart] = useState<PartId | undefined>(undefined);
  const [visited, setVisited] = useState<Set<StepId>>(() => new Set([1]));
  const [visitedParts, setVisitedParts] = useState<Set<string>>(() => new Set());
  /** Positions where Next was tried: their blockers show on every field. */
  const [attempted, setAttempted] = useState<Set<string>>(() => new Set());
  const [touched, setTouched] = useState<Set<string>>(() => new Set());
  const [choice, setChoice] = useState<StartChoice>({ startFrom: 'defaults', countryCode: 'SA' });
  const [busy, setBusy] = useState<'' | 'starting' | 'resuming' | 'exiting' | 'creating' | 'reloading' | 'saving'>('');
  const [error, setError] = useState('');
  const [plan, setPlan] = useState<CompanySetupPlan | null>(null);
  const [planState, setPlanState] = useState<'idle' | 'loading' | 'error'>('idle');
  const [result, setResult] = useState<CompanySetupResult | null>(null);
  const [commitError, setCommitError] = useState<{ message: string; retry: boolean; position?: WizardPosition; checkAgain?: boolean; leave?: boolean } | null>(null);
  const [confirmation, setConfirmation] = useState<ConfirmState | null>(null);
  /** B2: problems the server returned for a section save, shown on their fields until the next save. */
  const [sectionProblems, setSectionProblems] = useState<Problem[]>([]);
  const [sectionStatus, setSectionStatus] = useState<SectionStatus | null>(null);
  /** Parts written to the company in this visit, for the part header's Saved pill. */
  const [savedParts, setSavedParts] = useState<Set<OrgSection>>(() => new Set());
  const [enterMotion, setEnterMotion] = useState(true);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const focusField = useRef<string | null>(null);
  /** Per section: what its last explicit Save (or the draft as opened) held, and when it was saved by Save. */
  const [sectionRefs, setSectionRefs] = useState<Partial<Record<SectionId, { snapshot: string; at: string | null }>>>({});
  const [sectionAttempts, setSectionAttempts] = useState<Set<SectionId>>(() => new Set());
  const [sectionBusy, setSectionBusy] = useState<SectionId | null>(null);
  const [sectionMessages, setSectionMessages] = useState<Partial<Record<SectionId, string>>>({});
  const bodyRef = useRef(body);
  bodyRef.current = body;
  /** A draft as the server holds it: every section matches it. `at` is set when it was saved before (resumed). */
  const markSections = useCallback((saved: CompanySetupDraftBody, at: string | null) => {
    const refs: Partial<Record<SectionId, { snapshot: string; at: string | null }>> = {};
    for (const id of Object.values(STEP_SECTIONS).flat()) refs[id] = { snapshot: sectionSnapshot(saved, id), at };
    setSectionRefs(refs);
    setSectionAttempts(new Set());
    setSectionMessages({});
  }, []);

  const mounted = useRef(false);
  const [, setClock] = useState(0);
  // One structure tree, never two copies: a sticky panel at xl and up, a closed disclosure below.
  const [xl, setXl] = useState(false);
  useEffect(() => {
    const query = window.matchMedia?.('(min-width: 1280px)');
    if (!query) return;
    const sync = () => setXl(query.matches);
    sync();
    query.addEventListener('change', sync);
    return () => query.removeEventListener('change', sync);
  }, []);

  const autosave = useDraftAutosave({ draftId, body, step });
  const errorBody = (err: unknown) => (err as { response?: { data?: Partial<CompanySetupErrorBody> & { plan?: CompanySetupPlan | null } } } | null)?.response?.data;
  /** The server's refusal in the reader's language, or ''. A known code reads as what to do next. */
  const serverText = (err: unknown) => {
    const data = errorBody(err);
    const copy = setupCodeCopy(data?.code);
    if (copy) return t(copy);
    return (locale === 'ar' ? data?.messageAr || data?.message : data?.message) ?? '';
  };

  // "Saved · 2 min ago" stays true as time passes.
  useEffect(() => { const id = setInterval(() => setClock(c => c + 1), 30_000); return () => clearInterval(id); }, []);

  // ── Who may appoint a company super admin ──────────────────────────────────
  const groupAdmin = hasRole('Admin') && (!!context?.isGroupScope || existing);
  const manageable = existing ? context?.manageableCompanies?.find(c => c.id === existingCompanyId) ?? null : null;
  useEffect(() => {
    if (!groupAdmin) { setCeiling(null); return; }
    let active = true;
    rolesApi.ceiling().then(c => { if (active) setCeiling(c); }).catch(() => { if (active) setCeiling(null); });
    return () => { active = false; };
  }, [groupAdmin]);
  const adminRole = ceiling && ceiling !== 'loading' ? ceiling.roles.find(r => r.name.toLowerCase() === 'admin') : undefined;
  const serverGrant = context?.canGrantCompanyAdmin;
  const adminStepVisible = !existing && !context?.isGroupScope ? false : typeof serverGrant === 'boolean'
    ? serverGrant
    : groupAdmin && ceiling !== 'loading' && (adminRole ? adminRole.canAssign : true);
  const adminDecided = !context || typeof serverGrant === 'boolean' || !groupAdmin || ceiling !== 'loading';
  const steps = useMemo(() => STEP_IDS.filter(s => s !== 6 || adminStepVisible), [adminStepVisible]);
  const positions = useMemo(() => wizardPositions(steps), [steps]);

  useEffect(() => {
    if (adminDecided && !adminStepVisible && draftId && body.companyAdmin.mode !== 'groupOnly')
      setBody(b => ({ ...b, companyAdmin: { mode: 'groupOnly' } }));
  }, [adminDecided, adminStepVisible, draftId, body.companyAdmin.mode]);

  // ── Load context, drafts and (when linked) the draft to resume ─────────────
  const draftAddress = useCallback((id: string) => (existingCompanyId ? completeCompanyHref(existingCompanyId, id) : `/setup/new-company?draft=${id}`), [existingCompanyId]);
  /** Resolves true when the draft is on screen. */
  const openDraft = useCallback(async (id: string, urlPart?: string | null): Promise<boolean> => {
    setBusy('resuming'); setError('');
    try {
      const draft = await companySetupApi.draft(id);
      if (draft.status !== 'Open') {
        setError(draft.status === 'Submitted'
          ? (existingCompanyId ? t('This draft was already used to save changes. Start again from Guided setup.') : t('This draft was already used to create a company.'))
          : (existingCompanyId ? t('This draft was discarded. Start again from Guided setup.') : t('This draft was discarded. Start a new company instead.')));
        return false;
      }
      if ((draft.body.existingCompanyId ?? null) !== (existingCompanyId ?? null)) {
        setError(t('This draft belongs to a different setup. Open it from Guided setup.'));
        return false;
      }
      autosave.adopt(draft); setRowStates(draft.rowStates ?? {});
      setDraftId(draft.id);
      setDraftMeta({ createdByName: draft.createdByName, createdAtUtc: draft.createdAtUtc });
      setBody(draft.body);
      markSections(draft.body, draft.updatedAtUtc);
      const resumeStep = (Math.min(Math.max(draft.step, 1), 7)) as StepId;
      // Resume lands on the first part with a blocker, else the part the address names, else the first part.
      const resumeAt = resumePart(resumeStep, urlPart, { body: draft.body, context: null });
      setStep(resumeStep);
      setPart(resumeAt);
      setVisited(new Set(STEP_IDS.filter(s => s <= resumeStep)));
      setVisitedParts(new Set(wizardPositions(STEP_IDS).filter(p => p.part && (p.step < resumeStep || (p.step === resumeStep && partsOf(p.step).indexOf(p.part) <= partsOf(p.step).indexOf(resumeAt!)))).map(positionKey)));
      setChoice({ startFrom: draft.body.startFrom, countryCode: draft.body.company.countryCode, copyFromCompanyId: draft.body.copyFromCompanyId });
      router.replace(withPart(draftAddress(draft.id), resumeAt), { scroll: false });
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
          const target = ctx.manageableCompanies?.find(c => c.id === existingCompanyId);
          if (!target) { setLoad('forbidden'); return; }
          const resume = requestedDraft ?? target.openDraftId ?? null;
          if (resume) {
            setLoad('ready');
            void openDraft(resume, requestedPart).then(ok => { if (!ok && active) loadPrefill().catch(() => { if (active) setLoad('error'); }); });
            return;
          }
          loadPrefill().then(() => { if (active) setLoad('ready'); }).catch(() => { if (active) setLoad('error'); });
          return;
        }
        if (ctx.sisterCompanies.length > 0 && !requestedDraft) setChoice(c => ({ ...c, countryCode: ctx.sisterCompanies[0].countryCode || c.countryCode }));
        setLoad('ready');
        if (requestedDraft && ctx.canCreate) void openDraft(requestedDraft, requestedPart);
      })
      .catch(err => { if (active) setLoad(httpStatus(err) === 403 ? 'forbidden' : 'error'); });
    return () => { active = false; };
    // Load once per visit; openDraft is stable enough for the initial deep link.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ── Validation and messages ────────────────────────────────────────────────
  const input = { body, context, adminStepVisible, baseline: existing ? defaults : null };
  const allProblems = useMemo(() => [...(plan?.problems ?? []), ...sectionProblems], [plan, sectionProblems]);
  const serverProblems = useMemo(() => problemsByField(allProblems), [allProblems]);
  const clientErrors = useMemo(() => {
    const map = new Map<string, FieldError>();
    for (const p of positions) for (const e of validatePosition(p, { body, context, adminStepVisible, baseline: existing ? defaults : null })) if (!map.has(e.field)) map.set(e.field, e);
    return map;
  }, [positions, body, context, adminStepVisible, existing, defaults]);
  const errorFor = useCallback((field: string) => {
    const client = clientErrors.get(field);
    const section = sectionOfField(field);
    if (client && (touched.has(field) || attempted.has(positionKey(positionOfField(field))) || attempted.has(String(positionOfField(field).step)) || (section && sectionAttempts.has(section)))) return t(client.message, client.params);
    const server = serverProblems.get(field);
    if (server) {
      const copy = setupCodeCopy(server.code);
      return copy ? t(copy) : locale === 'ar' ? server.messageAr || server.message : server.message;
    }
    return undefined;
  }, [clientErrors, touched, attempted, sectionAttempts, serverProblems, locale, t]);

  const partStates = useMemo(() => {
    const out: Partial<Record<PartId, PartState>> = {};
    for (const p of positions) if (p.part) out[p.part] = partState(p.part, { ...input, visited: visitedParts, serverProblems: allProblems });
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [positions, body, context, adminStepVisible, visitedParts, allProblems, defaults]);
  const states = useMemo(() => {
    const out = {} as Record<StepId, StepState>;
    for (const s of STEP_IDS) {
      if (s === 7) out[s] = plan ? (canCommit(plan) ? 'done' : 'blocked') : visited.has(7) ? 'inProgress' : 'notStarted';
      else if (partsOf(s).length) out[s] = stepStateFromParts(partsOf(s).map(p => partStates[p] ?? 'notStarted'));
      else out[s] = stepState(s, { ...input, visited, serverProblems: plan?.problems });
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [body, context, adminStepVisible, visited, plan, defaults, partStates]);
  const issues = useMemo(() => {
    const out: Partial<Record<StepId, { fix: number; check: number }>> = {};
    for (const s of steps) out[s] = s === 7 ? { fix: (plan?.problems.length ?? 0) + (plan?.items.filter(i => i.action === 'conflict').length ?? 0), check: plan?.warnings.length ?? 0 } : stepIssueCounts(s, { ...input, serverProblems: allProblems });
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [steps, body, context, adminStepVisible, plan, allProblems, defaults]);

  const position: WizardPosition = part ? { step, part } : { step };
  const positionIndex = Math.max(0, positions.findIndex(p => positionKey(p) === positionKey(position)));
  const blocking = firstBlockingPosition(positions.filter(p => p.step !== 1 || !draftId), input);
  const blockingIndex = blocking ? positions.findIndex(p => positionKey(p) === positionKey(blocking)) : -1;
  const reachableAt = (p: WizardPosition) => !!draftId && (blockingIndex < 0 || positions.findIndex(x => positionKey(x) === positionKey(p)) <= blockingIndex);
  const firstOf = (s: StepId): WizardPosition => (partsOf(s).length ? { step: s, part: partsOf(s)[0] } : { step: s });
  const titleOf = (p: WizardPosition) => (p.part ? t(PART_COPY[p.part].title) : t(stepCopy(p.step, mode).title));

  // ── Navigation ─────────────────────────────────────────────────────────────
  useEffect(() => {
    // Not on first mount: the page's own heading and back link come first for someone arriving.
    if (!mounted.current) { mounted.current = true; return; }
    if (focusField.current) {
      const el = document.getElementById(fieldId(focusField.current));
      focusField.current = null;
      if (el) { el.focus(); return; }
    }
    headingRef.current?.focus({ preventScroll: false });
  }, [step, part, attempted]);

  /** Step 5's embedded editors can hold an unsaved form: RulesStep fills this in, and leaving the part asks first. */
  const rulesLeaveRef = useRef<(() => Promise<boolean>) | null>(null);
  const goToPosition = (target: WizardPosition, opts: { viaKeyboard?: boolean } = {}) => {
    const confirm = step === 5 ? rulesLeaveRef.current : null;
    if (confirm) { void confirm().then(ok => { if (ok) moveTo(target, opts); }); return; }
    moveTo(target, opts);
  };
  const moveTo = (target: WizardPosition, opts: { viaKeyboard?: boolean } = {}) => {
    const resolved: WizardPosition = partsOf(target.step).length && !target.part ? firstOf(target.step) : target;
    if (positionKey(resolved) === positionKey(position)) return;
    setEnterMotion(!opts.viaKeyboard);
    setVisited(v => new Set([...v, resolved.step]));
    if (resolved.part) setVisitedParts(v => new Set([...v, positionKey(resolved)]));
    setStep(resolved.step);
    setPart(resolved.part);
    setError('');
    setCommitError(null);
    setSectionStatus(s => (s && (s.kind === 'saved' || s.kind === 'kept') ? s : null));
    if (draftId) router.replace(withPart(draftAddress(draftId), resolved.part), { scroll: false });
  };
  /** Kept for the step-only callers (summary, review): opens a step at its first part. */
  const goTo = (target: StepId) => goToPosition(firstOf(target));

  /** A rail click on something not reachable yet opens what blocks it, with its first blocker focused. */
  const openBlocking = () => {
    if (!blocking) return;
    const errors = validatePosition(blocking, input);
    if (errors[0]) focusField.current = errors[0].field;
    setAttempted(a => new Set([...a, positionKey(blocking)]));
    goToPosition(blocking);
  };
  const selectStep = (s: StepId) => {
    if (s === step) return;
    const target = partsOf(s).length ? { step: s, part: resumePart(s, null, input) } : { step: s };
    if (reachableAt(target)) goToPosition(target); else if (reachableAt(firstOf(s))) goToPosition(firstOf(s)); else openBlocking();
  };
  const selectPart = (p: PartId) => {
    const target = { step, part: p };
    if (reachableAt(target)) goToPosition(target); else openBlocking();
  };

  /** From the review: open the step (and part) a problem names, and focus its field. */
  const goToField = (target: StepId, field?: string) => {
    if (field) focusField.current = normalizeFieldPath(field);
    const at = field ? positionOfField(field, target) : firstOf(target);
    goToPosition(at.step === target ? at : firstOf(target));
  };

  const refreshPlan = useCallback(async (forBody: CompanySetupDraftBody) => {
    setPlanState('loading');
    try { const next = await companySetupApi.dryRun(forBody); planBody.current = forBody; setPlan(next); setPlanState('idle'); }
    catch { setPlanState('error'); }
  }, []);

  const planBody = useRef<CompanySetupDraftBody | null>(null);
  useEffect(() => {
    if (plan && planBody.current && planBody.current !== body && step !== 5) setPlan(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [body]);

  useEffect(() => {
    if (!draftId || (step !== 5 && step !== 7)) return;
    const timer = setTimeout(() => { void refreshPlan(body); }, step === 7 ? 0 : 400);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step, draftId]);

  const adoptNew = (draft: CompanySetupDraft) => {
    autosave.adopt(draft); setRowStates(draft.rowStates ?? {});
    setDraftId(draft.id);
    setDraftMeta({ createdByName: draft.createdByName, createdAtUtc: draft.createdAtUtc });
    setBody(draft.body);
    markSections(draft.body, null);
    setDrafts(list => [{ id: draft.id, legalNameEn: draft.body.company.legalNameEn, step: draft.step, updatedAtUtc: draft.updatedAtUtc, createdByName: draft.createdByName, existingCompanyId: draft.body.existingCompanyId ?? null }, ...list]);
    router.replace(draftAddress(draft.id), { scroll: false });
    setVisited(v => new Set([...v, 2]));
    setStep(2);
    setPart(undefined);
  };

  const startExisting = async () => {
    if (!existingCompanyId || !defaults) return;
    setBusy('starting'); setError('');
    try {
      adoptNew(await companySetupApi.createDraft({ ...body, existingCompanyId }));
    } catch (err) {
      const status = httpStatus(err);
      if (status === 409) {
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

  const nextPosition = positions[positionIndex + 1];
  const prevPosition = positionIndex > 0 ? positions[positionIndex - 1] : undefined;
  const orgPart = isOrgPart(part) ? part : null;
  const changes = orgPart ? sectionChanges(body, defaults, orgPart) : { added: 0, changed: 0, removed: 0 };
  /** Existing mode: this Organisation part has rows to add or change, so Save & next writes them (B2). */
  const writesNow = existing && !!orgPart && !!draftId && (changes.added + changes.changed + changes.removed > 0);

  /**
   * B2: write the current Organisation part to the company. Flushes the draft first (the server saves what it holds),
   * adopts the rebased draft it returns, and advances only on success — never optimistically.
   */
  const saveSection = async (advance: boolean) => {
    if (!draftId || !orgPart) return;
    const section = orgPart;
    setBusy('saving'); setSectionStatus(null); setSectionProblems([]);
    const saved = await autosave.flush();
    if (!saved) {
      setBusy('');
      setSectionStatus(autosave.isConflict() ? { kind: 'stale', part: section } : { kind: 'network', part: section, advance });
      return;
    }
    try {
      const res = await companySetupApi.applySection(draftId, section, autosave.version());
      autosave.adopt(res.draft); setRowStates(res.draft.rowStates ?? {});
      setBody(res.draft.body);
      // The saved rows are now what the company has: "Changed" resets, and the next save compares against them.
      setDefaults(d => (d ? { ...d, [section]: res.draft.body[section] } : d));
      setPlan(null);
      setSavedParts(s => new Set([...s, section]));
      setSectionStatus({ kind: 'saved', part: section, created: res.created, updated: res.updated, replayed: res.replayed });
      setBusy('');
      if (advance && nextPosition) goToPosition(nextPosition);
    } catch (err) {
      setBusy('');
      const status = httpStatus(err);
      const data = errorBody(err);
      if (status === 409 && data?.code === 'draft_version_stale') { setSectionStatus({ kind: 'stale', part: section }); return; }
      if (status === 409 || status === 422 || status === 400 || status === 403) {
        const problems: Problem[] = data?.plan?.problems?.length
          ? data.plan.problems
          : data?.field ? [{ step: 4, field: data.field, code: data.code ?? '', message: data.message ?? '', messageAr: data.messageAr }] : [];
        setSectionProblems(problems);
        setSectionStatus({ kind: 'problems', part: section, text: serverText(err) || t('{part} were not saved. Fix what is marked, then save again.', { part: t(PART_COPY[section].title) }) });
        const first = problems.map(p => normalizeFieldPath(p.field)).find(f => positionOfField(f).part === section);
        if (first) { focusField.current = first; setAttempted(a => new Set([...a, positionKey(position)])); }
        return;
      }
      setSectionStatus({ kind: 'network', part: section, advance });
    }
  };

  const next = (viaKeyboard = false) => {
    if (busy) return;
    if (step === 1 && !draftId) { void start(); return; }
    const errors = validatePosition(position, input);
    if (errors.length) {
      focusField.current = errors[0].field;
      setAttempted(a => new Set([...a, positionKey(position)]));
      return;
    }
    if (writesNow) { void saveSection(true); return; }
    if (orgPart && draftId) {
      // Create mode (or nothing changed): the rows stay in the draft until Create; send it now so nothing waits.
      void autosave.flush();
      if (!existing) setSectionStatus({ kind: 'kept', part: orgPart });
    }
    if (nextPosition) goToPosition(nextPosition, { viaKeyboard });
  };
  const keepFocus = (event: React.MouseEvent) => event.preventDefault();
  const back = () => { if (prevPosition) goToPosition(prevPosition); };

  /** Leaving the wizard inside the app: send the draft first, then go. */
  const leaveTo = async (href: string) => {
    if (draftId) await autosave.flush();
    router.push(href);
  };

  const saveAndExit = async () => {
    setBusy('exiting'); setError('');
    const ok = await autosave.flush();
    setBusy('');
    if (ok) router.push(GUIDED_SETUP_HREF);
    else if (!autosave.isConflict()) setError(t('The draft could not be saved, so you are still here. Check your connection and try again.'));
  };

  const reloadDraft = async () => {
    if (!draftId) return;
    setBusy('reloading');
    try {
      const draft = await companySetupApi.draft(draftId);
      autosave.adopt(draft); setRowStates(draft.rowStates ?? {});
      setBody(draft.body);
      markSections(draft.body, draft.updatedAtUtc);
      const s = (Math.min(Math.max(draft.step, 1), 7)) as StepId;
      setStep(s);
      setPart(partsOf(s).includes(part as PartId) ? part : partsOf(s)[0]);
      setPlan(null);
      setSectionStatus(null);
      setSectionProblems([]);
    } catch { setError(t('The draft could not be reloaded. Check your connection and try again.')); }
    finally { setBusy(''); }
  };

  const changeAndCheck = (change: (b: CompanySetupDraftBody) => CompanySetupDraftBody) => {
    const nextBody = change(body);
    setBody(nextBody);
    void refreshPlan(nextBody);
  };

  const reloadFromCompany = () => {
    if (!existingCompanyId) return;
    setConfirmation({
      title: t('Reload the company’s current setup?'),
      message: t('This draft was saved before KynexOne recorded what {company} had when it started, so it cannot tell your edits from changes made elsewhere. The draft is replaced with what the company has today; then make your changes again. Nothing has been changed yet.', { company: body.company.legalNameEn }),
      confirmLabel: t('Replace my draft'),
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
      try {
        await companySetupApi.discardDraft(summary.id);
        setDrafts(list => list.filter(d => d.id !== summary.id));
      } catch {
        setError(t('The draft could not be discarded. Check your connection and try again.'));
      }
      setConfirmation(null);
    },
  });

  const discardCurrent = () => {
    if (!draftId || !existingCompanyId) return;
    setConfirmation({
      title: t('Discard this draft?'),
      message: t('The changes in this draft are dropped. Nothing was changed for {company}.', { company: body.company.legalNameEn }),
      confirmLabel: t('Discard draft'),
      onConfirm: async () => {
        try { await companySetupApi.discardDraft(draftId); }
        catch { setConfirmation(null); setError(t('The draft could not be discarded. Check your connection and try again.')); return; }
        setConfirmation(null);
        setDraftId(null); setDraftMeta(null); setPlan(null);
        setVisited(new Set([1])); setVisitedParts(new Set()); setAttempted(new Set()); setTouched(new Set()); setSectionRefs({});
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
      const conflict = autosave.isConflict();
      setCommitError({ message: conflict
        ? (existing ? t('This draft was changed elsewhere. Reload it, check the review again, then update.') : t('This draft was changed elsewhere. Reload it, check the review again, then create.'))
        : (existing ? t('The latest changes could not be saved, so nothing was changed. Check your connection and try again.') : t('The latest changes could not be saved, so nothing was created. Check your connection and try again.')), retry: !conflict });
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
      const code = data?.code ?? '';
      if (code === 'draft_version_stale') {
        // The review is out of date: check it again (new version, new plan) rather than go to a step.
        setCommitError({ message: serverMessage || t('This draft was changed after you reviewed it. Check it again before you continue.'), retry: false, checkAgain: true });
      } else if (code === 'draft_discarded' || code === 'draft_company_removed' || status === 404) {
        setCommitError({ message: serverMessage || t('This draft can no longer be used. Start again from Guided setup.'), retry: false, leave: true });
      } else if (code === 'idempotency_key_mismatch') {
        setCommitError({ message: serverMessage || t('The request did not match this draft. Try again: retrying never applies it twice.'), retry: true });
      } else if (status === 402 || code === 'company_limit_reached') {
        setCommitError({ message: t('Your plan’s company limit is reached. Nothing was created.'), retry: false });
      } else if (status === 409 || status === 422 || status === 400) {
        const target: WizardPosition = data?.field ? positionOfField(data.field, (data.step as StepId) || 2)
          : data?.step && steps.includes(data.step as StepId) && data.step !== 7 ? firstOf(data.step as StepId) : { step: 2 };
        setCommitError({ message: serverMessage || (existing
          ? t('The changes could not be saved because something in the draft clashes with an existing record. Nothing was changed.')
          : t('The company could not be created because something in the draft clashes with an existing record. Nothing was created.')), retry: false, position: target });
        void refreshPlan(body);
      } else if (status === 403) {
        setCommitError({ message: serverMessage || (existing ? t('You no longer have permission to change this company’s setup. Nothing was changed. Ask your group super admin.') : t('You no longer have permission to create companies. Nothing was created. Ask your group super admin.')), retry: false });
      } else {
        setCommitError({ message: existing
          ? t('We could not confirm whether the changes were saved. Try again: retrying never applies them twice.')
          : t('We could not confirm whether the company was created. Try again: retrying never creates a second company.'), retry: true });
      }
    } finally { setBusy(''); }
  };

  // ── Per-section Save (on top of the background autosave) ───────────────────
  const stepSections = (s: StepId) => (steps.includes(s) ? STEP_SECTIONS[s] : []);
  /** The one explicitly saved section on screen: company details, branches, work rules (its part) or the super admin. */
  const positionSection: SectionId | null = step === 5 ? (part === 'workRules' ? 'rules' : null) : (stepSections(step)[0] ?? null);
  const explicitStatus = (id: SectionId): ExplicitStatus => {
    const ref = sectionRefs[id];
    if (!ref) return { kind: 'notSaved' };
    if (sectionSnapshot(body, id) !== ref.snapshot) return { kind: 'unsaved' };
    return ref.at ? { kind: 'saved', at: ref.at } : { kind: 'notSaved' };
  };
  const saveExplicit = async (id: SectionId) => {
    if (!draftId || sectionBusy) return;
    setSectionMessages(p => ({ ...p, [id]: undefined }));
    // This section's own checks only; the rest of the step is checked by Next.
    const errors = validatePosition(position, input).filter(e => sectionOfField(e.field) === id);
    if (errors.length) {
      setSectionAttempts(a => new Set([...a, id]));
      setSectionMessages(p => ({ ...p, [id]: t('{count, plural, one {# field needs} other {# fields need}} attention in this section. Nothing was saved.', { count: errors.length }) }));
      requestAnimationFrame(() => document.getElementById(fieldId(errors[0].field))?.focus());
      return;
    }
    setSectionBusy(id);
    const ok = await autosave.flush();
    setSectionBusy(null);
    if (!ok) {
      setSectionMessages(p => ({ ...p, [id]: autosave.isConflict()
        ? t('This draft was changed elsewhere, so this section was not saved. Reload the draft, then save the section again.')
        : t('This section could not be saved. Your changes are still here. Check your connection and try again.') }));
      return;
    }
    const saved = bodyRef.current;
    setSectionRefs(r => ({ ...r, [id]: { snapshot: sectionSnapshot(saved, id), at: new Date().toISOString() } }));
    if (nextPosition) goToPosition(nextPosition);
  };
  /** The rail: a visited step with sections is Saved only when every section of it is. */
  const savedStates = {} as Record<StepId, 'saved' | 'unsaved' | null>;
  for (const s of STEP_IDS) {
    const ids = stepSections(s);
    savedStates[s] = !draftId || ids.length === 0 || !visited.has(s) ? null : ids.every(id => explicitStatus(id).kind === 'saved') ? 'saved' : 'unsaved';
  }
  const unsavedSections = draftId && !result ? steps.flatMap(stepSections).filter(id => explicitStatus(id).kind === 'unsaved') : [];
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
    status: explicitStatus,
    save: id => { void saveExplicit(id); },
    busy: sectionBusy,
    problem: id => sectionMessages[id] ?? null,
    isLast: () => true,
    disabled: !draftId || autosave.state === 'conflict' || (existing ? false : !context?.canCreate),
  };

  // Existing mode is not limited by the create gates (plan limit, creation mode): the company already exists.
  const locked = existing ? false : !context?.canCreate;

  /** A stale review: read the draft again (its version) and plan it again. */
  const checkAgain = async () => {
    if (!draftId) return;
    setCommitError(null);
    try {
      const draft = await companySetupApi.draft(draftId);
      autosave.adopt(draft); setRowStates(draft.rowStates ?? {});
      setBody(draft.body);
      await refreshPlan(draft.body);
    } catch { setError(t('The draft could not be reloaded. Check your connection and try again.')); }
  };

  // The outcome is announced by moving focus to its heading.
  useEffect(() => { if (result) headingRef.current?.focus(); }, [result]);

  /** Cmd/Ctrl+Enter is the primary action. Plain Enter never is; nothing fires while an IME is composing. */
  const onShortcut = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Enter' || !(event.metaKey || event.ctrlKey) || event.nativeEvent.isComposing) return;
    if (confirmation || result) return;
    event.preventDefault();
    if (step === 7) { if (canCommit(plan) && !busy && autosave.state !== 'conflict') void create(); }
    else if (!locked) next(true);
  };

  // ── Render ─────────────────────────────────────────────────────────────────
  if (load === 'loading') return <WizardSkeleton label={t('Opening company setup…')} />;
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
      <Link href={GUIDED_SETUP_HREF} className="btn-secondary mt-3">{t('Back to Guided setup')}</Link>
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
    const sum = (counts?: Partial<Record<string, number>> | null) => Object.values(counts ?? {}).reduce<number>((a, n) => a + (n ?? 0), 0);
    const created = sum(result.created);
    const updated = sum(result.updated);
    const confirmedOnly = existing && created === 0 && updated === 0;
    // The one next action: the first thing still to do, when there is one.
    const todo = result.readiness.find(i => i.status === 'todo');
    const doneHref = `/setup?tab=aiSetup&path=configuration&companyId=${result.companyId}`;
    return (
      <div className="surface space-y-6 p-5 sm:p-8" data-testid="create-company-success" data-mode={mode}>
        <div className="flex items-start gap-3">
          <CheckCircle2 className="mt-1 h-7 w-7 shrink-0 text-emerald-600 motion-safe:animate-scale-in" aria-hidden="true" />
          <div className="min-w-0">
            <h2 ref={headingRef} tabIndex={-1} className="text-xl font-semibold text-slate-950 outline-none dark:text-white"><bdi>{
              confirmedOnly ? t('Setup for {name} is confirmed', { name }) : existing ? t('{name} is updated', { name }) : t('{name} is created', { name })}</bdi></h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{confirmedOnly
              ? t('Nothing needed changing. The draft is closed.')
              : existing
                ? (result.replayed ? t('These changes were already saved from this draft; here is the same result.') : t('The changes were saved together. Finish the items below before the next payroll.'))
                : (result.replayed ? t('This company was already created from this draft; here is the same result.') : t('The company and its setup were created together. Finish the items below before the first payroll.'))}</p>
            {!confirmedOnly && <p className="mt-1 text-sm font-medium text-slate-800 tabular-nums dark:text-slate-200" data-testid="result-counts">{existing
              ? t('{created} added · {updated} updated', { created, updated })
              : t('{count, plural, one {# record created} other {# records created}}', { count: created })}</p>}
          </div>
        </div>
        <CompanyReadinessList items={result.readiness} />
        <details className="rounded-xl border border-slate-200 dark:border-white/10">
          <summary className="cursor-pointer px-4 py-3 text-sm font-semibold text-slate-900 dark:text-white">{t('More settings for this company')}</summary>
          <div className="px-4 pb-4"><MoreSettingsLinks companyId={result.companyId} /></div>
        </details>
        {/* The one primary is the readiness list's "Next: {item}"; these only leave. */}
        <div className="flex flex-wrap gap-2">
          {!existing && <Link href={doneHref} className="btn-secondary">{t('Open the new company’s setup')}</Link>}
          <Link href={GUIDED_SETUP_HREF} className={todo ? 'btn-secondary' : 'btn-primary'}>{t('Back to Guided setup')}</Link>
        </div>
      </div>
    );
  }

  const tree = buildTree(body, context);
  const stepPosition = steps.indexOf(step) + 1;
  const isLast = step === 7;
  const savedRecently = autosave.savedAt && Date.now() - new Date(autosave.savedAt).getTime() < 60_000;
  const saveLabel = {
    // The background copy of the whole draft. Explicit Save, per section, is how progress is confirmed.
    idle: '', pending: t('Backing up the draft…'), saving: t('Backing up the draft…'),
    saved: autosave.savedAt ? (savedRecently ? t('Draft backed up · just now') : t('Draft backed up {when}', { when: fmt.time(autosave.savedAt) })) : t('Draft backed up'),
    conflict: '', error: t('Not saved yet. Your changes are kept on this screen; we will try again when you next change something.'),
  }[autosave.state];
  // A quiet live region: it speaks only when a save lands or fails, never on every keystroke.
  const liveSave = autosave.state === 'saved' ? t('Draft saved') : autosave.state === 'error' ? t('The draft could not be saved yet.') : '';
  const positionErrors = attempted.has(positionKey(position)) || attempted.has(String(step)) ? validatePosition(position, input) : [];
  const noChange = existing && planIsNoChange(plan);
  const heading = (existing ? EXISTING_HEADINGS[step] : undefined) ?? STEP_HEADINGS[step];
  const companyName = (locale === 'ar' && body.company.legalNameAr?.trim()) || body.company.legalNameEn || manageable?.legalNameEn || '';
  const headingParams = { company: companyName };
  const stepParts = partsOf(step);
  const partNumber = part ? stepParts.indexOf(part) + 1 : 0;
  const nextTitle = nextPosition ? titleOf(nextPosition.step === step ? nextPosition : { step: nextPosition.step }) : '';
  const primaryLabel = step === 1 && !draftId ? t('Start')
    : writesNow ? t(SAVE_PART_LABEL[orgPart!])
    : part && nextPosition ? (nextPosition.step === step ? t('Next: {part}', { part: nextTitle }) : t('Next: {step}', { step: nextTitle }))
    : t('Next');
  const backTitle = prevPosition ? titleOf(prevPosition) : '';
  const shortcut = isMac() ? '⌘↵' : 'Ctrl ↵';
  const structureOnStep4Only = step === 4;
  const strip = ([...partsOf(4)] as OrgSection[]).map(p => t(COUNT_LABEL[p], { count: body[p].length })).join(' · ');

  const statusLine = (() => {
    const s = sectionStatus;
    if (!s) return null;
    const name = t(PART_COPY[s.part].title);
    if (s.kind === 'saved') return (
      <span className="inline-flex min-w-0 items-center gap-1.5 text-emerald-800 dark:text-emerald-200" data-testid="section-saved">
        <Check className="h-3.5 w-3.5 shrink-0 motion-safe:animate-scale-in" aria-hidden="true" />
        <span className="truncate">{s.replayed ? t('{part} were already saved.', { part: name }) : t('{part} saved · {created} added, {updated} changed', { part: name, created: s.created, updated: s.updated })}</span>
      </span>
    );
    if (s.kind === 'kept') return <span className="text-slate-600 dark:text-slate-400">{t('{part} kept in the draft. They are created when you press Create company.', { part: name })}</span>;
    return null;
  })();

  const sectionNotice = sectionStatus && (sectionStatus.kind === 'network' || sectionStatus.kind === 'problems' || sectionStatus.kind === 'stale') && (
    <div className="mt-4" data-testid="section-error"><Notice tone="error" role="alert">
      {sectionStatus.kind === 'network' && <>
        <p className="font-semibold">{t('Not saved. Your changes are still here.')}</p>
        <p className="mt-1">{t('Check your connection and try again. Trying again never saves twice.')}</p>
        <button type="button" className="btn-secondary mt-2" onClick={() => void saveSection(sectionStatus.advance)}>{t('Try again')}</button>
      </>}
      {sectionStatus.kind === 'problems' && <>
        <p className="font-semibold">{sectionStatus.text}</p>
        <ul className="mt-1 list-disc space-y-0.5 ps-5">
          {sectionProblems.slice(0, 6).map((p, i) => {
            const field = normalizeFieldPath(p.field);
            const where = positionOfField(field, 4);
            const copy = setupCodeCopy(p.code);
            const text = copy ? t(copy) : (locale === 'ar' ? p.messageAr || p.message : p.message);
            return (
              <li key={`${field}-${i}`}>
                {where.part === part
                  ? <a className="underline" href={`#${fieldId(field)}`} onClick={ev => { ev.preventDefault(); document.getElementById(fieldId(field))?.focus(); }}>{text}</a>
                  : <>{text}{' '}<button type="button" className="font-semibold underline" onClick={() => { focusField.current = field; goToPosition(where); }}>{t('Go to {step}', { step: titleOf(where) })}</button></>}
              </li>
            );
          })}
          {sectionProblems.length > 6 && <li>{t('{count} more not shown', { count: sectionProblems.length - 6 })}</li>}
        </ul>
      </>}
      {sectionStatus.kind === 'stale' && <>
        <p className="font-semibold">{t('This draft was changed elsewhere — reload')}</p>
        <p className="mt-1">{t('Nothing was saved to the company. Reload to see the latest draft, then save again.')}</p>
        <button type="button" className="btn-secondary mt-2" disabled={busy === 'reloading'} onClick={() => void reloadDraft()}>{t('Reload draft')}</button>
      </>}
    </Notice></div>
  );

  const treeView = <StructureTree tree={tree} collapsible activePart={isOrgPart(part) ? part : undefined} activeGroup={step === 3 ? 'branches' : undefined}
    onJump={p => selectPart(p)} canJump={p => step === 4 && reachableAt({ step: 4, part: p })} />;
  const structureAside = (
    <aside className={`border-t border-slate-200 p-4 dark:border-white/10 lg:col-span-2 xl:border-s xl:border-t-0 ${structureOnStep4Only ? 'hidden 2xl:col-span-1 2xl:block' : 'xl:col-span-1'}`} aria-label={t('Company structure')} data-testid="structure-panel">
      {xl ? (
        <div className="sticky top-4 max-h-[calc(100dvh-6rem)] overflow-y-auto overscroll-contain pb-20">
          <h2 className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Company structure')}</h2>
          {treeView}
        </div>
      ) : (
        <details className="group">
          <summary className="flex cursor-pointer list-none items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
            {t('Company structure')}
            <ChevronDown className="h-4 w-4 text-slate-500 transition-transform duration-200 group-open:rotate-180 motion-reduce:transition-none" aria-hidden="true" />
            <span className="sr-only">{t('Show or hide')}</span>
          </summary>
          <div className="mt-3">{treeView}</div>
        </details>
      )}
    </aside>
  );

  return (
    <div className="space-y-4" data-testid="create-company-wizard" onKeyDown={onShortcut}>
      <ConfirmDialog state={confirmation} onCancel={() => setConfirmation(null)} />
      <div className="surface overflow-clip">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3 dark:border-white/10 sm:px-6">
          <div className="min-w-0 text-xs text-slate-600 dark:text-slate-300">
            {draftId
              ? <p className="flex min-w-[9rem] items-center gap-1.5 tabular-nums" data-testid="autosave-status" data-state={autosave.state} title={autosave.savedAt ? fmt.dateTime(autosave.savedAt) : undefined}>
                  {autosave.state === 'saved' && <Check key={autosave.savedAt ?? ''} className="h-3 w-3 text-emerald-600 motion-safe:animate-scale-in" aria-hidden="true" />}
                  <span>{saveLabel || t('Draft backed up')}</span>
                  {autosave.state === 'error' && <button type="button" className="ms-1 rounded px-1 font-semibold text-blue-700 hover:underline dark:text-primary-fg" onClick={() => void autosave.flush()}>{t('Retry now')}</button>}
                  {draftMeta && <span className="hidden sm:inline">{' · '}{t('Started by {name}', { name: draftMeta.createdByName })}</span>}
                </p>
              : <p>{existing ? t('A draft is saved as soon as you start, and kept until you save the changes or discard it.') : t('A draft is saved as soon as you start, and kept until you create the company or discard it.')}</p>}
            <span className="sr-only" aria-live="polite">{liveSave}</span>
          </div>
          {draftId && <button type="button" className="btn-secondary text-slate-700 dark:text-slate-200" disabled={!!busy} onClick={() => void saveAndExit()}>{busy === 'exiting' ? t('Saving…') : t('Save draft & exit')}</button>}
        </div>
        {autosave.state === 'conflict' && (
          <div className="border-b border-amber-200 px-4 py-3 dark:border-amber-400/30 sm:px-6">
            <Notice tone="warning" role="alert">
              <p className="font-semibold">{t('This draft was changed elsewhere — reload')}</p>
              <p className="mt-1">{t('Someone saved this draft in another tab or device after you opened it. Reload to see their version; changes made here since then are not saved.')}</p>
              <button type="button" className="btn-secondary mt-2" disabled={busy === 'reloading'} onClick={() => void reloadDraft()}>{t('Reload draft')}</button>
            </Notice>
          </div>
        )}
        <div className={`grid lg:grid-cols-[13.5rem_minmax(0,1fr)] ${structureOnStep4Only ? '2xl:grid-cols-[13.5rem_minmax(0,1fr)_17rem]' : 'xl:grid-cols-[13.5rem_minmax(0,1fr)_17rem]'}`}>
          <aside className="setup-step-rail border-b border-slate-200 bg-slate-50/80 p-3 dark:border-white/10 dark:bg-white/[0.02] lg:border-b-0 lg:border-e lg:p-4">
            <WizardRail steps={steps} current={step} currentPart={part} states={states} issues={issues} partStates={partStates} saved={savedStates}
              reachable={s => reachableAt(firstOf(s))} reachablePart={p => reachableAt({ step, part: p })}
              blockingTitle={blocking ? titleOf(blocking) : undefined}
              onSelect={selectStep} onSelectPart={selectPart} disabled={!!busy} mode={mode}
              progress={positions.length > 1 ? positionIndex / (positions.length - 1) : 0} />
          </aside>
          <div className={`min-w-0 p-4 sm:p-6 ${step === 7 ? 'xl:col-span-2' : ''}`}>
            <div key={positionKey(position)} className={enterMotion && step !== 5 ? STEP_ENTER : ''} data-testid="wizard-position" data-position={positionKey(position)}>
              <div className="mb-6">
                <p className="hidden text-xs font-semibold uppercase tracking-wide text-slate-600 rtl:normal-case rtl:tracking-normal rtl:text-body-sm dark:text-slate-400 lg:block">
                  {part
                    ? t('Step {position} of {total} · Part {part} of {parts}', { position: stepPosition, total: steps.length, part: partNumber, parts: stepParts.length })
                    : t('Step {position} of {total}', { position: stepPosition, total: steps.length })}
                </p>
                <div className="mt-1 flex flex-wrap items-center gap-2">
                  <h2 ref={headingRef} tabIndex={-1} className="scroll-mt-24 text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white">
                    <bdi>{part ? t(PART_COPY[part].title) : t(heading.heading, headingParams)}</bdi>
                  </h2>
                  {orgPart && existing && savedParts.has(orgPart) && !writesNow && <TagPill tag="saved" />}
                  {draftId && positionSection && <SectionSaveProvider value={sectionSave}><SectionSavedStatus section={positionSection} /></SectionSaveProvider>}
                </div>
                <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400">{part ? t(PART_COPY[part].purpose) : t(heading.help, headingParams)}</p>
                {step === 4 && <p className="mt-1 max-w-3xl text-xs leading-5 text-slate-600 dark:text-slate-400">{t(heading.help, headingParams)}</p>}
                {stepParts.length > 0 && part && (
                  <div className="mt-3 lg:hidden">
                    <RovingTabList<PartId> items={stepParts.map(p => ({ id: p, label: t(PART_COPY[p].title), testId: `part-tab-${p}` }))}
                      activeId={part} onChange={p => selectPart(p)} idPrefix="ccw-parts" label={t('Parts of {step}', { step: t(stepCopy(step, mode).title) })} compact />
                  </div>
                )}
                {structureOnStep4Only && <p className="mt-3 text-xs text-slate-600 tabular-nums dark:text-slate-400 2xl:hidden" data-testid="structure-strip">{strip}</p>}
              </div>
              {error && <div className="mb-4"><Notice tone="error" role="alert">{error}</Notice></div>}
              {positionErrors.length > 0 && (
                <div className="mb-4"><Notice tone="error" role="alert">
                  <p className="font-semibold">{t('{count, plural, one {# field needs} other {# fields need}} attention before you continue.', { count: positionErrors.length })}</p>
                  <ul className="mt-1 list-disc ps-5">
                    {positionErrors.slice(0, 6).map(e => <li key={e.field}><a className="underline" href={`#${fieldId(e.field)}`} onClick={ev => { ev.preventDefault(); document.getElementById(fieldId(e.field))?.focus(); }}>{t(e.message, e.params)}</a></li>)}
                    {positionErrors.length > 6 && <li>{t('{count} more not shown', { count: positionErrors.length - 6 })}</li>}
                  </ul>
                </Notice></div>
              )}
              <WizardFormProvider value={{ body, update: change => setBody(b => change(b)), errorFor, context, readOnly: locked || autosave.state === 'conflict' || busy === 'saving', baseline: existing ? defaults : null, rowStates,
                restoreRow: async (section, id) => {
                  await orgRowsApi.restore(ORG_KIND[section], id);
                  setRowStates(r => ({ ...r, [id]: { ...(r[id] ?? { inUse: false }), isActive: true } }));
                } }}>
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
                  {step === 4 && orgPart && <OrganisationStep defaults={defaults} part={orgPart} />}
                  {step === 5 && <RulesStep defaults={defaults} preview={plan} previewState={planState} part={part && !isOrgPart(part) ? part as RulesPartId : undefined}
                    onPartChange={p => goToPosition({ step: 5, part: p })} confirmLeaveRef={rulesLeaveRef} beforeLeave={autosave.flush} />}
                  {step === 6 && adminStepVisible && <AdminStep />}
                  {step === 7 && <ReviewStep plan={plan} state={planState} onRetry={() => void refreshPlan(body)} onGoTo={goToField} visibleSteps={steps}
                    onChangeAndCheck={changeAndCheck} onReloadPrefill={existing ? reloadFromCompany : undefined}
                    savedSections={[...savedParts]} />}
                  {draftId && positionSection && <SectionSaveBar section={positionSection} />}
                </div>
                </SectionSaveProvider>
                </ArabicSuggestionsProvider>
              </WizardFormProvider>
            </div>
            {sectionNotice}
            {commitError && (
              <div className="mt-4"><Notice tone="error" role="alert">
                <p>{commitError.message}</p>
                <div className="mt-2 flex flex-wrap gap-2">
                  {commitError.retry && <button type="button" className="btn-secondary" onClick={() => void create()}>{t('Try again')}</button>}
                  {commitError.position && <button type="button" className="btn-secondary" onClick={() => goToPosition(commitError.position!)}>{t('Go to {step}', { step: titleOf(commitError.position) })}</button>}
                  {commitError.checkAgain && <button type="button" className="btn-secondary" onClick={() => void checkAgain()}>{t('Check again')}</button>}
                  {commitError.leave && <Link href={existing ? GUIDED_SETUP_HREF : '/setup?tab=companies'} className="btn-secondary">{existing ? t('Back to Guided setup') : t('Back to companies')}</Link>}
                  {autosave.state === 'conflict' && <button type="button" className="btn-secondary" onClick={() => void reloadDraft()}>{t('Reload draft')}</button>}
                </div>
              </Notice></div>
            )}
            <div className="sticky bottom-0 z-10 -mx-4 mt-6 border-t border-slate-200 bg-white/95 px-4 py-3 backdrop-blur supports-[backdrop-filter]:bg-white/80 max-lg:static max-lg:pe-20 dark:border-white/10 dark:bg-surface/95 sm:-mx-6 sm:px-6 lg:pe-24" data-testid="wizard-footer">
              <div className="flex flex-wrap items-center justify-between gap-3">
                {prevPosition
                  ? <button type="button" className="btn-secondary text-slate-700 dark:text-slate-200" onMouseDown={keepFocus} onClick={back} disabled={!!busy} data-testid="wizard-back">
                      <ArrowLeft className="h-4 w-4" aria-hidden="true" />
                      <span className="sm:hidden">{t('Back')}</span><span className="hidden sm:inline">{t('Back to {destination}', { destination: backTitle })}</span>
                    </button>
                  : <Link href={GUIDED_SETUP_HREF} className="btn-secondary text-slate-700 dark:text-slate-200"
                      onClick={e => { if (draftId) { e.preventDefault(); void leaveTo(GUIDED_SETUP_HREF); } }}>{t('Back to Guided setup')}</Link>}
                <p role="status" className="order-last w-full min-w-0 text-xs sm:order-none sm:w-auto sm:flex-1 sm:text-center" data-testid="wizard-footer-status">{statusLine}</p>
                <div className="ms-auto flex flex-wrap items-center gap-2">
                  {writesNow && <button type="button" className="btn-secondary text-slate-700 dark:text-slate-200" onMouseDown={keepFocus} disabled={!!busy} onClick={() => {
                    const errors = validatePosition(position, input);
                    if (errors.length) { focusField.current = errors[0].field; setAttempted(a => new Set([...a, positionKey(position)])); return; }
                    void saveSection(false);
                  }} data-testid="wizard-save-only">{t(SAVE_PART_ONLY[orgPart!])}</button>}
                  {!isLast
                    ? <button type="button" className="btn-primary min-w-[10rem] justify-center hover:translate-y-0 disabled:cursor-not-allowed disabled:opacity-60" onMouseDown={keepFocus} onClick={() => next()}
                        disabled={(!!busy && busy !== 'saving') || locked} aria-busy={busy === 'saving' || busy === 'starting' || undefined}
                        aria-keyshortcuts="Meta+Enter Control+Enter" data-testid="wizard-next">
                        {busy === 'saving' ? <><Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />{t('Saving…')}</>
                          : busy === 'starting' ? <><Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />{t('Preparing the draft…')}</>
                          : <>{primaryLabel}<ArrowRight className="h-4 w-4" aria-hidden="true" /></>}
                        <kbd className="ms-1 hidden rounded border border-white/30 px-1 font-sans text-caption font-medium leading-4 text-white/90 lg:inline" aria-hidden="true">{shortcut}</kbd>
                      </button>
                    : <button type="button" className="btn-primary min-w-[10rem] justify-center hover:translate-y-0 disabled:cursor-not-allowed disabled:opacity-50" onClick={() => void create()} disabled={!!busy || !canCommit(plan) || planState === 'loading' || autosave.state === 'conflict'}
                        aria-busy={busy === 'creating' || undefined} aria-keyshortcuts="Meta+Enter Control+Enter" data-testid="wizard-create">
                        {busy === 'creating'
                          ? <><Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />{existing ? t('Saving all changes…') : t('Creating the company…')}</>
                          : existing ? (noChange ? t('Finish without changes') : t('Save all changes')) : t('Create company')}
                        <kbd className="ms-1 hidden rounded border border-white/30 px-1 font-sans text-caption font-medium leading-4 text-white/90 lg:inline" aria-hidden="true">{shortcut}</kbd>
                      </button>}
                </div>
              </div>
              {writesNow && <p className="mt-1.5 text-end text-xs text-slate-600 dark:text-slate-400" data-testid="saves-now">{t('Saves to {company} now. You can still change it later in Setup.', { company: companyName })}</p>}
              {isLast && plan && !canCommit(plan) && <p className="mt-1.5 text-end text-xs text-rose-700 dark:text-rose-300">{existing ? t('Save all changes is available once the items above are fixed.') : t('Create is available once the items above are fixed.')}</p>}
            </div>
          </div>
          {step !== 7 && structureAside}
        </div>
      </div>
    </div>
  );
}
