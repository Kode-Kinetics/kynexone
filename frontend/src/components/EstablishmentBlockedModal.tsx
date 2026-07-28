'use client';

import { AlertTriangle, ExternalLink, Undo2 } from 'lucide-react';
import { Modal } from './Modal';
import { useLocale } from '../contexts/LocaleContext';
import type { EstablishmentBlockedPayload } from '../api/establishment';

/** Replaces {token} placeholders in a translated template. */
function fmt(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (m, key) => (key in values ? String(values[key]) : m));
}

interface EstablishmentBlockedModalProps {
  /** The ESTABLISHMENT_BUDGET_EXCEEDED 409 payload; null keeps the modal closed. */
  block: EstablishmentBlockedPayload | null;
  /** Name of the employee whose assignment was blocked (when known). */
  employeeName?: string;
  onClose: () => void;
  /**
   * Returns the user to their own form with the department selection editable.
   * The system never recommends departments with spare budget — the user changes
   * their own selection. Omit where no originating selection exists.
   */
  onChooseDifferentDepartment?: () => void;
}

/**
 * Blocked-assignment popup for the Establishment Matrix guard (server-authoritative
 * 409, code ESTABLISHMENT_BUDGET_EXCEEDED). Renders the structured payload — the
 * decision was made server-side; this is display only.
 */
export function EstablishmentBlockedModal({ block, employeeName, onClose, onChooseDifferentDepartment }: EstablishmentBlockedModalProps) {
  const { t, locale } = useLocale();
  if (!block) return null;

  const levelName = (locale === 'ar' && block.levelNameAr) ? block.levelNameAr : block.levelNameEn;

  const body = fmt(t('{department} already has {current} of {budgeted} budgeted {level}(s).'), {
    department: block.departmentName, current: block.current, budgeted: block.budgeted, level: levelName,
  });
  const cannotAssign = employeeName
    ? fmt(t('{employee} cannot be assigned as {level} in {department}.'), {
        employee: employeeName, level: levelName, department: block.departmentName,
      })
    : fmt(t('No further {level} can be assigned in {department}.'), {
        level: levelName, department: block.departmentName,
      });

  const openBudgetEditor = () => {
    // New tab so the originating form keeps its state — after the raise the user
    // resubmits the same form to complete the assignment without re-entry.
    window.open(
      `/setup?tab=establishment&department=${encodeURIComponent(block.departmentId)}&level=${encodeURIComponent(block.staffingLevelId)}`,
      '_blank',
      'noopener',
    );
  };

  return (
    <Modal
      isOpen
      title={t('Staffing budget reached')}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          {onChooseDifferentDepartment && (
            <button
              type="button"
              className="btn-secondary flex items-center gap-1.5"
              onClick={() => { onClose(); onChooseDifferentDepartment(); }}
            >
              <Undo2 className="h-3.5 w-3.5" />
              {t('Choose a different department')}
            </button>
          )}
          {block.canEditEstablishment && (
            <button type="button" className="btn-primary flex items-center gap-1.5" onClick={openBudgetEditor}>
              <ExternalLink className="h-3.5 w-3.5" />
              {t('Edit staffing budget')}
            </button>
          )}
        </>
      }
    >
      <div className="space-y-3">
        <div className="flex items-start gap-3 rounded-xl border border-rose-200 bg-rose-50 p-3 dark:border-rose-500/30 dark:bg-rose-500/10">
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-rose-500 dark:text-rose-400" />
          <div className="text-sm text-rose-700 dark:text-rose-300">
            <p className="font-semibold">{body}</p>
            <p className="mt-1">{cannotAssign}</p>
            <p className="mt-1">{t('To proceed, increase the budget for this level or choose a different department.')}</p>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="rounded-md bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-300">
            {block.departmentName}
          </span>
          <span className="rounded-md bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-300">
            {levelName}
          </span>
          <span className="rounded-md bg-rose-100 px-2 py-1 font-semibold text-rose-600 dark:bg-rose-500/15 dark:text-rose-400">
            {block.current} / {block.budgeted}
          </span>
        </div>

        {block.exitingIncumbents > 0 && (
          <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
            {fmt(t('{n} current {level}(s) are serving notice; this slot frees when they exit.'), {
              n: block.exitingIncumbents, level: levelName,
            })}
          </p>
        )}

        {!block.canEditEstablishment && (
          <p className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-xs text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
            {t('Ask a user with the Establishment Management permission to amend the budget.')}
          </p>
        )}
      </div>
    </Modal>
  );
}
