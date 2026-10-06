import { test, expect } from '@playwright/test';
import type { RenewalCaseItem, RenewalRadar } from '../src/api/renewals';
import { renewals } from '../src/i18n/releaseA/renewals';
import { translate } from '../src/i18n/translations';
import { createFormatter } from '../src/lib/format';
import {
  actionKeys, badgeKeys, badgeText, closedStateKeys, consequenceCodes, nextLineKeys, fill, filterItems, formatDay, gapReasonKeys, holdReasonKeys, linkKindKeys,
  nextLine, stageKeys, stepKeys, toggleAll, toggleSelection, unopenedReasonKeys,
} from '../src/lib/renewalRadar';

// Release A R4: the renewal dashboard's sentences come from server codes through these maps. Every code has an
// English sentence and an Arabic one, the storyline lines read exactly as rehearsed, and a tile's drill-down lists
// exactly the rows the tile counted.

const en = (key: string) => translate('en', key);
const fEn = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
const fAr = createFormatter({ locale: 'ar', timeZone: 'Asia/Riyadh' });
const ar = (key: string) => translate('ar', key);

const item = (over: Partial<RenewalCaseItem> = {}): RenewalCaseItem => ({
  caseId: 'c1', contractId: 'k1', contractNumber: 'CON-1',
  employee: { id: 1, publicId: 'p1', name: 'Faisal Al-Qahtani', nameAr: 'فيصل القحطاني', code: 'E-1' },
  companyId: 'co', companyName: 'Masar Facility Services', companyNameAr: 'مسار للخدمات', nationalityClass: 'Saudi', expiringEndDate: '2026-12-31', daysLeft: 86,
  stage: 'Preparing', state: 'Open', holdReason: null, allowedActions: ['ConvertIndefinite', 'NonRenew'], contractAction: null,
  renewalNumber: 2, chainStartedOn: '2023-02-01',
  next: { step: 'PrepareOffer', dueOn: '2026-10-16', consequence: 'RenewsOnCurrentTerms', overdue: false, daysLeft: 10 },
  badges: [{ code: 'Art55Threshold', params: { renewals: '2', maxRenewals: '3', years: '3.9', maxYears: '4' }, blockCode: 'RENEWAL_ART55_THRESHOLD' }],
  blockReasons: [], deadlines: { offerDueOn: '2026-10-16', noticeDueOn: '2026-10-30', qiwaSubmitDueOn: null, qiwaGateDueOn: null, qiwaRespondByOn: null, nextHardDeadline: null },
  fastLaneEligible: false, ...over,
});

test('every code the dashboard can receive has an English and an Arabic sentence', () => {
  const maps = [stepKeys, nextLineKeys, badgeKeys, holdReasonKeys, actionKeys, stageKeys, unopenedReasonKeys, linkKindKeys, gapReasonKeys, closedStateKeys];
  for (const map of maps) {
    for (const key of Object.values(map)) {
      expect(renewals.en[key], key).toBe(key);
      expect(/[؀-ۿ]/.test(renewals.ar[key] ?? ''), key).toBe(true);
    }
  }
  // The server's step, consequence and badge codes (RenewalStepCodes, RenewalConsequenceCodes, RenewalBadgeCodes).
  expect(Object.keys(stepKeys).sort()).toEqual(['Apply', 'ApproveOffer', 'AwaitEmployee', 'ConfirmHistory', 'ExpiredNoOutcome',
    'PrepareOffer', 'RecordQiwaOutcome', 'ResolveHold', 'SendToQiwa', 'ServeNotice']);
  // Every Next-line pair names a known step and consequence.
  for (const pair of Object.keys(nextLineKeys)) {
    const [step, consequence] = pair.split('.');
    expect(stepKeys[step], pair).toBeTruthy();
    expect(consequenceCodes as readonly string[], pair).toContain(consequence);
  }
  expect(Object.keys(badgeKeys).sort()).toEqual(['Art55Meter', 'Art55Threshold', 'ChainUnconfirmed', 'ExpiredHoldoverPending', 'ExpiredNoOutcome',
    'NonSaudiFixedTerm', 'NoticeDatePassed', 'OffboardingOpen', 'OnHold', 'QiwaOverdue'].sort());
});

