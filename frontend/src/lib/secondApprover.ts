/**
 * H2 — "is there a second person who could approve this?". The server answers it up front (a 422 with code
 * `no_eligible_approver` on every action that stages work only a second person can decide, and the status endpoint below), and
 * ships the sentence in both languages, so the page shows the server's own words and never composes its own.
 */
export type SecondApproverStatus = {
  hasEligibleApprover: boolean;
  /** The refusal the server would give: English and Arabic. */
  message?: string;
  messageAr?: string;
};

/** The notice to show above an action that needs a second approver, or null when one exists (or the status is not known yet). */
export function secondApproverNotice(status: SecondApproverStatus | null | undefined, locale: string): string | null {
  if (!status || status.hasEligibleApprover) return null;
  const text = locale === 'ar' ? status.messageAr || status.message : status.message || status.messageAr;
  return text && text.trim() ? text.trim() : null;
}
