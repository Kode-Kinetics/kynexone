'use client';

import { useCallback, useEffect, useRef, useState, type MutableRefObject, type ReactNode } from 'react';
import { useSearchParams } from 'next/navigation';
import { useLocale } from '../../contexts/LocaleContext';
import { msg } from '../../i18n/translations';
import { useNavigationBlocker } from '../../hooks/useNavigationBlocker';
import type { CompanySetupDraftBody, CompanySetupPlan } from '../../api/companySetup';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { isGuid, type CompanySetupReturn } from '../setup/companySetupReturn';
import { MoreSettingsLinks } from './MoreSettingsLinks';
import { useWizardForm } from './wizardForm';
import { WorkRulesPart } from './rules/WorkRulesPart';
import { LeavePart } from './rules/LeavePart';
import { EndOfServicePart } from './rules/EndOfServicePart';
import { DirtyRegistry, PartHeading, RULES_PARTS, useKeyboardInitiated, useRulesAccess, type RulesPartId } from './rules/rulesShared';
import { SaveModePill, useSaveTally, type SaveMode } from './rules/SaveModePill';
import styles from './rules/motion.module.css';

export type { RulesPartId } from './rules/rulesShared';

export interface RulesStepProps {
  defaults: CompanySetupDraftBody | null;
  preview: CompanySetupPlan | null;
  previewState: 'idle' | 'loading' | 'error';
  /** The part to show (Addendum B4). Undefined: all four parts, stacked. */
  part?: RulesPartId;
  /** Opens another step-5 part (More settings → Leave & holidays). Without it that entry opens in a side panel. */
  onPartChange?: (part: RulesPartId) => void;
  /** True while an embedded editor holds an open, unsaved form. */
  onDirtyChange?: (dirty: boolean) => void;
  /**
   * Filled in by RulesStep. Before Next, Back or a rail click the wizard awaits `confirmLeaveRef.current?.()`:
   * true = go on (nothing unsaved, or the reader chose to discard), false = stay.
   */
  confirmLeaveRef?: MutableRefObject<(() => Promise<boolean>) | null>;
  /** Saves the draft before a More settings page link leaves the wizard (the wizard's autosave.flush). */
  beforeLeave?: () => Promise<boolean>;
}

const PART_COPY: Record<RulesPartId, { title: string; description: string }> = {
  workRules: { title: msg('Working week & numbering'), description: msg('Checked on the review step and saved together with the company.') },
  leave: { title: msg('Leave & holidays'), description: msg('Statutory leave, public holidays and the leave policies employees request against.') },
  endOfService: { title: msg('End of service'), description: msg('Gratuity rates and the other rules that follow the company’s country.') },
  moreSettings: { title: msg('More settings for this company'), description: msg('Some open here, beside the wizard. The others open their own page, with a way back to this step.') },
};

/**
 * Step 5, Work & pay rules, in four parts (Addendum B4):
 *  - workRules — working week and employee numbering, draft data saved by Create / Update;
 *  - leave — the country pack's leave and holidays (draft) plus the live Leave policies / Holiday calendars /
 *    Leave types editors;
 *  - endOfService — the pack's end-of-service settings (draft) plus the live Country rules editor;
 *  - moreSettings — everything else, grouped; embeddable editors open in place, the rest on their own page.
 * Each part's header says how it saves (audit A10). An embedded editor's open, unsaved form is never lost silently.
 */
