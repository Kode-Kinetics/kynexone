import { notifyApiError } from '../../api/client';
import { renewalErrorText } from '../../lib/renewalRadar';

/** Shows a failed renewal request in the viewer's language (catalogue reason or the server's EN/AR message). */
export function notifyRenewalError(err: unknown, locale: string, fallback: string): void {
  const status = (err as { response?: { status?: number } })?.response?.status;
  notifyApiError({ response: { status, data: { message: renewalErrorText(err, locale, fallback) } } }, fallback);
}
