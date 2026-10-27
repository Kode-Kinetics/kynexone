'use client';

import { useId } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import type { HireTemplate } from '@/src/api/onboardingTemplates';
import type { HireForm } from '@/src/lib/hire/hireForm';
import { FieldShell } from './fields/FieldShell';
import type { HireTemplateChoices } from './useHireTemplate';

export interface AppliedTemplate {
  template: HireTemplate;
  applied: Partial<HireForm>;
  prior: Partial<HireForm>;
  kept: ReadonlyArray<keyof HireForm>;
}

/**
 * Step 1: apply a hire template. A template only fills fields that are still empty; whatever the person already entered stays, and
 * the chip says how many fields were left alone. Undo puts back what the template filled. Everything is announced in a polite
 * live region. Nothing is applied until the person chooses.
 */
export function TemplatePicker({ choices, applied, onApply, onUndo }: {
  choices: HireTemplateChoices;
  applied: AppliedTemplate | null;
  onApply: (template: HireTemplate) => void;
  onUndo: () => void;
}) {
  const { t, locale } = useLocale();
  const id = useId();
  if (!choices.ready || choices.templates.length === 0) return null;
  const name = (x: HireTemplate) => (locale === 'ar' && x.nameAr ? x.nameAr : x.name);
  const suggested = choices.templates.find(x => x.id === choices.suggestedId);
  const filled = applied ? Object.keys(applied.applied).length : 0;
  return (
    <div className="sm:col-span-2" data-testid="hire-template-picker">
      <FieldShell field="hireTemplate" label={t('Hire template')} hint={t('Fills the fields you have not filled yet. You can change everything afterwards.')}>
        {p => (
          <select {...p} value={applied?.template.id ?? ''} className="input w-full min-w-0"
            onChange={e => { const chosen = choices.templates.find(x => x.id === e.target.value); if (chosen) onApply(chosen); }}>
            <option value="">{t('Not chosen')}</option>
            {choices.templates.map(x => <option key={x.id} value={x.id}>{x.id === choices.suggestedId ? t('{name} (suggested)', { name: name(x) }) : name(x)}</option>)}
          </select>
        )}
      </FieldShell>
      {suggested && !applied && (
        <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">
          {t('Suggested for this designation: {name}.', { name: name(suggested) })}{' '}
          <button type="button" className="font-medium text-sapphire underline" onClick={() => onApply(suggested)}>{t('Apply')}</button>
        </p>
      )}
      <div id={id} role="status" aria-live="polite" data-testid="hire-template-status">
        {applied && (
          <p className="mt-2 inline-flex flex-wrap items-center gap-2 rounded-full border border-emerald-300 bg-emerald-50 px-3 py-1 text-sm text-emerald-900 dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-100">
            <span>{t('Applied: {name}', { name: name(applied.template) })}</span>
            <span className="text-xs">{t('{filled, plural, one {# field filled} other {# fields filled}}, {kept, plural, one {# left as you entered it.} other {# left as you entered them.}}', { filled, kept: applied.kept.length })}</span>
            <button type="button" className="font-semibold underline" onClick={onUndo}>{t('Undo')}</button>
          </p>
        )}
      </div>
    </div>
  );
}
