export function employeeActivationFailure(error: unknown, translate: (message: string) => string = message => message): string {
  const data = (error as { response?: { data?: { blocking?: { label?: string }[]; message?: string } } } | null)?.response?.data;
  const labels = Array.isArray(data?.blocking)
    ? data.blocking.map(item => item?.label).filter((label): label is string => typeof label === 'string' && !!label.trim())
    : [];
  if (labels.length) return [...new Set(labels)].map(translate).join(' · ');
  if (typeof data?.message === 'string' && data.message.trim()) return translate(data.message);
  return translate('The employee could not be activated. Reload the readiness checks and try again.');
}

/**
 * The refusal as the server wrote it: its `message`, verbatim and untranslated, so what the person
 * reads is what the audit trail holds. A body with no message falls back to the generic sentence.
 */
export function activationRejectionMessage(error: unknown, translate: (message: string) => string = message => message): string {
  const message = (error as { response?: { data?: { message?: unknown } } } | null)?.response?.data?.message;
  if (typeof message === 'string' && message.trim()) return message;
  return translate('The employee could not be activated. Reload the readiness checks and try again.');
}

/**
 * The server refused to return a leaver to work because their final settlement for the current service is approved or paid
 * (409 `rehire_required`): only Rehire starts a new service period. Returns the server's sentence in the viewer's language
 * (`messageAr` in Arabic), or null for any other failure.
 */
export function settledServiceRefusal(error: unknown, locale: string): string | null {
  const data = (error as { response?: { data?: { code?: unknown; message?: unknown; messageAr?: unknown } } } | null)?.response?.data;
  if (data?.code !== 'rehire_required') return null;
  const pick = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);
  return (locale === 'ar' ? pick(data.messageAr) ?? pick(data.message) : pick(data.message) ?? pick(data.messageAr));
}
