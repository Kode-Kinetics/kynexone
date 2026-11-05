'use client';

import { AlertTriangle, ChevronDown, ChevronRight } from 'lucide-react';
import { Fragment, useState } from 'react';
import type { ReconAdds, ReconBridgeRow, ReconContinuing, ReconDto, ReconExits, ReconItem, ReconSectionKind } from '../../api/payrollRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { categoryName, groupName, driverName, sectionName } from './reconWords';
import { amountText, categoryOfKey, signedAmount, valueTieIssues, type DrillTarget, type ValueCell } from '../../lib/payrollRecon';
import { Table, Td, Th } from '../ui/Table';
import { useReconPeriod } from './useReconPeriod';
import { ReconFigure } from './ReconFigure';

type Group = 'adds' | 'exits' | 'continuing';
const ADD_KEYS: Array<keyof ReconAdds> = ['newJoiner', 'rehire', 'lateJoiner', 'reinstated', 'transferIn'];
const EXIT_KEYS: Array<keyof ReconExits> = ['leaver', 'transferOut', 'notPaid', 'unexplained'];
const CONT_KEYS: Array<keyof ReconContinuing> = ['package', 'proration', 'variable', 'arrears', 'attendance', 'loans', 'statutory', 'other'];

const sum = (o: object) => Math.round(Object.values(o as Record<string, number>).reduce((s, v) => s + Math.round(v * 100), 0)) / 100;

/**
 * The value bridge: for each earning, deduction and employer cost, what it was last month, what joiners
 * added, what leavers took away, how it moved among continuing staff, and what it is now. Employer cost
 * sits in its own section and never enters net pay. Open a row's breakdown to see each cell; each cell
 * opens the employees behind it.
 */
