'use client';

import { AlertTriangle, CheckCircle2 } from 'lucide-react';
import type { ReconDto } from '../../api/payrollRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { amountText, normalizeCells, signedAmount, tiesOk, type DrillTarget } from '../../lib/payrollRecon';
import { Table, Td, Th } from '../ui/Table';
import { ReconFigure } from './ReconFigure';
import { fsCellName, fsComponentName } from './reconWords';
import { useReconPeriod } from './useReconPeriod';

/**
 * Final settlements, on an accrual basis. A final settlement is never inside a monthly run: it is
 * accrued in the period it was approved (the period the ledger books it), and paid separately. Each
 * accrued settlement is counted in exactly one cell, so the cells add up to the settlements accrued;
 * leavers in this month's bridge who have no accrued settlement are listed as exceptions.
 */
export function FinalSettlementBlock({ recon, onDrill }: { recon: ReconDto; onDrill: (target: DrillTarget, title: string, opener: HTMLElement) => void }) {
  const t = useT();
  const fmt = useFormat();
  const periodText = useReconPeriod();
  const fs = recon.finalSettlement;
  const cells = normalizeCells(fs.cells);
  const accrued = cells.reduce((s, c) => s + c.count, 0);
  const full = recon.mode === 'Full';
  const plain = (v: number) => fmt.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const num = (v: number) => amountText(v, plain);
  const missing = fs.leaversWithoutFs ?? [];

  return (
    <div className="space-y-4">
      <p className="text-body-sm text-fg-muted">
        {t('Final settlements are counted on an accrual basis: in the period they were approved, which is the period the ledger books them, not when they are paid. They are never part of a monthly run.')}
      </p>

      <Table caption={t('Where each accrued final settlement sits in {period}', { period: periodText(recon.period) })} density="compact">
        <thead>
          <tr>
            <Th>{t('Where the employee is')}</Th>
            <Th numeric>{t('Settlements')}</Th>
          </tr>
        </thead>
        <tbody>
          {cells.map(c => {
            const name = fsCellName(t, c.code);
            return (
              <tr key={c.code} data-testid={`recon-fs-cell-${c.code}`}>
                <Td>{name}</Td>
                <Td numeric>
                  <ReconFigure
                    plain={c.count === 0}
                    label={t('{what}: {count, plural, one {# settlement} other {# settlements}}. Show the employees.', { what: name, count: c.count })}
                    onOpen={opener => onDrill({ section: 'finalSettlement', cell: c.code }, name, opener)}
                  >{fmt.integer(c.count)}</ReconFigure>
                </Td>
              </tr>
            );
          })}
          <tr className="bg-surface-sunken font-semibold" data-testid="recon-fs-total">
            <Td>{t('Settlements accrued in {period}', { period: periodText(recon.period) })}</Td>
            <Td numeric><bdi>{fmt.integer(accrued)}</bdi></Td>
          </tr>
        </tbody>
      </Table>
      {tiesOk(fs.ties) ? (
        <p className="flex items-start gap-2 text-body-sm text-success-fg"><CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{t('Every accrued settlement sits in exactly one cell.')}</p>
      ) : (
        <p role="alert" className="flex items-start gap-2 text-body-sm text-danger-fg"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{t('The final settlement cells do not add up to the settlements accrued. Do not sign off until this is resolved.')}</p>
      )}

      <Table caption={t('Final settlement value by component, {period}', { period: periodText(recon.period) })} density="compact">
        <thead>
          <tr>
            <Th>{t('Component')}</Th>
            {full && <Th numeric>{t('Prior month')}</Th>}
            <Th numeric>{t('This month')}</Th>
            {full && <Th numeric>{t('Change')}</Th>}
          </tr>
        </thead>
        <tbody>
          {fs.perComponent.map(c => {
            const name = c.label || fsComponentName(t, c.code);
            return (
              <tr key={c.code} data-testid={`recon-fs-component-${c.code}`}>
                <Td>{name}</Td>
                {full && <Td numeric><bdi>{num(c.prior)}</bdi></Td>}
                <Td numeric>
                  <ReconFigure
                    plain={c.current === 0}
                    label={t('{what}, this month: {value}. Show the employees.', { what: name, value: num(c.current) })}
                    onOpen={opener => onDrill({ section: 'finalSettlement', component: c.code }, name, opener)}
                  >{num(c.current)}</ReconFigure>
                </Td>
                {full && <Td numeric><bdi>{signedAmount(c.delta, plain)}</bdi></Td>}
              </tr>
            );
          })}
        </tbody>
      </Table>
      <p className="text-meta text-fg-muted">{t('Amounts are in {currency}. Recoveries are separate lines; they are never netted into earnings.', { currency: recon.currency })}</p>

      <div className="space-y-2" data-testid="recon-fs-exceptions">
        <h5 className="text-body font-semibold text-fg">{t('Leavers without an accrued final settlement')}</h5>
        {missing.length === 0 ? (
          <p className="text-body-sm text-fg-muted">{t('Every leaver in this month’s bridge has an accrued final settlement.')}</p>
        ) : (
          <Table caption={t('Leavers without an accrued final settlement')} density="compact">
            <thead>
              <tr><Th>{t('Employee')}</Th><Th>{t('Last working day')}</Th></tr>
            </thead>
            <tbody>
              {missing.map(m => (
                <tr key={m.employeeId} data-testid={`recon-fs-missing-${m.employeeId}`}>
                  <Td>{m.employeeName ? `${m.employeeName}${m.employeeCode ? ` (${m.employeeCode})` : ''}` : t('Employee {id}', { id: m.employeeId })}</Td>
                  <Td><bdi>{m.lastWorkingDay ? fmt.date(m.lastWorkingDay) : '—'}</bdi></Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </div>
    </div>
  );
}
