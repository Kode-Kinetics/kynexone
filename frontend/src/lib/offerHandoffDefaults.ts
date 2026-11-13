/**
 * The compensation handoff starts each standard component on its payroll field instead of eight blank selects: basic, housing, transport
 * and other allowances (any monthly cash component that names a payroll field) go to that field, and an excluded component stays excluded.
 * Everything else (benefits, annual and one-time items, components with no payroll field) is left for the person to decide, and a
 * disposition already saved is never replaced. The API still validates every disposition, so this only saves the clicking.
 */
import type { OfferCompensationDisposition } from '../api/employeeDrafts';

export interface HandoffComponent {
  code: string;
  kind: string;
  frequency?: string | null;
  included: boolean;
  structureField?: string | null;
}

const PAYROLL_FIELDS = ['BasicSalary', 'HousingAllowance', 'TransportAllowance', 'FoodAllowance', 'MobileAllowance', 'OtherAllowance'];

export function defaultDispositions(components: ReadonlyArray<HandoffComponent>, saved: ReadonlyArray<OfferCompensationDisposition>): OfferCompensationDisposition[] {
  const used = new Set<string>(saved.filter(d => d.action === 'Payroll' && d.payrollField).map(d => d.payrollField as string));
  return components.map((c): OfferCompensationDisposition => {
    const existing = saved.find(d => d.code === c.code);
    if (existing) return existing;
    if (!c.included) return { code: c.code, action: 'Excluded' };
    const field = c.code === 'BASIC' ? 'BasicSalary' : c.structureField ?? null;
    if (c.kind === 'Cash' && c.frequency === 'Monthly' && field && PAYROLL_FIELDS.includes(field) && !used.has(field)) {
      used.add(field);
      return { code: c.code, action: 'Payroll', payrollField: field };
    }
    return { code: c.code, action: '' };
  });
}
