import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { bankFileProductionAllowed, bankTemplatesAllowed } from '../src/lib/payrollPaymentAccess';

// The Payroll Manager prepares the Saudi bank file (payroll.payment_batch.manage); payroll.export keeps working. The custom
// layout templates stay with payroll.export, as on the server.

const only = (...keys: string[]) => (p: string) => keys.includes(p);

test('the bank file panel opens to the payment-batch key or payroll.export, and to nobody else', () => {
  expect(bankFileProductionAllowed(only('payroll.payment_batch.manage'))).toBe(true);
  expect(bankFileProductionAllowed(only('payroll.export'))).toBe(true);
  expect(bankFileProductionAllowed(only('payroll.payment.settle', 'payroll.wps_status.update', 'payroll.remit.manage', 'payroll.read'))).toBe(false);
  expect(bankFileProductionAllowed(() => false)).toBe(false);
});

test('custom bank templates stay with payroll.export holders', () => {
  expect(bankTemplatesAllowed(only('payroll.export'))).toBe(true);
  expect(bankTemplatesAllowed(only('payroll.payment_batch.manage'))).toBe(false);
});

test('the gate and the panel use those rules rather than a bare payroll.export check', () => {
  const read = (rel: string) => fs.readFileSync(path.resolve(__dirname, '../src', rel), 'utf8');
  const gate = read('components/payroll/SaudiBankExportGate.tsx');
  const panel = read('components/payroll/SaudiBankExportPanel.tsx');
  expect(gate).toContain('bankFileProductionAllowed(hasPermission)');
  expect(gate).not.toContain("hasPermission('payroll.export')");
  expect(panel).toContain('bankFileProductionAllowed(hasPermission)');
  expect(panel).toContain('bankTemplatesAllowed(hasPermission)');
  expect(panel).not.toContain("hasPermission('payroll.export')");
});
