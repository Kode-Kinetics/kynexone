'use client';

import { useId, useState } from 'react';
import type { VoidRunOptions } from '../../api/payroll';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { journalTitle } from '../../lib/glJournalView';
import {
  electionOptions, electionsComplete, readRequiredElections,
  type VoidChoices, type VoidDisposition, type VoidElectionGroup,
} from '../../lib/voidElections';
import { serverMessage } from '../gl/glUi';

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';

/**
 * The body of the Void dialog. A run whose net pay or statutory remittance has already left the bank cannot be voided until
 * the operator states what happened to that cash, so the first submit may be refused (HTTP 422) with the list of elections the
 * server needs. They are shown here as choices with a reference field, then the same void is sent again carrying them.
 * Nothing is inferred and nothing is skipped: the server still refuses an unstated election or a recall without a reference.
 */
export function VoidRunForm({ currency, onClose, onSubmit }: {
  /** The run's currency, for the amounts the server names. */
  currency?: string | null;
  onClose: () => void;
  onSubmit: (reason: string, options: VoidRunOptions) => Promise<void>;
}) {
  const t = useT();
  const reasonId = useId();
  const [reason, setReason] = useState('');
  const [groups, setGroups] = useState<VoidElectionGroup[] | null>(null);
  const [choices, setChoices] = useState<VoidChoices>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    setBusy(true); setError('');
    try {
      await onSubmit(reason.trim(), electionOptions(choices));
    } catch (e: unknown) {
      const required = readRequiredElections(e);
      if (required) setGroups(required);
      else setError(serverMessage(e, t) || t('The action was refused.'));
      setBusy(false);
    }
  };

  const pending = groups != null && !electionsComplete(groups, choices);

  return (
    <div className="space-y-4">
      <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">
        {t("Voiding never deletes. The run's status becomes Voided, its payslips are voided, and contra-entries are written for any general-ledger rows it already posted. The period is then free for a replacement run. This is irreversible and the reason is recorded against your identity.")}
      </p>
      <div className="space-y-1">
        <label htmlFor={reasonId} className="block text-xs font-semibold text-slate-700 dark:text-slate-300">{t('Reason (required)')}</label>
        <textarea id={reasonId} aria-label={t('Reason')} className={input} rows={3} value={reason} onChange={e => setReason(e.target.value)}
          placeholder={t('Recorded against your identity on the audit trail.')} />
      </div>

      {groups && (
        <div data-testid="void-elections" className="space-y-3 rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-500/30 dark:bg-amber-500/10">
          <div>
            <p className="text-sm font-semibold text-amber-900 dark:text-amber-200">{t('Cash has already left the bank for this run')}</p>
            <p className="mt-1 text-xs leading-5 text-amber-900 dark:text-amber-200">
              {t('A void must not silently put that cash back on the books. For each payment below, say what happened to the money.')}
            </p>
          </div>
          {groups.map(g => (
            <ElectionGroup key={g.field} group={g} currency={currency} choices={choices}
              onChange={patch => setChoices(c => ({ ...c, ...patch }))} />
          ))}
          {pending && <p className="text-xs text-amber-900 dark:text-amber-200">{t('Choose what happened to every payment above to continue.')}</p>}
        </div>
      )}

      {error && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onClose}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
          {t('Cancel')}
        </button>
        <button type="button" data-testid="void-run-submit" onClick={submit} disabled={busy || reason.trim().length < 1 || pending}
          className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50">
          {busy ? t('Working…') : t('Void run')}
        </button>
      </div>
    </div>
  );
}

function ElectionGroup({ group, currency, choices, onChange }: {
  group: VoidElectionGroup;
  currency?: string | null;
  choices: VoidChoices;
  onChange: (patch: VoidChoices) => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const name = useId();
  const refId = useId();
  const choice = choices[group.field];
  const reference = choices[group.referenceField] ?? '';
  const title = group.kind === 'settlement' ? t('Net pay') : t('Statutory remittances');
  const option = (value: VoidDisposition, label: string, hint: string) => (
    <label htmlFor={`${name}-${value}`} className="grid cursor-pointer grid-cols-[auto_1fr] items-start gap-x-2 rounded-lg border border-amber-200 bg-white p-3 dark:border-white/10 dark:bg-white/5">
      <input id={`${name}-${value}`} type="radio" name={name} value={value} checked={choice === value} className="mt-0.5"
        onChange={() => onChange({ [group.field]: value } as VoidChoices)} />
      <span className="text-sm font-semibold text-slate-900 dark:text-white">{label}</span>
      <span className="col-start-2 text-xs text-slate-600 dark:text-slate-400">{hint}</span>
    </label>
  );

  return (
    <fieldset data-testid={`void-election-${group.kind}`} className="space-y-2">
      <legend className="text-sm font-semibold text-slate-900 dark:text-white">{title}</legend>
      <ul className="space-y-0.5 text-xs text-slate-700 dark:text-slate-300">
        {group.items.map((i, n) => (
          <li key={n}>
            {i.label ? <span className="font-medium">{t(journalTitle({ kind: 'remittance', group: i.label }))}: </span> : null}
            {t('{amount} credited to {account}', { amount: fmt.money(i.amount, currency), account: i.cashAccount })}
          </li>
        ))}
      </ul>
      <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{t('What happened to this money?')}</p>
      <div role="radiogroup" aria-label={`${title}: ${t('What happened to this money?')}`} className="grid gap-2 sm:grid-cols-2">
        {option('FundsRecalled', t('Funds recalled'), t('The bank returned the money. The payment journal is reversed and cash goes back up.'))}
        {option('FundsDisbursed', t('Funds already disbursed'), t('The money is gone and has not come back. It stays out of cash and is recorded as recoverable.'))}
      </div>
      {choice === 'FundsRecalled' && (
        <div className="space-y-1">
          <label htmlFor={refId} className="block text-xs font-semibold text-slate-700 dark:text-slate-300">{t('Recall reference')}</label>
          <input id={refId} className={input} value={reference} aria-required="true" placeholder={t('Bank recall or return reference')}
            onChange={e => onChange({ [group.referenceField]: e.target.value } as VoidChoices)} />
          <span className="block text-caption text-slate-600 dark:text-slate-400">{t('Required when the funds were recalled.')}</span>
        </div>
      )}
    </fieldset>
  );
}
