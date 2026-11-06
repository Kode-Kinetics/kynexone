'use client';

import { useEffect, useId, useRef } from 'react';
import { Lock, RotateCcw } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { msg } from '../../i18n/translations';
import { previewFailure, previewResolution } from '../../lib/employeeCostCentre';
import { BdiMessage } from '../../components/BdiMessage';
import { CostCentreSelect } from '../../components/employees/CostCentreSelect';
import { CostCentreInvalidSentence, CostCentreSourceLine } from '../../components/employees/CostCentreSourceLine';
import type { EditCostCentre } from './useEditCostCentre';
import type { CostCentreIntent } from './useEmployeeEditDraft';

export const EDIT_COST_CENTRE_SELECT_ID = 'edit-field-costCenter';

/** A message on the block and where focus goes with it; `n` changes on every new message so focus moves again. */
export interface EditCostCentreAlert { message: string; focus: 'decision' | 'reason' | 'select'; n: number }

/**
 * The cost centre in Edit employee → Employment (Wave A, D4): the RESOLVED cost centre pre-selected in a dropdown of the
 * company's active cost centres, the line saying where it comes from, the server's answer live when the department changes
 * (aria-live), the keep-or-follow question when an override meets a department change, and the reason whenever the save
 * changes the override. Without organization.costcenter.manage: the value, its source and who can change it.
 */