test("Faisal's badge and Next line read as the storyline says, in English and Arabic", () => {
  const faisal = item();
  expect(badgeText(faisal.badges[0], en)).toBe('Art 55: 2 of 3 renewals, 3.9 of 4 years — only convert to indefinite or non-renew');
  expect(badgeText(faisal.badges[0], ar)).toContain('المادة 55: 2 من 3 تجديدات، 3.9 من 4 سنوات');
  expect(nextLine(faisal, en, fEn, '2026-10-06')).toBe('Next: send offer by 16 Oct — if missed: renews on current terms (Art. 74(2))');
  expect(nextLine(faisal, ar, fAr, '2026-10-06')).toContain('التالي: إرسال العرض قبل');
  expect(nextLine(item({ next: null }), en, fEn)).toBeNull();
  // An unexpected pair still reads as a whole sentence: the step alone.
  expect(nextLine(item({ next: { step: 'Apply', dueOn: '2026-12-31', consequence: 'QiwaLate', overdue: false, daysLeft: 1 } }), en, fEn, '2026-10-06'))
    .toBe('Next: apply the new term by 31 Dec');
});

test('placeholders are filled, and an unknown one stays visible instead of vanishing', () => {
  expect(fill('In {from}–{to} days', { from: 0, to: 30 })).toBe('In 0–30 days');
  expect(fill('On hold: {reason}', {})).toBe('On hold: {reason}');
  expect(badgeText({ code: 'OnHold', params: { reason: 'LabourDispute' }, blockCode: null }, en)).toBe('On hold: Labour dispute');
});

test('dates are Gregorian and add the year only when it differs', () => {
  expect(formatDay('2026-10-16', fEn, '2026-10-06')).toBe('16 Oct');
  expect(formatDay('2027-01-31', fEn, '2026-10-06')).toBe('31 Jan 2027');
  expect(formatDay(null, fEn)).toBe('—');
  expect(formatDay('2026-10-16', fAr, '2026-10-06')).toMatch(/16/);
});

test('a tile drill-down lists exactly the rows the tile counted', () => {
  const radar = {
    today: '2026-10-06', days: 120, openLeadDays: 120, buckets: [], items: [item({ caseId: 'a' }), item({ caseId: 'b' }), item({ caseId: 'c' })],
    exceptions: {} as RenewalRadar['exceptions'], reconciliation: {} as RenewalRadar['reconciliation'],
  } as RenewalRadar;
  expect(filterItems(radar, { kind: 'all' }).map((i) => i.caseId)).toEqual(['a', 'b', 'c']);
  expect(filterItems(radar, { kind: 'bucket', key: '0-30', caseIds: ['c', 'a'] }).map((i) => i.caseId)).toEqual(['a', 'c']);
});

test('only rows eligible for the fast lane can be selected for it', () => {
  const eligible = item({ caseId: 'e', fastLaneEligible: true });
  const blocked = item({ caseId: 'x', fastLaneEligible: false });
  let selected = toggleSelection(new Set(), blocked);
  expect([...selected]).toEqual([]);
  selected = toggleSelection(selected, eligible);
  expect([...selected]).toEqual(['e']);
  expect([...toggleSelection(selected, eligible)]).toEqual([]);
  const all = toggleAll(new Set(), [eligible, blocked, item({ caseId: 'f', fastLaneEligible: true })]);
  expect([...all].sort()).toEqual(['e', 'f']);
  expect([...toggleAll(all, [eligible, blocked, item({ caseId: 'f', fastLaneEligible: true })])]).toEqual([]);
});
