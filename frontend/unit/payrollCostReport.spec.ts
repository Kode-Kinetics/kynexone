import { expect, test } from '@playwright/test';
import { allocationDifference, allocationToCsv } from '../src/lib/payrollCostReport';

test.describe('allocation report', () => {
  const rows = [
    { costCenterId: 'a', costCenterCode: 'SALES', costCenterName: 'Sales', employeeCount: 14, grossSalary: 1000, netSalary: 900, employerCost: 100, totalCost: 1100, contraDeductions: 50, journalNet: 1050, clearedAgainstAccrual: 0, roundingAdjustment: 0 },
    { costCenterId: 'b', costCenterCode: 'OPS', costCenterName: 'Ops, "main"', employeeCount: 9, grossSalary: 500, netSalary: 450, employerCost: 50, totalCost: 550, contraDeductions: 0, journalNet: 600, clearedAgainstAccrual: 0, roundingAdjustment: 0 },
  ];

  test('difference is the sum of journal-net minus what the stamps imply, null when not stamped', () => {
    expect(allocationDifference(rows)).toBe(50);
    expect(allocationDifference([{ ...rows[0], journalNet: null }])).toBeNull();
    expect(allocationDifference([rows[0]])).toBe(0);
  });

  test('csv is the on-screen table, quoted, with a total row and a formula guard', () => {
    const csv = allocationToCsv(rows, { headers: ['Cost centre', 'Headcount', 'Gross', 'Employer cost', 'Net', 'Journal net', 'Cleared against accrual'], total: 'Total' });
    const lines = csv.replace(/^﻿/, '').split('\r\n');
    expect(lines[0]).toBe('"Cost centre","Headcount","Gross","Employer cost","Net","Journal net","Cleared against accrual"');
    expect(lines[1]).toBe('"SALES — Sales","14","1000.00","100.00","900.00","1050.00","0.00"');
    expect(lines[2]).toBe('"OPS — Ops, ""main""","9","500.00","50.00","450.00","600.00","0.00"');
    expect(lines[3]).toBe('"Total","23","1500.00","150.00","1350.00","1650.00","0.00"');
    const evil = allocationToCsv([{ ...rows[0], costCenterCode: '=SUM(1)' }], { headers: ['a'], total: 'T' });
    expect(evil).toContain(`"'=SUM(1) — Sales"`);
  });
});
