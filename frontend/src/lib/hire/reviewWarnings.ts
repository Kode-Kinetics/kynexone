import type { HireForm } from './hireForm';
import { addDays, ageOn, isRealDate } from './dates';

/**
 * The statutory checks (CD6 warnings) the Review step can already foresee from what was typed, as server warning codes.
 * The server stays the authority: it asks for the reason with the same codes when the draft is saved. Nothing here blocks.
 */
export function reviewWarnings(form: HireForm, ctx: { today: string }): string[] {
  const out: string[] = [];
  const start = isRealDate(form.startDate) ? form.startDate : '';
  if (start) {
    if (start < addDays(ctx.today, -30)) out.push('joining_backdated_over_30d');
    if (start > addDays(ctx.today, 180)) out.push('joining_over_180d_future');
  }
  if (start && isRealDate(form.probationEndDate) && form.probationEndDate > addDays(start, 180)) out.push('probation_over_limit');
  if (isRealDate(form.dob)) {
    const age = ageOn(form.dob, start || ctx.today);
    if (age >= 0 && age < 15) out.push('under_minimum_age');
    else if (age >= 15 && age < 18) out.push('juvenile_15_17');
    else if (age > 70) out.push('age_over_70');
  }
  const expiries = [form.idExpiry, form.passportExpiry].filter(isRealDate);
  if (expiries.some(e => e < ctx.today)) out.push('id_expired');
  else if (expiries.some(e => e <= addDays(ctx.today, 90))) out.push('id_expiring_90d');
  return out;
}
