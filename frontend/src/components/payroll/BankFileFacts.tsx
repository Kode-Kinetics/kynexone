'use client';

import type React from 'react';
import { CheckCircle2, AlertTriangle, ChevronDown } from 'lucide-react';
import type { PayrollPaymentBatch, PayrollRun } from '../../api/payroll';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { InfoTip } from '../InfoTip';

/** Run net − paid outside the file = what the file must hold. null when the API does not say what is paid outside. */
export function tieOut(batch: PayrollPaymentBatch, run: PayrollRun | null | undefined): { runNet: number; outside: number; expected: number; difference: number } | null {
  const outside = typeof batch.excludedTotal === 'number' ? batch.excludedTotal
    : batch.paymentExclusions ? batch.paymentExclusions.reduce((s, x) => s + (x.amount ?? 0), 0) : null;
  const runNet = typeof run?.totalNetSalary === 'number' ? run.totalNetSalary : null;
  if (outside === null || runNet === null) return null;
  const expected = Math.round((runNet - outside) * 100) / 100;
  return { runNet, outside, expected, difference: Math.round((batch.totalAmount - expected) * 100) / 100 };
}

/**
 * What this run pays through the bank, once a batch is open: the batch, who is in the file, how much, whether it matches the locked run,
 * and which bank. Every figure is read from the batch, the run and the bank setup the screen already loaded; a figure the API does not
 * give is left out rather than guessed. A difference against the run is never hidden: it is shown in red.
 */
