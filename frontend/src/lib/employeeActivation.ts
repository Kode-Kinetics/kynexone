export interface ActivationForm { effectiveDate: string; reason: string }

/** Today as a yyyy-MM-dd calendar day in the browser's zone (the effective date's default). */
export function todayCalendarDay(now: Date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}`;
}

/**
 * What stops an activation before it is sent. Returns the English message key (the caller translates
 * it), or null when the form is sendable. Activating without a date or a reason used to be impossible
 * to get wrong, because both were hard-coded; now that the owner can edit them they must be checked.
 */
export function activationFormError(form: ActivationForm): 'Choose the date the employee becomes active.' | 'Enter a reason for the activation.' | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(form.effectiveDate) || Number.isNaN(Date.parse(`${form.effectiveDate}T00:00:00Z`))) {
    return 'Choose the date the employee becomes active.';
  }
  if (!form.reason.trim()) return 'Enter a reason for the activation.';
  return null;
}
