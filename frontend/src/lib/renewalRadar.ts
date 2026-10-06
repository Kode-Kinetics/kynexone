import type { RenewalBadge, RenewalCaseItem, RenewalRadar } from '../api/renewals';

// Pure helpers for the contract renewals dashboard (Release A slice R4). The server sends codes; this file maps each
// code to ONE English sentence, which is also its i18n key (src/i18n/releaseA/renewals.ts holds the Arabic). Nothing
// here formats numbers the server did not send — every figure on the page traces to a row.

/** Next step code → sentence. `{date}` is the due date. */
export const stepKeys: Record<string, string> = {
  ConfirmHistory: 'Next: confirm the contract history by {date}',
  ResolveHold: 'Next: resolve the hold by {date}',
  PrepareOffer: 'Next: send offer by {date}',
  ApproveOffer: 'Next: get the offer approved by {date}',
  ServeNotice: 'Next: serve the non-renewal notice by {date}',
  AwaitEmployee: 'Next: the employee answers the offer by {date}',
  SendToQiwa: 'Next: send the renewal to Qiwa by {date}',
  RecordQiwaOutcome: 'Next: record the Qiwa outcome by {date}',
  Apply: 'Next: apply the new term by {date}',
  ExpiredNoOutcome: 'The contract ended on {date} with no outcome',
};

/** The consequence codes the server sends (RenewalConsequenceCodes). */
export const consequenceCodes = ['NoOptionsUntilConfirmed', 'RenewsOnCurrentTerms', 'BecomesIndefinite', 'QiwaLate', 'ContinuesByLaw'] as const;

/**
 * "{step}.{consequence}" → ONE whole sentence (Arabic cannot reorder a sentence glued from fragments). These are the
 * pairs the server produces (RenewalNextStep.Next); an unexpected pair falls back to the step sentence alone.
 */
export const nextLineKeys: Record<string, string> = {
  'ConfirmHistory.NoOptionsUntilConfirmed': 'Next: confirm the contract history by {date} — if missed: no renewal option can be offered',
  'ResolveHold.RenewsOnCurrentTerms': 'Next: resolve the hold by {date} — if missed: renews on current terms (Art. 74(2))',
  'ResolveHold.BecomesIndefinite': 'Next: resolve the hold by {date} — if missed: becomes an indefinite contract (Art. 55)',
  'PrepareOffer.RenewsOnCurrentTerms': 'Next: send offer by {date} — if missed: renews on current terms (Art. 74(2))',
  'PrepareOffer.BecomesIndefinite': 'Next: send offer by {date} — if missed: becomes an indefinite contract (Art. 55)',
  'ApproveOffer.RenewsOnCurrentTerms': 'Next: get the offer approved by {date} — if missed: renews on current terms (Art. 74(2))',
  'ApproveOffer.BecomesIndefinite': 'Next: get the offer approved by {date} — if missed: becomes an indefinite contract (Art. 55)',
  'ServeNotice.RenewsOnCurrentTerms': 'Next: serve the non-renewal notice by {date} — if missed: renews on current terms (Art. 74(2))',
  'ServeNotice.BecomesIndefinite': 'Next: serve the non-renewal notice by {date} — if missed: becomes an indefinite contract (Art. 55)',
  'AwaitEmployee.QiwaLate': 'Next: the employee answers the offer by {date} — if missed: Qiwa may not confirm before the contract ends',
  'AwaitEmployee.ContinuesByLaw': 'Next: the employee answers the offer by {date} — if missed: the contract continues by law',
  'SendToQiwa.QiwaLate': 'Next: send the renewal to Qiwa by {date} — if missed: Qiwa may not confirm before the contract ends',
  'RecordQiwaOutcome.ContinuesByLaw': 'Next: record the Qiwa outcome by {date} — if missed: the contract continues by law',
  'Apply.ContinuesByLaw': 'Next: apply the new term by {date} — if missed: the contract continues by law',
  'ExpiredNoOutcome.ContinuesByLaw': 'The contract ended on {date} with no outcome — it continues by law',
};

/** Badge code → sentence. Placeholders are the badge's params. */
export const badgeKeys: Record<string, string> = {
  Art55Threshold: 'Art 55: {renewals} of {maxRenewals} renewals, {years} of {maxYears} years — only convert to indefinite or non-renew',
  Art55Meter: 'Art 55: {renewals} of {maxRenewals} renewals, {years} of {maxYears} years',
  NonSaudiFixedTerm: 'Non-Saudi: fixed-term only (Art. 37)',
  ChainUnconfirmed: 'Contract history not confirmed',
  OnHold: 'On hold: {reason}',
  NoticeDatePassed: 'Notice date passed',
  QiwaOverdue: 'Qiwa overdue',
  ExpiredNoOutcome: 'Ended with no outcome',
  OffboardingOpen: 'Leaving the company',
  ExpiredHoldoverPending: 'Expired — holdover pending',
};

export const holdReasonKeys: Record<string, string> = {
  Resignation: 'Resignation',
  UnpaidLeave: 'Unpaid leave',
  Abroad: 'Abroad',
  Transfer: 'Transfer',
  LabourDispute: 'Labour dispute',
};

export const actionKeys: Record<string, string> = {
  RenewAsIs: 'Renew on current terms',
  RenewWithChanges: 'Renew with changes',
  ConvertIndefinite: 'Convert to indefinite',
  NonRenew: 'Do not renew',
};

