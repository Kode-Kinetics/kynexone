'use client';

import { AlertTriangle, CheckCircle2 } from 'lucide-react';
import type { ReconDto, ReconVolumeLine, VolumeAddCategory, VolumeCategory, VolumeExitCategory } from '../../api/payrollRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { offCycleSummary, tiesOk, volumeMovement, volumeTies, type DrillTarget } from '../../lib/payrollRecon';
import { Table, Td, Th } from '../ui/Table';
import { ReconFigure } from './ReconFigure';
import { categoryHint, categoryName } from './reconWords';
import { useReconPeriod } from './useReconPeriod';

const ADDS: VolumeAddCategory[] = ['NEW_JOINER', 'REHIRE', 'LATE_JOINER', 'REINSTATED', 'TRANSFER_IN'];
const EXITS: VolumeExitCategory[] = ['LEAVER', 'TRANSFER_OUT', 'NOT_PAID', 'UNEXPLAINED'];

/**
 * The headcount bridge: employees paid last month, who joined, who left, employees paid this month.
 * Every movement is listed, including the ones at zero, so a reader can see what was checked. Each
 * non-zero count opens the employees behind it. The bridge says whether it ties; one that does not is
 * never presented as a bridge.
 */
export function VolumeBridgeTable({ recon, onDrill }: { recon: ReconDto; onDrill: (target: DrillTarget, title: string, opener: HTMLElement) => void }) {
  const t = useT();
  const fmt = useFormat();
  const period = useReconPeriod();
  const { volume } = recon;
  const full = recon.mode === 'Full';
  const lineOf = (c: string): ReconVolumeLine | undefined => volume.lines.find(l => l.category === c);
  const move = volumeMovement(volume);
  const ties = tiesOk(volume.ties) && (!full || volumeTies(volume));
  const n = (v: number) => fmt.integer(v);

  const countCell = (category: VolumeCategory, count: number, sign: '+' | '−' | '') => {
    const shown = count === 0 ? n(0) : `${sign}${n(count)}`;
    const name = categoryName(t, category);
    return (
      <ReconFigure
        plain={count === 0}
        label={t('{what}: {count, plural, one {# employee} other {# employees}}. Show the employees.', { what: name, count })}
        onOpen={opener => onDrill({ section: 'volume', category }, `${name} · ${t('{count, plural, one {# employee} other {# employees}}', { count })}`, opener)}
      >
        {shown}
      </ReconFigure>
    );
  };

  const detail = (c: VolumeAddCategory | VolumeExitCategory, line: ReconVolumeLine | undefined) => {
    const count = line?.count ?? 0;
    const parts: string[] = [];
    if (count > 0 || c === 'UNEXPLAINED') parts.push(categoryHint(t, c));
    if (c === 'LEAVER' && count > 0) parts.push(t('Final settlement: {approved} accrued, {missing} missing.', { approved: n(line?.fsApproved ?? 0), missing: n(line?.fsMissing ?? 0) }));
    return parts.join(' ');
  };

  const row = (c: VolumeAddCategory | VolumeExitCategory, sign: '+' | '−') => {
    const line = lineOf(c);
    const count = line?.count ?? 0;
    return (
      <tr key={c} data-testid={`recon-volume-row-${c}`}>
        <Td>{categoryName(t, c)}</Td>
        <Td numeric>{countCell(c, count, sign)}</Td>
        <Td className="text-fg-muted">{detail(c, line)}</Td>
      </tr>
    );
  };

  const section = (label: string) => (
    <tr><th scope="rowgroup" colSpan={3} className="bg-surface-sunken px-3 py-1.5 text-start text-meta font-semibold uppercase tracking-wide text-fg-muted">{label}</th></tr>
  );

  const off = volume.memo?.offCycle;
  const offText = (v: unknown) => {
    const s = offCycleSummary(v as Parameters<typeof offCycleSummary>[0]);
    const runs = t('{count, plural, one {# run} other {# runs}}', { count: s.count });
    return s.gross === null || s.count === 0 ? runs : `${runs} · ${fmt.money(s.gross, recon.currency)}`;
  };

  return (
    <div className="space-y-3">
      <Table caption={full ? t('Headcount bridge from {prior} to {current}', { prior: period(recon.priorPeriod), current: period(recon.period) }) : t('Headcount of {current}', { current: period(recon.period) })} density="compact">
        <thead>
          <tr>
            <Th>{t('Movement')}</Th>
            <Th numeric>{t('Employees')}</Th>
            <Th>{t('What it means')}</Th>
          </tr>
        </thead>
        <tbody>
          {full && (
            <tr data-testid="recon-volume-opening">
              <Td className="font-semibold">{t('Paid in {period}', { period: period(recon.priorPeriod) })}</Td>
              <Td numeric className="font-semibold">
                <ReconFigure
                  label={t('{what}: {count, plural, one {# employee} other {# employees}}. Show the employees.', { what: t('Opening headcount'), count: volume.opening })}
                  onOpen={opener => onDrill({ section: 'volume', category: 'OPENING' }, t('Opening headcount · {count, plural, one {# employee} other {# employees}}', { count: volume.opening }), opener)}
                  plain={volume.opening === 0}
                >{n(volume.opening)}</ReconFigure>
              </Td>
              <Td className="text-fg-muted">{t('Employees with a payslip in the prior month’s run.')}</Td>
            </tr>
          )}
          {full && section(t('Added'))}
          {full && ADDS.map(c => row(c, '+'))}
          {full && section(t('Left'))}
          {full && EXITS.map(c => row(c, '−'))}
          <tr data-testid="recon-volume-closing">
            <Td className="font-semibold">{t('Paid in {period}', { period: period(recon.period) })}</Td>
            <Td numeric className="font-semibold">
              <ReconFigure
                label={t('{what}: {count, plural, one {# employee} other {# employees}}. Show the employees.', { what: t('Closing headcount'), count: volume.closing })}
                onOpen={opener => onDrill({ section: 'volume', category: 'CLOSING' }, t('Closing headcount · {count, plural, one {# employee} other {# employees}}', { count: volume.closing }), opener)}
                plain={volume.closing === 0}
              >{n(volume.closing)}</ReconFigure>
            </Td>
            <Td className="text-fg-muted">{t('Employees with a payslip in this run.')}</Td>
          </tr>
        </tbody>
      </Table>

      {full && (ties ? (
        <p className="flex items-start gap-2 text-body-sm text-success-fg" data-testid="recon-volume-ties">
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span><bdi>{t('{opening} opening + {adds} added − {exits} left = {closing} closing. The bridge ties.', { opening: n(volume.opening), adds: n(move.adds), exits: n(move.exits), closing: n(volume.closing) })}</bdi></span>
        </p>
      ) : (
        <p role="alert" className="flex items-start gap-2 text-body-sm text-danger-fg" data-testid="recon-volume-ties">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span><bdi>{t('The headcount bridge does not tie: {opening} opening + {adds} added − {exits} left, against {closing} closing. Do not sign off until this is resolved.', { opening: n(volume.opening), adds: n(move.adds), exits: n(move.exits), closing: n(volume.closing) })}</bdi></span>
        </p>
      ))}

      <p className="text-meta text-fg-muted" data-testid="recon-volume-memo">
        {t('{count, plural, one {# employee is in their final wage month in this run.} other {# employees are in their final wage month in this run.}}', { count: volume.memo?.finalWageMonth ?? 0 })}{' '}
        {off && t('Off-cycle runs sit outside this bridge. Prior month: {prior}. This month: {current}.', { prior: offText(off.prior), current: offText(off.current) })}
      </p>
    </div>
  );
}
