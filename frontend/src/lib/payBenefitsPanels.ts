// The panels of an employee record's Pay & Benefits section, in the order they are offered. Labels are i18n keys.
// The package, benefits and deductions panels already exist; this list is what makes them reachable from the section.

export type PayBenefitsPanelId = 'payroll' | 'package' | 'benefits' | 'deductions';

export interface PayBenefitsPanel {
  id: PayBenefitsPanelId;
  label: string;
}

/** Release A shows the contract-year package and deductions; before it the plain benefits panel, only to a reader of benefits. */
export function payBenefitsPanels(access: { releaseA: boolean; canReadBenefits: boolean }): PayBenefitsPanel[] {
  const panels: PayBenefitsPanel[] = [{ id: 'payroll', label: 'Payroll profile' }];
  if (access.releaseA) {
    panels.push({ id: 'package', label: 'Package' }, { id: 'deductions', label: 'Deductions' });
  } else if (access.canReadBenefits) {
    panels.push({ id: 'benefits', label: 'Benefits' });
  }
  return panels;
}
