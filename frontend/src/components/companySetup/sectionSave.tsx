'use client';

import { createContext, useContext } from 'react';
import { CheckCircle2, CircleDot } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { msg } from '../../i18n/translations';
import type { CompanySetupDraftBody } from '../../api/companySetup';
import type { StepId } from '../../lib/companySetupWizard';

/**
 * Explicit per-section Save on top of the draft's background autosave. A section's Save checks that section only,
 * saves the draft now (autosave.flush: same version and 409 handling), and records what was saved, so the section,
 * its header and the step rail can say "Saved at {time}" or "Unsaved changes". It never applies anything to the
 * company. Organisation (Step 4) has no section Save here: each part has its own "Save {part} & next", which
 * writes to an existing company at once.
 */
export type SectionId = 'company' | 'branches' | 'rules' | 'companyAdmin';

export const STEP_SECTIONS: Record<StepId, SectionId[]> = {
  1: [], 2: ['company'], 3: ['branches'], 4: [], 5: ['rules'], 6: ['companyAdmin'], 7: [],
};

export const SECTION_TITLE: Record<SectionId, string> = {
  company: msg('Company details'), branches: msg('Branches & locations'),
  rules: msg('Work & pay rules'), companyAdmin: msg('Company super admin'),
};

export const sectionHeadingId = (id: SectionId) => `ccw-section-${id}`;

/** Which section a field path (as validateStep and the server name it) belongs to. */
export function sectionOfField(field: string): SectionId | null {
  if (field.startsWith('company.')) return 'company';
  if (field.startsWith('branches') || field === 'locations') return 'branches';
  if (field.startsWith('rules')) return 'rules';
  if (field.startsWith('companyAdmin')) return 'companyAdmin';
  return null;
}

/** The part of the draft a section owns, as a comparable string. */
export function sectionSnapshot(body: CompanySetupDraftBody, id: SectionId): string {
  // Rules: the Work rules part only (week and employee numbers); the leave part's choices are not this section's.
  const slice = id === 'company' ? body.company : id === 'rules' ? { workWeek: body.rules.workWeek, employeeNumbering: body.rules.employeeNumbering }
    : id === 'companyAdmin' ? body.companyAdmin : body[id];
  return JSON.stringify(slice ?? null);
}

export type SectionStatus = { kind: 'saved'; at: string } | { kind: 'unsaved' } | { kind: 'notSaved' };

export interface SectionSaveValue {
  status: (id: SectionId) => SectionStatus;
  save: (id: SectionId) => void;
  busy: SectionId | null;
  /** Why the last Save of this section did not go through, in the reader's language. */
  problem: (id: SectionId) => string | null;
  /** Last section of its step: its button reads "Save" and then opens the next step. */
  isLast: (id: SectionId) => boolean;
  disabled: boolean;
}

const Ctx = createContext<SectionSaveValue | null>(null);
export const SectionSaveProvider = Ctx.Provider;
const useSectionSave = () => useContext(Ctx);

/** "Saved at 10:42", "Unsaved changes" or "Not saved yet", as a quiet pill. */
export function SectionSavedStatus({ section, testId }: { section: SectionId; testId?: string }) {
  const t = useT();
  const fmt = useFormat();
  const ctx = useSectionSave();
  if (!ctx) return null;
  const status = ctx.status(section);
  const base = 'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold rtl:text-[13px]';
  return (
    <span className={`${base} ${status.kind === 'saved'
      ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200'
      : status.kind === 'unsaved' ? 'bg-amber-50 text-amber-900 dark:bg-amber-500/15 dark:text-amber-200' : 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-300'}`}
      data-testid={testId ?? `section-status-${section}`} data-status={status.kind}>
      {status.kind === 'saved' ? <CheckCircle2 className="h-3 w-3" aria-hidden="true" /> : <CircleDot className="h-3 w-3" aria-hidden="true" />}
      {status.kind === 'saved' ? t('Saved at {time}', { time: fmt.time(status.at) }) : status.kind === 'unsaved' ? t('Unsaved changes') : t('Not saved yet')}
    </span>
  );
}

/** The section's own Save, at its end: status beside the button, and a plain message when it did not save. */
export function SectionSaveBar({ section }: { section: SectionId }) {
  const t = useT();
  const ctx = useSectionSave();
  if (!ctx) return null;
  const problem = ctx.problem(section);
  const busy = ctx.busy === section;
  const label = ctx.isLast(section) ? t('Save') : t('Save and continue');
  return (
    <div className="mt-4 border-t border-slate-200 pt-3 dark:border-white/10" data-testid={`section-save-${section}`}>
      <div className="flex flex-wrap items-center justify-end gap-3">
        <span aria-live="polite"><SectionSavedStatus section={section} testId={`section-save-status-${section}`} /></span>
        <button type="button" className="btn-primary disabled:cursor-not-allowed disabled:opacity-50" disabled={ctx.disabled || !!ctx.busy}
          onClick={() => ctx.save(section)}>
          {busy ? t('Saving…') : label}<span className="sr-only"> {t(SECTION_TITLE[section])}</span>
        </button>
      </div>
      {problem && <p role="alert" className="mt-2 text-end text-sm font-medium text-rose-700 dark:text-rose-300" data-testid={`section-save-problem-${section}`}>{problem}</p>}
    </div>
  );
}
