/**
 * Cost accounting v1 (WP-F phase 3): the pure rules behind the Payroll "journal by cost centre" report (screen 7).
 * Unit-tested on their own (unit/payrollCostReport.spec.ts).
 */


const cents = (n: number) => Math.round(n * 100) / 100;

export interface AllocationRow {
  costCenterId: string | null;
  costCenterCode: string;
  costCenterName: string;
  employeeCount: number;
  grossSalary: number;
  netSalary: number;
  employerCost: number;
  totalCost: number;
  contraDeductions?: number;
  journalNet?: number | null;
  clearedAgainstAccrual?: number | null;
  roundingAdjustment?: number | null;
  smallCell?: boolean;
}

/** What the stamps imply the journal books for one cost centre (the server's own reconciliation identity, T-025). */
const expectedJournalNet = (r: AllocationRow) =>
  r.grossSalary - (r.contraDeductions ?? 0) + r.employerCost - (r.clearedAgainstAccrual ?? 0) + (r.roundingAdjustment ?? 0);

/** Journal net minus what the stamps imply, over all rows. Null when the run is not stamped (nothing to compare with). */
export function allocationDifference(rows: readonly AllocationRow[]): number | null {
  if (rows.length === 0 || rows.some((r) => r.journalNet == null)) return null;
  return cents(rows.reduce((s, r) => s + ((r.journalNet as number) - expectedJournalNet(r)), 0));
}

const money = (n: number) => n.toFixed(2);
// A spreadsheet runs a cell that starts with = + - @ as a formula: a cost-centre name is user-typed text.
const guard = (s: string) => (/^[=+\-@\t\r]/.test(s) ? `'${s}` : s);
const cell = (s: string) => `"${guard(s).replace(/"/g, '""')}"`;

export interface CsvLabels { headers: string[]; total: string; notRecorded?: string }

/** The on-screen table as CSV: one line per cost centre, then the total. UTF-8 with a BOM so Arabic opens right in Excel. */
export function allocationToCsv(rows: readonly AllocationRow[], labels: CsvLabels): string {
  const lines: string[][] = [labels.headers];
  for (const r of rows) {
    lines.push([
      `${r.costCenterCode} — ${r.costCenterName}`, String(r.employeeCount), money(r.grossSalary), money(r.employerCost), money(r.netSalary),
      r.journalNet == null ? '' : money(r.journalNet), r.clearedAgainstAccrual == null ? '' : money(r.clearedAgainstAccrual),
    ]);
  }
  const sum = (pick: (r: AllocationRow) => number) => rows.reduce((s, r) => s + pick(r), 0);
  const stamped = rows.length > 0 && rows.every((r) => r.journalNet != null);
  lines.push([
    labels.total, String(sum((r) => r.employeeCount)), money(sum((r) => r.grossSalary)), money(sum((r) => r.employerCost)), money(sum((r) => r.netSalary)),
    stamped ? money(sum((r) => r.journalNet ?? 0)) : '', stamped ? money(sum((r) => r.clearedAgainstAccrual ?? 0)) : '',
  ]);
  return `﻿${lines.map((row) => row.map(cell).join(',')).join('\r\n')}`;
}
