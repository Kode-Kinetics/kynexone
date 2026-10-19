import { expect, test } from '@playwright/test';
import * as review from '../src/lib/policyReview';

test('checkpoint review context restores only for the same company, independently of ordinary form changes', () => {
  expect(review).toHaveProperty('checkpointMatchesReviewCompany');
  const checkpoint = JSON.stringify({ value: { attendanceMethods: ['WebCheckIn'] }, profile: { companyId: 'company-a', branchCity: 'Riyadh' } });
  expect(review.checkpointMatchesReviewCompany(checkpoint, 'company-a')).toBe(true);
  expect(review.checkpointMatchesReviewCompany(checkpoint, 'company-b')).toBe(false);
  expect(review.checkpointMatchesReviewCompany(checkpoint, undefined)).toBe(false);
  const changedForm = JSON.stringify({ value: { attendanceMethods: ['Manual'] }, profile: { companyId: 'company-a', branchCity: 'Jeddah' } });
  expect(review.checkpointMatchesReviewCompany(changedForm, 'company-a')).toBe(true);
  expect(review.checkpointMatchesReviewCompany(JSON.stringify({ value: {}, profile: {} }), undefined)).toBe(true);
  expect(review.checkpointMatchesReviewCompany(JSON.stringify({ value: {}, profile: {} }), 'company-a')).toBe(false);
  for (const malformed of ['', '{', 'null', '{}', '{"profile":null}']) expect(review.checkpointMatchesReviewCompany(malformed, undefined)).toBe(false);
});

const source = { documentId: 'policy-3', contentSha256: 'version-a', sourceName: 'Policy 3', sourceQuote: 'Probation lasts 90 days.', sourceStart: 100, sourceLength: 23 };
const item = { id: 'probation-conflict', itemType: 'issue' as const, kind: 'conflict' as const, section: 'Employment confirmation', summary: 'Confirm whether these are distinct milestones.', sources: [source], target: 'profile' as const, field: 'probationMonths' };

// A targeted review block must never become a blanket lock on unrelated settings.
test('an unresolved probation conflict blocks probation but leaves attendance review available', () => {
  expect(review.blockingReviewItems).toBeInstanceOf(Function);
  expect(review.blockingReviewItems([item], {}, { target: 'profile', field: 'probationMonths' })).toHaveLength(1);
  expect(review.blockingReviewItems([item], {}, { target: 'configuration', field: 'attendanceMethods' })).toEqual([]);
});

test('a comment alone cannot mark a conflicting clause ready for a configuration change', () => {
  expect(review.validReviewDecision).toBeInstanceOf(Function);
  expect(review.validReviewDecision(item, { disposition: '', comment: 'Looks fine.', owner: '' }).valid).toBe(false);
  expect(review.validReviewDecision(item, { disposition: 'NeedsClarification', comment: 'Ask HR to distinguish the dates.', owner: 'HR Director' }).valid).toBe(true);
  expect(review.blockingReviewItems([item], { [item.id]: { disposition: 'NeedsClarification', comment: 'Ask HR.', owner: 'HR Director' } }, { target: 'profile', field: 'probationMonths' })).toHaveLength(1);
});

test('recording a manual process never clears an unsupported operational rule into an applied setting', () => {
  expect(review.validReviewDecision).toBeInstanceOf(Function);
  const unsupported = { ...item, id: 'onboarding-workflow', kind: 'unsupported' as const };
  expect(review.validReviewDecision(unsupported, { disposition: 'ManualProcess', comment: 'HR maintains the checklist.', owner: 'HR Officer' }).valid).toBe(true);
  expect(review.blockingReviewItems([unsupported], { [unsupported.id]: { disposition: 'ManualProcess', comment: 'Done', owner: 'HR Officer' } }, { target: 'profile', field: 'probationMonths' })).toHaveLength(1);
});

test('source-version changes make a prior decision inapplicable while its history remains available', () => {
  expect(review.currentReviewDecisions).toBeInstanceOf(Function);
  const old = { ...source, sourceHash: 'version-a', companyId: 'company', id: 'decision-1', itemKey: item.id, itemType: 'issue' as const, summary: item.summary, disposition: 'NeedsClarification' as const, comment: 'Await owner response.', owner: 'HR Director', reviewedByUserId: 'reviewer', reviewedAtUtc: '2026-10-13T10:00:00Z' };
  expect(review.currentReviewDecisions([old], [{ documentId: 'policy-3', contentSha256: 'version-b', name: 'Revised Policy 3' }], 'company')).toEqual({});
  expect([old]).toHaveLength(1);
});

test('repeated ungrounded missing notes consolidate without becoming blocking policy requirements', () => {
  expect(review.deduplicateCoverageNotes).toBeInstanceOf(Function);
  const notes = [{ section: 'Benefits', kind: 'missing', message: 'Benefit grades not stated.' }, { section: 'Benefits', kind: 'missing', message: ' Benefit grades not stated. ' }];
  expect(review.deduplicateCoverageNotes(notes)).toEqual([{ section: 'Benefits', kind: 'missing', message: 'Benefit grades not stated.', occurrences: 2 }]);
});

test('a changed proposed value cannot reuse the review key of an earlier value with the same passage', async () => {
  const low = await review.reviewItemKey('company', item, { probationMonths: 2 });
  const high = await review.reviewItemKey('company', item, { probationMonths: 3 });
  expect(high).not.toBe(low);
});
