import type { InterviewFeedback } from '../api/recruitment';

/** A "YYYY-MM-DDTHH:mm" wall time (datetime-local) that is already behind `now`. Blank is not past: that is the "required" message's job. */
export function interviewStartIsPast(local: string, now: Date = new Date()): boolean {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(local)) return false;
  const start = new Date(local);
  return !Number.isNaN(start.getTime()) && start.getTime() < now.getTime();
}

export interface FeedbackRow { id: string; interviewer: string; rating: number; recommendation: string; notes: string; submittedAtUtc: string }

/** What the interview screen shows for the submitted feedback, newest first. */
export function interviewFeedbackRows(items: Pick<InterviewFeedback, 'id' | 'interviewerName' | 'overallScore' | 'recommendation' | 'notes' | 'submittedAtUtc'>[]): FeedbackRow[] {
  return items
    .map(i => ({ id: i.id, interviewer: i.interviewerName, rating: i.overallScore, recommendation: i.recommendation, notes: i.notes, submittedAtUtc: i.submittedAtUtc }))
    .sort((a, b) => Date.parse(b.submittedAtUtc) - Date.parse(a.submittedAtUtc));
}

export const HANDOFF_LABELS: Record<string, string> = {
  NeedsReview: 'Needs review', Confirmed: 'Confirmed', NotApplicable: 'Not applicable', LegacySnapshotUnavailable: 'Old offer details are not available',
  AcceptedOffer: 'Accepted offer', ConfirmedLegacyPlacement: 'Confirmed placement of an older offer', ExplicitLegacyPlacementRequired: 'Placement of an older offer must be set explicitly',
  UnsupportedMissingSourceCompany: 'Original company is missing, not supported',
  Payroll: 'Payroll', NamedProcess: 'Named process', OfferOnlyBenefit: 'Offer-only benefit', Excluded: 'Excluded',
};

/** The plain English for a raw compensation-handoff state, provenance or disposition; anything unknown is returned as it came. */
export function handoffLabel(raw: string): string {
  return HANDOFF_LABELS[raw] ?? raw;
}

/** The plain sentence for where the sending address comes from (the API's own value is a technical string), or null when unknown. */
export function senderSourceKey(source: string | null | undefined): string | null {
  const v = (source ?? '').trim();
  if (/^Tenant\b/i.test(v)) return 'Sent from your company email';
  if (/^Platform\b/i.test(v)) return 'Sent from the platform email';
  return null;
}

/** "Name <address>", the way a mail client shows a sender. */
export function fromLine(name: string | null | undefined, address: string | null | undefined): string {
  const n = (name ?? '').trim(); const a = (address ?? '').trim();
  return n && a ? `${n} <${a}>` : n || a;
}

/** The text direction of a select showing a person's name: Latin names read left to right (so the start of a long label is what shows), Arabic names right to left; nothing chosen follows the page. */
export function applicationDir(name: string | null | undefined): 'ltr' | 'rtl' | undefined {
  const n = (name ?? '').trim();
  if (!n) return undefined;
  return /[\u0600-\u06FF]/.test(n) ? 'rtl' : 'ltr';
}
