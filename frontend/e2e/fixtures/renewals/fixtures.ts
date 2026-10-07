import type { ContractChain, RenewalCaseItem, RenewalRadar } from '../../../src/api/renewals';

// Fixtures in the exact shapes the R4 API returns (RenewalCaseReadModel / ContractRenewalsController.Radar), modelled on
// the reviewer's fx-radar.json / fx-chain-*.json and the demo cast. Today is 6 Oct 2026.

const today = '2026-10-06';
const masar = { companyId: 'co-1', companyName: 'Masar Facility Services Co.', companyNameAr: 'شركة مسار للخدمات' };
const deadlines = (offer: string, notice: string) => ({
  offerDueOn: offer, noticeDueOn: notice, qiwaSubmitDueOn: null, qiwaGateDueOn: null, qiwaRespondByOn: null, nextHardDeadline: offer,
});

const faisal: RenewalCaseItem = {
  caseId: 'case-faisal', contractId: 'con-faisal', contractNumber: 'CON-2026-0003',
  employee: { id: 3, publicId: 'emp-faisal', name: 'Faisal Al-Qahtani', nameAr: 'فيصل القحطاني', code: 'E-003' },
  ...masar, nationalityClass: 'Saudi', expiringEndDate: '2026-12-31', daysLeft: 86, stage: 'Preparing', state: 'Open', holdReason: null,
  allowedActions: ['ConvertIndefinite', 'NonRenew'], contractAction: null, renewalNumber: 2, chainStartedOn: '2023-02-01',
  next: { step: 'PrepareOffer', dueOn: '2026-10-18', consequence: 'BecomesIndefinite', overdue: false, daysLeft: 12 },
  badges: [{ code: 'Art55Threshold', params: { renewals: '2', maxRenewals: '3', years: '3.9', maxYears: '4' }, blockCode: 'RENEWAL_ART55_THRESHOLD' }],
  blockReasons: [], deadlines: deadlines('2026-10-18', '2026-11-01'), fastLaneEligible: false,
};

const ramon: RenewalCaseItem = {
  caseId: 'case-ramon', contractId: 'con-ramon', contractNumber: 'CON-2026-0011',
  employee: { id: 11, publicId: 'emp-ramon', name: 'Ramon Dela Cruz', nameAr: 'رامون ديلا كروز', code: 'E-011' },
  ...masar, nationalityClass: 'NonSaudi', expiringEndDate: '2026-11-30', daysLeft: 55, stage: 'Preparing', state: 'Open', holdReason: null,
  allowedActions: ['RenewAsIs', 'RenewWithChanges', 'NonRenew'], contractAction: null, renewalNumber: 1, chainStartedOn: '2025-12-01',
  next: { step: 'PrepareOffer', dueOn: '2026-10-16', consequence: 'RenewsOnCurrentTerms', overdue: false, daysLeft: 10 },
  badges: [{ code: 'NonSaudiFixedTerm', params: {}, blockCode: null }],
  blockReasons: [], deadlines: deadlines('2026-10-16', '2026-10-01'), fastLaneEligible: true,
};

const mohamed: RenewalCaseItem = {
  caseId: 'case-mohamed', contractId: 'con-mohamed', contractNumber: 'CON-2025-0090',
  employee: { id: 21, publicId: 'emp-mohamed', name: 'Mohamed Hassan', nameAr: 'محمد حسن', code: 'E-021' },
  ...masar, nationalityClass: 'NonSaudi', expiringEndDate: '2026-09-30', daysLeft: -6, stage: 'Preparing', state: 'Open', holdReason: null,
  allowedActions: ['RenewAsIs', 'RenewWithChanges'], contractAction: null, renewalNumber: 0, chainStartedOn: '2025-10-01',
  next: { step: 'ExpiredNoOutcome', dueOn: '2026-09-30', consequence: 'ContinuesByLaw', overdue: true, daysLeft: -6 },
  badges: [{ code: 'ExpiredNoOutcome', params: {}, blockCode: null }, { code: 'ExpiredHoldoverPending', params: {}, blockCode: null }],
  blockReasons: [], deadlines: deadlines('2026-07-18', '2026-08-01'), fastLaneEligible: false,
};