export const stageKeys: Record<string, string> = {
  Preparing: 'Preparing',
  Approving: 'Approving',
  WithEmployee: 'With employee',
  Qiwa: 'Qiwa',
  Done: 'Done',
};

/** Why a due contract has no case yet. */
export const unopenedReasonKeys: Record<string, string> = {
  RENEWAL_NO_COMPANY: 'The contract does not name its employing company',
  NationalityUnknown: 'Saudi or non-Saudi is not confirmed',
  AwaitingDailyRun: 'Opens with the next daily run',
  SuccessorOnFile: 'A following contract is already on file',
  DuplicateTerm: 'Two current contracts cover the same end date',
};

/** Closed review states, for the "Active contract with no open review" list. */
export const closedStateKeys: Record<string, string> = {
  Applied: 'Renewal applied',
  NonRenewed: 'Not renewed',
  Cancelled: 'Review cancelled',
};

export const linkKindKeys: Record<string, string> = {
  Original: 'First contract',
  Renewal: 'Renewal',
  Amendment: 'Amended version',
  Recorded: 'Confirmed by HR',
  Unconfirmed: 'Not linked',
};

export const gapReasonKeys: Record<string, string> = {
  GapOrOverlap: 'Does not start the day after the earlier contract ended',
  PredecessorUnconfirmed: 'The earlier contract is not confirmed',
  EarlierTermsNotOnFile: 'Starts after the employee joined: earlier contracts are not on file',
  AmbiguousSuccessor: 'Two contracts claim the same earlier contract',
  Overlap: 'Another contract in force covers its start date',
  ExtendsTerm: 'A new version that extends the contract is a renewal, not an amendment',
  CompanyChanged: 'The earlier contract was with another company',
  StartsBeforeJoining: 'Starts before the employee’s joining date',
  JoiningDateUnknown: 'The employee’s joining date is not recorded',
};

/** Replaces `{name}` placeholders. Unknown placeholders stay visible rather than vanishing silently. */
export function fill(template: string, params: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (whole, name: string) => (name in params ? String(params[name]) : whole));
}

/** The slice of the shared formatter (useFormat / createFormatter) the dashboard needs. */
export interface DayFormatter {
  date: (d: string, style?: 'dayMonth' | 'medium') => string;
}

/** "16 Oct" (or "16 Oct 2027" when not this year) through the shared formatter: the tenant's calendar, Latin digits. */
export function formatDay(iso: string | null | undefined, f: DayFormatter, today?: string): string {
  if (!iso) return '—';
  const sameYear = today ? today.slice(0, 4) === iso.slice(0, 4) : true;
  return f.date(iso, sameYear ? 'dayMonth' : 'medium');
}

/** The translated badge sentence. */
export function badgeText(badge: RenewalBadge, t: (key: string) => string): string {
  const key = badgeKeys[badge.code];
  if (!key) return badge.code;
  const params = { ...badge.params };
  if (badge.code === 'OnHold' && params.reason) params.reason = t(holdReasonKeys[params.reason] ?? params.reason);
  return fill(t(key), params);
}

/** The "Next: … — if missed: …" line as one translated sentence, or null for a closed case. */
export function nextLine(item: Pick<RenewalCaseItem, 'next'>, t: (key: string) => string, f: DayFormatter, today?: string): string | null {
  const next = item.next;
  if (!next) return null;
  const key = nextLineKeys[`${next.step}.${next.consequence}`] ?? stepKeys[next.step];
  return key ? fill(t(key), { date: formatDay(next.dueOn, f, today) }) : next.step;
}

/** A dashboard filter: a bucket or an exception tile, each naming exactly the case ids it counts. */
export type RadarFilter = { kind: 'all' } | { kind: 'bucket' | 'exception'; key: string; caseIds: string[] };

/** The rows a filter selects: always exactly the ids its tile counted, so tile and list reconcile. */
export function filterItems(radar: RenewalRadar, filter: RadarFilter): RenewalCaseItem[] {
  if (filter.kind === 'all') return radar.items;
  const ids = new Set(filter.caseIds);
  return radar.items.filter((i) => ids.has(i.caseId));
}

/** Selection for R5's batch fast lane: only eligible rows can be selected. */
export function toggleSelection(selected: ReadonlySet<string>, item: Pick<RenewalCaseItem, 'caseId' | 'fastLaneEligible'>): Set<string> {
  const next = new Set(selected);
  if (next.has(item.caseId)) next.delete(item.caseId);
  else if (item.fastLaneEligible) next.add(item.caseId);
  return next;
}

/** Selects every eligible row in view, or clears them when they are all selected already. */
export function toggleAll(selected: ReadonlySet<string>, rows: Pick<RenewalCaseItem, 'caseId' | 'fastLaneEligible'>[]): Set<string> {
  const eligible = rows.filter((r) => r.fastLaneEligible).map((r) => r.caseId);
  const allOn = eligible.length > 0 && eligible.every((id) => selected.has(id));
  const next = new Set(selected);
  for (const id of eligible) {
    if (allOn) next.delete(id);
    else next.add(id);
  }
  return next;
}

/** The plain-language text of a block reason in the current language. */
export function blockText(reason: { titleEn: string; titleAr: string; whyEn: string; whyAr: string; fixEn: string; fixAr: string }, locale: string) {
  const ar = locale === 'ar';
  return { title: ar ? reason.titleAr : reason.titleEn, why: ar ? reason.whyAr : reason.whyEn, fix: ar ? reason.fixAr : reason.fixEn };
}