export function RulesStep({ defaults, preview, previewState, part, onPartChange, onDirtyChange, confirmLeaveRef, beforeLeave }: RulesStepProps) {
  const { t } = useLocale();
  const { body } = useWizardForm();
  const params = useSearchParams();
  const access = useRulesAccess();
  const [dirtyEditors, setDirtyEditors] = useState<string[]>([]);
  const [confirmation, setConfirmation] = useState<ConfirmState | null>(null);
  const pendingAnswer = useRef<((leave: boolean) => void) | null>(null);
  const [leaveTally, recordLeave] = useSaveTally();
  const [eosTally, recordEos] = useSaveTally();
  const existing = !!body.existingCompanyId;
  const draftMode: SaveMode = existing ? 'update' : 'create';

  const onDirty = useCallback((keys: string[]) => { setDirtyEditors(keys); onDirtyChange?.(keys.length > 0); }, [onDirtyChange]);
  const dirty = dirtyEditors.length > 0;
  const editorName = dirtyEditors.length ? t(dirtyEditors[0]) : '';

  // Route changes and closing the tab (the app's navigation guard).
  useNavigationBlocker({
    id: 'company-setup-rules-editor',
    when: dirty,
    message: t('Your changes to {editor} are not saved. Leave and discard them?', { editor: editorName }),
    onDiscard: () => setDirtyEditors([]),
  });

  // Next, Back and the rail (the wizard asks through confirmLeaveRef).
  const answer = (leave: boolean) => { pendingAnswer.current?.(leave); pendingAnswer.current = null; setConfirmation(null); };
  const confirmLeave = useCallback((): Promise<boolean> => {
    if (!dirty) return Promise.resolve(true);
    pendingAnswer.current?.(false);
    return new Promise<boolean>(resolve => {
      pendingAnswer.current = resolve;
      setConfirmation({
        title: t('Save your changes to {editor}?', { editor: editorName }),
        message: t('The {editor} form is still open and its changes are not saved. Keep editing to save them there, or discard them and continue.', { editor: editorName }),
        confirmLabel: t('Discard changes'),
        cancelLabel: t('Keep editing'),
        onConfirm: () => answer(true),
        closeOnConfirm: false,
      });
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dirty, editorName, t]);
  useEffect(() => {
    if (!confirmLeaveRef) return;
    confirmLeaveRef.current = confirmLeave;
    return () => { if (confirmLeaveRef.current === confirmLeave) confirmLeaveRef.current = null; };
  }, [confirmLeaveRef, confirmLeave]);
  useEffect(() => () => { pendingAnswer.current?.(true); }, []);

  const draftId = params?.get('draft');
  const returnTo: CompanySetupReturn | null = isGuid(draftId)
    ? { draftId, ...(isGuid(body.existingCompanyId) ? { companyId: body.existingCompanyId } : {}), part: 'moreSettings' }
    : null;

  // Stacked (no part): "open the Leave & holidays part" scrolls to it.
  const openPart = (target: RulesPartId) => {
    if (onPartChange) { onPartChange(target); return; }
    const el = document.getElementById(`rules-part-${target}`);
    el?.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    el?.querySelector<HTMLElement>('h3')?.focus();
  };

  const chip = (id: RulesPartId) => {
    if (id === 'workRules') return <SaveModePill mode={draftMode} />;
    if (id === 'leave') return access.canEditLeave ? <SaveModePill mode="live" tally={leaveTally} /> : <SaveModePill mode={draftMode} />;
    if (id === 'endOfService') return access.canEditCountryRules ? <SaveModePill mode="live" tally={eosTally} /> : <SaveModePill mode={draftMode} />;
    return <SaveModePill mode="elsewhere" />;
  };
  const content = (id: RulesPartId) => {
    if (id === 'workRules') return <WorkRulesPart defaults={defaults} />;
    if (id === 'leave') return <LeavePart preview={preview} previewState={previewState} onSaved={recordLeave} />;
    if (id === 'endOfService') return <EndOfServicePart preview={preview} previewState={previewState} onSaved={recordEos} />;
    return <MoreSettingsLinks companyId={body.existingCompanyId} onOpenPart={part ? onPartChange : openPart} returnTo={returnTo} beforeLeave={beforeLeave} />;
  };
  const header = (id: RulesPartId) => (
    <PartHeading level={3} id={`rules-part-${id}-title`} title={t(PART_COPY[id].title)} description={t(PART_COPY[id].description)} aside={chip(id)} />
  );

  return (
    <DirtyRegistry onChange={onDirty}>
      <ConfirmDialog state={confirmation} onCancel={() => answer(false)} />
      <div data-testid="rules-step" data-part={part ?? 'all'} data-editor-dirty={dirty ? 'true' : 'false'}>
      {part
        ? <PartEnter key={part} part={part}>
            <section id={`rules-part-${part}`} aria-label={t(PART_COPY[part].title)} data-testid={`rules-part-${part}`}>
              {/* The wizard's own heading already names the part and says what it is for: only how it saves is added here. */}
              <div className="flex justify-end">{chip(part)}</div>
              <div className="mt-3">{content(part)}</div>
            </section>
          </PartEnter>
        : <div className="space-y-12">
            {RULES_PARTS.filter(id => id !== 'moreSettings').map(id => (
              <section key={id} id={`rules-part-${id}`} aria-labelledby={`rules-part-${id}-title`} data-testid={`rules-part-${id}`} className="scroll-mt-20 [&_h3]:outline-none">
                {header(id)}
                <div className="mt-5">{content(id)}</div>
              </section>
            ))}
            <details className="group rounded-xl border border-slate-200 dark:border-white/10" data-testid="more-settings-section" id="rules-part-moreSettings">
              <summary className="cursor-pointer list-none rounded-xl px-4 py-3 transition-colors duration-150 hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:hover:bg-white/[0.03]">
                <span className="flex flex-wrap items-center justify-between gap-2">
                  <span className="text-sm font-semibold text-slate-900 dark:text-white">{t(PART_COPY.moreSettings.title)}</span>
                  {chip('moreSettings')}
                </span>
                <span className="mt-0.5 block text-xs font-normal text-slate-600 dark:text-slate-400">{t(PART_COPY.moreSettings.description)}</span>
              </summary>
              <div className={`px-4 pb-4 ${styles.fadeIn}`}>{content('moreSettings')}</div>
            </details>
          </div>}
      </div>
    </DirtyRegistry>
  );
}

/** M1/M2: a part arrives with a short logical slide, unless the switch came from the keyboard. */
function PartEnter({ part, children }: { part: RulesPartId; children: ReactNode }) {
  const keyboard = useKeyboardInitiated();
  const [animate] = useState(() => !keyboard());
  const [direction] = useState(() => {
    const previous = lastPart;
    lastPart = part;
    return previous && RULES_PARTS.indexOf(part) < RULES_PARTS.indexOf(previous) ? 'back' : 'forward';
  });
  return <div className={animate ? styles.partIn : undefined} data-dir={direction}>{children}</div>;
}
let lastPart: RulesPartId | null = null;