export function BankFileFacts({ batch, run, employeesInFile, bankName, accountTail, onShowLeftOut, file, variant = 'strip', company, checked = false }: {
  /** The paying company, named first (group users pay several). */
  company?: string | null;
  /** Check employees has run clean: only then is a zero difference shown as passed (green). */
  checked?: boolean;
  /** The frozen file, once created (the rail then shows its credit date and reference). */
  file?: { reference: string; paymentDate: string } | null;
  /** 'strip' above the steps (up to 1679px); 'rail' beside them from 1680px. */
  variant?: 'strip' | 'rail';
  batch: PayrollPaymentBatch;
  run: PayrollRun | null | undefined;
  /** The payment records of the batch, when loaded (the people in the file). */
  employeesInFile: number | null;
  bankName?: string | null;
  accountTail?: string | null;
  /** Opens the list of employees paid outside the file. */
  onShowLeftOut?: () => void;
}) {
  const t = useT();
  const fx = useFormat();
  const money = (n: number) => fx.money(n, batch.currency);
  const leftOut = batch.paymentExclusions?.length ?? null;
  const tie = tieOut(batch, run);
  const excluded = tie?.outside ?? null;
  const runNet = tie?.runNet ?? null;
  const difference = tie?.difference ?? null;
  const matches = difference !== null && Math.abs(difference) < 0.005;
  const two = (n: number) => fx.number(n, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  const cell = 'min-w-0 px-4 py-2.5';
  const label = 'flex h-4 min-w-0 items-center gap-1 whitespace-nowrap text-meta text-fg-muted';
  const value = 'mt-0.5 truncate text-body font-semibold text-fg tabular-nums';
  const sub = 'mt-0.5 truncate text-meta text-fg-muted';

  const strip = (
    <dl className={`grid grid-cols-2 divide-line sm:grid-cols-3 lg:divide-x rtl:lg:divide-x-reverse ${company ? 'lg:grid-cols-6' : 'lg:grid-cols-5'}`}>
      {company && (
        <div className={cell}>
          <dt className={label}>{t('Company')}</dt>
          <dd className="mt-0.5 truncate text-body font-semibold text-fg" title={company}><bdi>{company}</bdi></dd>
        </div>
      )}
      <div className={cell}>
        <dt className={label}>{t('Payment batch')}<InfoTip text={t('The list of who this run pays and how much. Its number stays in KynexOne and is never sent to the bank.')} label={t('Payment batch')} /></dt>
        <dd className={`${value} font-mono`}><bdi>{batch.batchNumber}</bdi></dd>
        <dd className={sub}>{t('Created {date}', { date: fx.date(batch.createdAtUtc) })}</dd>
      </div>
      <div className={cell}>
        <dt className={label}>{t('Employees in file')}</dt>
        <dd className={value}>{employeesInFile === null ? '—' : fx.integer(employeesInFile)}</dd>
        {leftOut !== null && leftOut > 0 && (
          <dd className={sub}>
            {onShowLeftOut
              ? <button type="button" className="font-medium text-primary underline-offset-2 hover:underline" onClick={onShowLeftOut}>{t('{count} paid outside the file', { count: leftOut })}</button>
              : t('{count} paid outside the file', { count: leftOut })}
          </dd>
        )}
      </div>
      <div className={cell}>
        <dt className={label}>{t('Amount in file')}</dt>
        <dd className={value}><bdi dir="ltr">{money(batch.totalAmount)}</bdi></dd>
        {leftOut !== null && leftOut > 0 && <dd className={sub}>{t('After the {count} left out', { count: leftOut })}</dd>}
      </div>
      {difference !== null && (
        <div className={cell}>
          <dt className={label}>{t('Matches the run')}<InfoTip text={t('The file total equals the run’s net pay minus employees paid outside the file. Any difference stops the file.')} label={t('Matches the run')} /></dt>
          {/* Red whenever it does not tie; green only once Check employees has run clean; neutral before. */}
          <dd className={`mt-0.5 flex items-center gap-1 text-body font-semibold tabular-nums ${!matches ? 'text-danger-fg' : checked ? 'text-success-fg' : 'text-fg'}`} data-matches={matches} data-checked={checked}>
            {!matches ? <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" /> : checked ? <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" /> : null}
            {t('Difference {amount}', { amount: two(difference) })}
          </dd>
        </div>
      )}
      {(bankName || accountTail) && (
        <div className={cell}>
          <dt className={label}>{t('Bank')}</dt>
          <dd className="mt-0.5 truncate text-body font-semibold text-fg" title={bankName ?? undefined}>{bankName ?? '—'}</dd>
          {accountTail && <dd className={sub}>{t('Account {tail}', { tail: '' })}<bdi dir="ltr" className="font-mono">{accountTail}</bdi></dd>}
        </div>
      )}
    </dl>
  );

  if (variant === 'rail') {
    const rows: [string, React.ReactNode, string?][] = [
      ...(company ? [[t('Company'), <bdi key="co">{company}</bdi>] as [string, React.ReactNode]] : []),
      [t('Payment batch'), <bdi key="b" className="font-mono">{batch.batchNumber}</bdi>],
      [t('Employees in file'), employeesInFile === null ? '—' : fx.integer(employeesInFile)],
      ...(runNet !== null ? [[t('Run net pay'), <bdi key="n" dir="ltr">{two(runNet)}</bdi>] as [string, React.ReactNode]] : []),
      ...(excluded !== null ? [[t('Paid outside the file'), <bdi key="x" dir="ltr">− {two(excluded)}</bdi>] as [string, React.ReactNode]] : []),
      [t('Amount in file'), <bdi key="a" dir="ltr">{money(batch.totalAmount)}</bdi>],
      ...(difference !== null ? [[t('Matches the run'), <span key="d" className={!matches ? 'text-danger-fg' : checked ? 'text-success-fg' : 'text-fg'}>{t('Difference {amount}', { amount: two(difference) })}</span>] as [string, React.ReactNode]] : []),
      ...(bankName ? [[t('Bank'), bankName] as [string, React.ReactNode]] : []),
      ...(accountTail ? [[t('Debit account'), <bdi key="t" dir="ltr" className="font-mono">{accountTail}</bdi>] as [string, React.ReactNode]] : []),
      ...(run?.lockedAtUtc ? [[t('Locked'), fx.dateTime(run.lockedAtUtc)] as [string, React.ReactNode]] : []),
      ...(file ? [[t('Credit date'), fx.gregorian(file.paymentDate, 'weekdayDate')] as [string, React.ReactNode], [t('File reference'), <bdi key="r" dir="ltr" className="font-mono">{file.reference}</bdi>] as [string, React.ReactNode]] : []),
    ];
    return (
      <aside className="surface sticky top-32 p-4" data-testid="bank-release-summary" data-variant="rail" aria-label={t('Release summary')}>
        <p className="text-body font-semibold text-fg">{t('Release summary')}</p>
        <dl className="mt-3 space-y-2 text-body-sm">
          {rows.map(([k, v]) => <div key={k} className="flex items-baseline justify-between gap-3"><dt className="text-fg-muted">{k}</dt><dd className="text-end font-medium tabular-nums text-fg">{v}</dd></div>)}
        </dl>
      </aside>
    );
  }

  // The tie-out written out, on one line under the cells (a sixth of the width would wrap it into three).
  const formula = tie && (
    <p className="border-t border-line px-4 py-1 text-meta text-fg-muted" data-testid="bank-tie-formula">
      {t('Run net {net} − paid outside the file {outside} = file {file}', { net: two(tie.runNet), outside: two(tie.outside), file: two(tie.expected) })}
    </p>
  );
  return (
    <div data-testid="bank-file-facts">
      <div className="surface hidden sm:block">{strip}{formula}</div>
      {/* Phone: one line, the rest a tap away. */}
      <details className="surface group sm:hidden">
        <summary className="flex min-h-11 cursor-pointer list-none items-center justify-between gap-2 px-4 py-3 text-body">
          <span className="min-w-0">
            {employeesInFile !== null && <>{t('{count, plural, one {# employee} other {# employees}}', { count: employeesInFile })} · </>}
            <strong><bdi dir="ltr">{money(batch.totalAmount)}</bdi></strong>
          </span>
          <ChevronDown className="h-4 w-4 shrink-0 transition-transform group-open:rotate-180 motion-reduce:transition-none" aria-hidden="true" />
        </summary>
        <div className="border-t border-line">{strip}{formula}</div>
      </details>
    </div>
  );
}