export function EmployeeEditCostCentreField({ cc, label, intent, onIntent, alert }: {
  cc: EditCostCentre;
  label: string;
  intent: CostCentreIntent;
  onIntent: (next: Partial<CostCentreIntent>) => void;
  alert: EditCostCentreAlert | null;
}) {
  const { t } = useLocale();
  const labelId = useId();
  const sourceId = useId();
  const previewId = useId();
  const legendId = useId();
  const reasonId = useId();
  const reasonHintId = useId();
  const errorId = useId();
  const decisionRef = useRef<HTMLFieldSetElement>(null);
  const reasonRef = useRef<HTMLInputElement>(null);

  // Focus after the step's own heading focus (EmployeeCreatePanel focuses it in a frame): two frames later.
  useEffect(() => {
    if (!alert) return;
    let second = 0;
    const first = requestAnimationFrame(() => {
      second = requestAnimationFrame(() => {
        const select = document.getElementById(EDIT_COST_CENTRE_SELECT_ID);
        const target = alert.focus === 'decision' ? decisionRef.current ?? select : alert.focus === 'reason' ? reasonRef.current ?? select : select;
        target?.focus();
      });
    });
    return () => { cancelAnimationFrame(first); cancelAnimationFrame(second); };
  }, [alert]);

  const department = cc.departmentLabel;
  const livePreview = (() => {
    if (!cc.departmentChanged || !cc.canManage) return null;
    const state = cc.departmentPreview;
    if (state.status === 'rateLimited') return <span data-testid="cc-edit-preview-wait">{t('Checking too often. Trying again in a moment…')}</span>;
    if (state.status === 'error') {
      if (previewFailure(state.error).kind === 'departmentNotFound') return <span>{t('This department is not in the employee’s company.')}</span>;
      return (
        <span className="inline-flex flex-wrap items-center gap-2">
          <span>{t('The cost centre for the new department could not be checked. It is worked out again when you save.')}</span>
          <button type="button" onClick={cc.retryDepartmentPreview} className="inline-flex items-center gap-1 font-semibold text-sapphire hover:underline"><RotateCcw className="h-3.5 w-3.5" aria-hidden />{t('Retry')}</button>
        </span>
      );
    }
    if (state.status !== 'ready') return <BdiMessage message={msg('Checking the cost centre for {department}…')} values={{ department }} />;
    const r = previewResolution(state.data, 'en');
    if (r.kind === 'invalid') return <CostCentreInvalidSentence level={r.level} code={r.code} />;
    if (r.costCentreId && state.data.code) return <BdiMessage message={msg('Will follow {department} → {code}')} values={{ department, code: state.data.code }} />;
    return <BdiMessage message={msg('Will follow {department}: it has no cost centre yet.')} values={{ department }} />;
  })();

  const followCode = cc.follow?.code ?? '';
  const reasonHint = intent.choice !== undefined && intent.choice !== '' && intent.choice !== cc.follow?.id
    ? t('Why this person has a different cost centre. Recorded in the audit log.')
    : cc.needsDecision
      ? t('Why you chose this for the override. Recorded in the audit log.')
      : t('Why this person goes back to the department’s cost centre. Recorded in the audit log.');

  return (
    <div role="group" aria-labelledby={labelId} data-testid="cc-edit-field" className="space-y-2 sm:col-span-2">
      {cc.canManage ? (
        <>
          <label id={labelId} htmlFor={EDIT_COST_CENTRE_SELECT_ID} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{label}</label>
          <CostCentreSelect
            id={EDIT_COST_CENTRE_SELECT_ID}
            testId="cc-edit-select"
            value={cc.selectValue}
            onChange={(value) => onIntent({ choice: value })}
            options={cc.options}
            extra={cc.extra}
            followPending={cc.follow === null}
            describedBy={[sourceId, previewId, alert ? errorId : ''].filter(Boolean).join(' ')}
            invalid={!!alert && alert.focus === 'select'}
          />
        </>
      ) : (
        <>
          <p id={labelId} className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</p>
          <p data-testid="cc-edit-value" className="min-h-10 rounded-lg bg-slate-50 px-3 py-2.5 text-sm text-slate-800 dark:bg-white/5 dark:text-slate-100">
            {cc.currentLabel ? <bdi dir="auto">{cc.currentLabel}</bdi> : <span aria-hidden="true">—</span>}
          </p>
        </>
      )}

      <div id={sourceId}>
        <CostCentreSourceLine resolution={cc.current} variant="sentence" testId="cc-edit-source" invalidTestId="cc-edit-source" setupLink={cc.canManage} />
      </div>
      <p id={previewId} data-testid="cc-edit-preview" aria-live="polite" className="text-xs font-medium text-slate-800 dark:text-slate-100">{livePreview}</p>

      {cc.needsDecision && (
        <fieldset
          ref={decisionRef}
          tabIndex={-1}
          role="radiogroup"
          aria-labelledby={legendId}
          aria-describedby={alert?.focus === 'decision' ? errorId : undefined}
          data-testid="cc-edit-decision"
          className="space-y-1.5 rounded-lg border border-amber-200 bg-amber-50/60 px-3 py-2.5 focus-visible:ring-2 focus-visible:ring-sapphire dark:border-amber-500/30 dark:bg-amber-500/5"
        >
          <legend id={legendId} className="px-1 text-sm font-semibold text-slate-800 dark:text-slate-100">{t('This employee has a cost centre override. What happens to it after the move?')}</legend>
          <label className="flex items-center gap-2 text-sm text-slate-800 dark:text-slate-100">
            <input type="radio" name="cc-edit-decision" value="keep" checked={intent.decision === 'keep'} onChange={() => onIntent({ decision: 'keep' })} />
            <BdiMessage message={msg('Keep {code} (override)')} values={{ code: cc.overrideCode }} />
          </label>
          <label className="flex items-center gap-2 text-sm text-slate-800 dark:text-slate-100">
            <input type="radio" name="cc-edit-decision" value="drop" checked={intent.decision === 'drop'} onChange={() => onIntent({ decision: 'drop' })} />
            {cc.follow === null
              ? <span>{t('Follow department')}</span>
              : followCode
                ? <BdiMessage message={msg('Follow {department} → {code}')} values={{ department, code: followCode }} />
                : <BdiMessage message={msg('Follow {department} · no cost centre')} values={{ department }} />}
          </label>
        </fieldset>
      )}

      {cc.needsReason && (
        <div>
          <label htmlFor={reasonId} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason')} <span aria-hidden="true" className="text-rose-500">*</span></label>
          <input
            ref={reasonRef}
            id={reasonId}
            data-testid="cc-edit-reason"
            value={intent.reason}
            maxLength={500}
            required
            aria-required="true"
            aria-invalid={alert?.focus === 'reason' || undefined}
            aria-describedby={[reasonHintId, alert?.focus === 'reason' ? errorId : ''].filter(Boolean).join(' ')}
            onChange={(e) => onIntent({ reason: e.target.value })}
            className="input mt-1.5 w-full"
          />
          <span id={reasonHintId} className="mt-1 block text-xs font-normal text-slate-500">{reasonHint}</span>
        </div>
      )}

      {alert && <p id={errorId} role="alert" data-testid="cc-edit-error" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{alert.message}</p>}

      {!cc.canManage && (
        <p role="note" data-testid="cc-edit-readonly-note" className="flex items-start gap-1.5 text-xs text-slate-600 dark:text-slate-300">
          <Lock className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t('Only HR managers and admins can change this.')}
        </p>
      )}
    </div>
  );
}