export const radar: RenewalRadar = {
  today, days: 120, openLeadDays: 120,
  buckets: [
    { key: 'overdue', fromDays: -1, toDays: -1, count: 1, caseIds: [mohamed.caseId] },
    { key: '0-30', fromDays: 0, toDays: 30, count: 0, caseIds: [] },
    { key: '31-60', fromDays: 31, toDays: 60, count: 1, caseIds: [ramon.caseId] },
    { key: '61-90', fromDays: 61, toDays: 90, count: 1, caseIds: [faisal.caseId] },
    { key: '91-120', fromDays: 91, toDays: 120, count: 0, caseIds: [] },
  ],
  reconciliation: {
    dueActiveContracts: 3, withOpenReview: 2, withoutReview: 1, withClosedReviewOnly: 0, notYetDue: 1,
    openReviewCaseIds: [faisal.caseId, ramon.caseId],
    notYetDueContracts: [{
      contractId: 'con-noura', contractNumber: 'CON-2026-0040', employee: { id: 40, publicId: 'emp-noura', name: 'Noura Al-Otaibi', nameAr: 'نورة العتيبي', code: 'E-040' },
      endDate: '2027-03-31', opensOn: '2026-12-01', reason: 'NotDue', blockReason: null,
    }],
  },
  exceptions: {
    expiringWithoutCase: [{
      contractId: 'con-khaled', contractNumber: 'CON-2026-0021', employee: { id: 22, publicId: 'emp-khaled', name: 'Khaled Al-Sabah', nameAr: 'خالد الصباح', code: 'E-022' },
      endDate: '2026-12-31', opensOn: '2026-09-02', reason: 'NationalityUnknown',
      blockReason: {
        code: 'RENEWAL_NATIONALITY_UNCONFIRMED', titleEn: 'Saudi or non-Saudi not confirmed', titleAr: 'لم يتم تأكيد سعودي أو غير سعودي',
        whyEn: 'The recorded nationality is a GCC nationality.', whyAr: 'الجنسية المسجلة خليجية.',
        fixEn: "Record the employee's nationality, or confirm Saudi or non-Saudi in the contract history.",
        fixAr: 'سجّل جنسية الموظف، أو أكّد سعودي أو غير سعودي في سجل العقود.', ownerRole: 'HR Manager',
      },
    }],
    activeWithoutOpenReview: [],
    needsConfirmation: { count: 0, caseIds: [] },
    noticeDatePassed: { count: 0, caseIds: [] },
    qiwaOverdue: { count: 0, caseIds: [] },
    art55Threshold: { count: 1, caseIds: [faisal.caseId] },
    expiredNoOutcome: { count: 1, caseIds: [mohamed.caseId] },
    expiredHoldoverPending: { count: 1, caseIds: [mohamed.caseId] },
  },
  items: [mohamed, ramon, faisal],
};

export const faisalChain: ContractChain = {
  contractId: 'con-faisal',
  terms: [
    { contractId: 'con-f1', contractNumber: 'CON-2023-0001', status: 'Expired', startDate: '2023-02-01', endDate: '2024-12-31', signedOn: '2023-01-20',
      renewalNumber: 0, linkKind: 'Original', linkedToContractId: null, gapReason: null, isCurrent: false },
    { contractId: 'con-f2', contractNumber: 'CON-2025-0002', status: 'Expired', startDate: '2025-01-01', endDate: '2025-12-31', signedOn: '2024-12-10',
      renewalNumber: 1, linkKind: 'Renewal', linkedToContractId: 'con-f1', gapReason: null, isCurrent: false },
    { contractId: 'con-faisal', contractNumber: 'CON-2026-0003', status: 'Active', startDate: '2026-01-01', endDate: '2026-12-31', signedOn: '2025-12-15',
      renewalNumber: 2, linkKind: 'Renewal', linkedToContractId: 'con-f2', gapReason: null, isCurrent: true },
  ],
  renewalNumber: 2, chainStartedOn: '2023-02-01', confirmed: true, nationalityClass: 'Saudi', autoRenew: true, nonRenewalNoticeDays: null,
  art55: { nationalityClass: 'Saudi', renewalsUsed: 2, maxRenewals: 3, yearsServed: 3.9, yearsIfRenewed: 4.9, maxYears: 4, thresholdReached: true, reading: 'Conservative' },
  nextAllowedActions: ['ConvertIndefinite', 'NonRenew'],
  blockReasons: [],
  caseId: 'case-faisal', caseState: 'Open', opensOn: '2026-09-02',
};
