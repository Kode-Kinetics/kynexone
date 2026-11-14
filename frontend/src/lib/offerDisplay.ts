/**
 * What the offer's Approval panel says about sending.
 *
 * WHY. The API returns one fixed sentence, "Open the offer workspace and choose explicit email
 * delivery or record confirmed external sending.", on every offer. On an offer that was already
 * sent it was stale advice. The sentence now shows only while the offer can still be sent; after
 * that the panel states where the offer actually is. Returns an English dictionary key (or the API
 * text) to be passed through t().
 */
const SENDABLE = new Set(['Draft', 'Approved', 'PendingApproval']);

export function offerApprovalNote(status: string, blockedReason: string | null | undefined): string | null {
  if (status === 'Sent') return "Sent to the candidate; awaiting the candidate's response.";
  if (status === 'Accepted') return 'The candidate accepted this offer.';
  if (status === 'Declined') return 'The candidate declined this offer.';
  if (!SENDABLE.has(status)) return null;
  return blockedReason?.trim() ? blockedReason : null;
}