export function ValueBridgeTable({ recon, onDrill }: { recon: ReconDto; onDrill: (target: DrillTarget, title: string, opener: HTMLElement) => void }) {
  const t = useT();
  const fmt = useFormat();
  const period = useReconPeriod();
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const full = recon.mode === 'Full';
  const { value } = recon;
  const cur = recon.currency;
  const plain = (v: number) => fmt.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const num = (v: number) => amountText(v, plain);
  const signed = (v: number) => signedAmount(v, plain);
  const issues = valueTieIssues(value, full);
  const cols = full ? 8 : 3;

  const toggle = (key: string) => setOpen(prev => { const next = new Set(prev); if (next.has(key)) next.delete(key); else next.add(key); return next; });

  const change = (r: ReconBridgeRow) => (
    <>{signed(r.delta)} <span className="text-fg-muted">({r.deltaPct === null ? '—' : signedAmount(r.deltaPct, v => fmt.percent(v, 1))})</span></>
  );

  const figure = (kind: ReconSectionKind, item: ReconItem, cell: 'prior' | 'current', text: string) => (
    <ReconFigure
      plain={(cell === 'prior' ? item.prior : item.current) === 0}
      label={cell === 'prior'
        ? t('{what}, prior month: {value}. Show the employees.', { what: item.label, value: text })
        : t('{what}, this month: {value}. Show the employees.', { what: item.label, value: text })}
      onOpen={opener => onDrill({ section: 'value', kind, itemCode: item.code, cell }, `${item.label} · ${cell === 'prior' ? t('Prior month') : t('This month')}`, opener)}
    >
      {text}
    </ReconFigure>
  );

  const itemRows = (kind: ReconSectionKind, item: ReconItem) => {
    const key = `${kind}-${item.code}`;
    const expanded = open.has(key);
    const rowsOf = (group: Group, keys: string[], values: Record<string, number>) => keys.filter(k => Math.round(values[k] * 100) !== 0).map(k => {
      const label = group === 'continuing' ? driverName(t, k) : categoryName(t, categoryOfKey(k));
      const text = signed(values[k]);
      const col = group === 'adds' ? 3 : group === 'exits' ? 4 : 5;
      const cells = Array.from({ length: 6 }, (_, i) => (i + 3 === col
        ? (
          <Td key={i} numeric>
            <ReconFigure
              label={t('{what}, {group}, {value}. Show the employees.', { what: `${item.label} · ${label}`, group: groupName(t, group), value: text })}
              onOpen={opener => onDrill({ section: 'value', kind, itemCode: item.code, cell: { group, key: k } as ValueCell }, `${item.label} · ${label}`, opener)}
            >{text}</ReconFigure>
          </Td>
        ) : <Td key={i} />));
      return (
        <tr key={`${key}-${k}`} data-testid={`recon-breakdown-${key}-${k}`} className="bg-surface-sunken/60">
          <Td className="ps-9 text-fg-muted">{label}</Td>
          <Td />
          {cells}
        </tr>
      );
    });

    return (
      <Fragment key={key}>
        <tr data-testid={`recon-item-${key}`}>
          <Td>
            {full ? (
              <button
                type="button"
                aria-expanded={expanded}
                aria-label={expanded ? t('Hide breakdown for {item}', { item: item.label }) : t('Show breakdown for {item}', { item: item.label })}
                onClick={() => toggle(key)}
                className="flex items-center gap-1.5 rounded text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                {expanded ? <ChevronDown className="h-4 w-4 shrink-0 rtl:-scale-x-100" aria-hidden="true" /> : <ChevronRight className="h-4 w-4 shrink-0 rtl:-scale-x-100" aria-hidden="true" />}
                <span>{item.label}</span>
              </button>
            ) : <span>{item.label}</span>}
            {item.explanation && <p className="ps-5 text-meta text-fg-muted">{t('Reason recorded: {text}', { text: item.explanation })}</p>}
          </Td>
          {full && <Td numeric>{figure(kind, item, 'prior', num(item.prior))}</Td>}
          {full && <Td numeric><bdi>{signed(sum(item.adds))}</bdi></Td>}
          {full && <Td numeric><bdi>{signed(sum(item.exits))}</bdi></Td>}
          {full && <Td numeric><bdi>{signed(sum(item.continuing))}</bdi></Td>}
          <Td numeric>{figure(kind, item, 'current', num(item.current))}</Td>
          {full && <Td numeric><bdi>{change(item)}</bdi></Td>}
          {full && (
            <Td>
              {item.flagged && (
                <span className="badge badge-warning" data-testid={`recon-flag-${key}`}>
                  <AlertTriangle className="h-3 w-3" aria-hidden="true" /> {t('Review')}
                  <span className="sr-only"> {t('This moved by more than {pct} or {amount}, so it needs a written reason at sign-off.', { pct: fmt.percent(recon.thresholds.pct, 1), amount: fmt.money(recon.thresholds.minAmount, cur) })}</span>
                </span>
              )}
            </Td>
          )}
        </tr>
        {full && expanded && (
          <>
            {rowsOf('adds', ADD_KEYS, item.adds as unknown as Record<string, number>)}
            {rowsOf('exits', EXIT_KEYS, item.exits as unknown as Record<string, number>)}
            {rowsOf('continuing', CONT_KEYS, item.continuing as unknown as Record<string, number>)}
            {sum(item.adds) === 0 && sum(item.exits) === 0 && sum(item.continuing) === 0 && (
              <tr className="bg-surface-sunken/60"><Td colSpan={cols} className="ps-9 text-fg-muted">{t('Nothing moved on this line.')}</Td></tr>
            )}
          </>
        )}
      </Fragment>
    );
  };

  const total = (id: 'gross' | 'deductions' | 'net' | 'employer', label: string, emphasis = false) => {
    const r = value.headline[id];
    const m = (v: number) => fmt.money(v, cur);
    return (
      <tr key={id} data-testid={`recon-headline-${id}`} className={`${emphasis ? 'font-semibold' : 'font-medium'} bg-surface-sunken`}>
        <Td>{label}</Td>
        {full && <Td numeric><bdi>{m(r.prior)}</bdi></Td>}
        {full && <Td numeric><bdi>{signed(sum(r.adds))}</bdi></Td>}
        {full && <Td numeric><bdi>{signed(sum(r.exits))}</bdi></Td>}
        {full && <Td numeric><bdi>{signed(sum(r.continuing))}</bdi></Td>}
        <Td numeric><bdi>{m(r.current)}</bdi></Td>
        {full && <Td numeric><bdi>{change(r)}</bdi></Td>}
        {full && <Td />}
      </tr>
    );
  };

  const sectionHead = (kind: ReconSectionKind) => (
    <tr key={`h-${kind}`}><th scope="rowgroup" colSpan={cols} className="px-3 py-1.5 text-start text-meta font-semibold uppercase tracking-wide text-fg-muted">{sectionName(t, kind)}</th></tr>
  );
  const sectionItems = (kind: ReconSectionKind) => value.sections.filter(s => s.kind === kind).flatMap(s => s.items.map(i => itemRows(kind, i)));

  return (
    <div className="space-y-3">
      <p className="text-meta text-fg-muted">
        {full
          ? t('Amounts are in {currency}. Open a line’s breakdown to see what joiners added, what leavers took away and what moved among continuing staff.', { currency: cur })
          : t('Amounts are in {currency}. There is no prior month, so this shows how this month’s pay is made up.', { currency: cur })}
      </p>
      {full && (
        <p className="text-meta text-fg-muted" data-testid="recon-flag-explanation">
          {t('A “Review” flag is raised when a line’s total change, including joiners and leavers, is more than {pct} of its prior amount or {amount}, whichever is larger. The employees you confirm at sign-off are counted on their own pay change, among continuing staff only.', { pct: fmt.percent(recon.thresholds.pct, 1), amount: fmt.money(recon.thresholds.minAmount, cur) })}
        </p>
      )}
      <Table caption={t('Value bridge from {prior} to {current}', { prior: period(recon.priorPeriod), current: period(recon.period) })} density="compact" data-testid="recon-value-table">
        <thead>
          <tr>
            <Th>{t('Component')}</Th>
            {full && <Th numeric>{t('Prior month')}</Th>}
            {full && <Th numeric>{t('Added')}</Th>}
            {full && <Th numeric>{t('Left')}</Th>}
            {full && <Th numeric>{t('Continuing staff')}</Th>}
            <Th numeric>{t('This month')}</Th>
            {full && <Th numeric>{t('Change')}</Th>}
            {full && <Th>{t('Check')}</Th>}
          </tr>
        </thead>
        <tbody>
          {sectionHead('Earning')}
          {sectionItems('Earning')}
          {total('gross', t('Gross pay'))}
          {sectionHead('Deduction')}
          {sectionItems('Deduction')}
          {total('deductions', t('Total deductions'))}
          {total('net', t('Net pay'), true)}
          {sectionHead('Employer')}
          {sectionItems('Employer')}
          {total('employer', t('Employer cost'))}
        </tbody>
      </Table>

      {issues.length > 0 && (
        <p role="alert" className="flex items-start gap-2 text-body-sm text-danger-fg" data-testid="recon-value-ties">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span>{t('These lines do not tie, so the bridge cannot be relied on: {lines}.', { lines: issues.join(', ') })}</span>
        </p>
      )}

      <p className="text-meta text-fg-muted" data-testid="recon-register-difference">
        {value.registerDifference === 0
          ? t('Line items match the payslip register (difference {amount}).', { amount: fmt.money(0, cur) })
          : t('Line items differ from the payslip register by {amount}. The difference is shown here, not absorbed into a line.', { amount: fmt.money(value.registerDifference, cur) })}
      </p>
    </div>
  );
}
