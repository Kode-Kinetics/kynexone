import { expect, test } from '@playwright/test';
import { appendPolicyProposals, mergePolicyValue, policySources, proposalKey, proposalsConflict, type SourcedPolicyProposal } from '../src/lib/policyBinder';
import { normalizeDraft } from '../src/api/setupAssistant';
import * as binder from '../src/lib/policyBinder';

test('task draft chunks are idempotent and retain distinct source versions', () => {
  expect(binder).toHaveProperty('appendOnboardingTaskDrafts');
  const row = { id: 'task', taskTitle: 'Issue ID card', taskDescription: 'Issue ID card', category: 'Access', assignedToName: null, dueOffsetDays: null, isMandatory: null, condition: null, sourceQuote: 'Issue ID card', sourceStart: 0, sourceLength: 13, documentId: 'one', contentSha256: 'v1', sourceName: 'Policy 3' };
  const rows = binder.appendOnboardingTaskDrafts([row], [row, { ...row, id: 'task2', taskTitle: 'System access' }, { ...row, contentSha256: 'v2' }]);
  expect(rows).toHaveLength(3);
  expect(rows[0].dueOffsetDays).toBeNull();
  expect(rows[2].contentSha256).toBe('v2');
});

const proposal = (value: unknown, documentId = 'one'): SourcedPolicyProposal => ({ target: 'configuration', field: 'leavePolicies', value, documentId, contentSha256: 'hash', sourceName: `${documentId}.pdf`, sourceStart: 10, sourceLength: 25, sourceQuote: 'Annual leave is 22 working days.' });

test('combines chapter collections without erasing previously accepted and grade-scoped policies', () => {
  const existing = [{ leaveTypeCode: 'ANNUAL', gradeCode: 'G1', annualEntitlementDays: 22 }, { leaveTypeCode: 'SICK', annualEntitlementDays: 30 }];
  const merged = mergePolicyValue('leavePolicies', existing, [{ leaveTypeCode: 'ANNUAL', gradeCode: 'G2', annualEntitlementDays: 26 }]);
  expect(merged).toEqual([...existing, { leaveTypeCode: 'ANNUAL', gradeCode: 'G2', annualEntitlementDays: 26 }]);
  expect(mergePolicyValue('leavePolicies', merged, [{ leaveTypeCode: 'ANNUAL', gradeCode: 'G1', annualEntitlementDays: 24 }])).toEqual([{ leaveTypeCode: 'ANNUAL', gradeCode: 'G1', annualEntitlementDays: 24 }, existing[1], { leaveTypeCode: 'ANNUAL', gradeCode: 'G2', annualEntitlementDays: 26 }]);
  expect(existing[0].annualEntitlementDays).toBe(22);
});

test('deduplicates repeated chunk delivery but keeps distinct supporting passages and contradictory rules', () => {
  const first = proposal([{ leaveTypeCode: 'ANNUAL', annualEntitlementDays: 22 }, { leaveTypeCode: 'SICK', annualEntitlementDays: 30 }]);
  const rows = appendPolicyProposals([], [first, first, proposal([{ annualEntitlementDays: 22, leaveTypeCode: 'ANNUAL' }], 'two'), proposal([{ leaveTypeCode: 'ANNUAL', annualEntitlementDays: 26 }], 'three')]);
  expect(rows).toHaveLength(4);
  expect(proposalKey(rows[0])).toBe(proposalKey(rows[3]));
  expect(proposalKey(rows[0])).not.toBe(proposalKey(rows[1]));
  expect(proposalsConflict(rows[0], rows[2])).toBe(false);
  expect(proposalsConflict(rows[0], rows[3])).toBe(true);
});

test('manual completion survives equality with an extracted suggestion, including its distinct source', () => {
  const original = proposal([{ leaveTypeCode: 'ANNUAL', annualEntitlementDays: 22 }]);
  const completed = { ...original, sourceStart: 100, userCompleted: true };
  const rows = appendPolicyProposals([original], [completed]);
  expect(rows).toHaveLength(2);
  expect(rows[1]).toMatchObject({ sourceStart: 100, userCompleted: true });
  const samePassage = appendPolicyProposals([original], [{ ...original, userCompleted: true }]);
  expect(samePassage).toHaveLength(1);
  expect(samePassage[0].userCompleted).toBe(true);
  expect(appendPolicyProposals(samePassage, [original])[0].userCompleted).toBe(true);
});

test('leave identity normalizes code case and surrounding whitespace for every scope', () => {
  const first = proposal([{ leaveTypeCode: 'ANNUAL', gradeCode: 'G1', departmentCode: 'HR', employmentType: 'FullTime', annualEntitlementDays: 22 }]);
  const revised = proposal([{ leaveTypeCode: ' annual ', gradeCode: 'g1', departmentCode: ' hr ', employmentType: 'fulltime', annualEntitlementDays: 26 }]);
  expect(proposalsConflict(first, revised)).toBe(true);
  expect(mergePolicyValue('leavePolicies', first.value, revised.value)).toEqual(revised.value);
});

test('merges primitive settings without duplicates and retains unproposed object fields', () => {
  expect(mergePolicyValue('attendanceMethods', ['WebCheckIn'], ['BiometricDevice', 'WebCheckIn'])).toEqual(['WebCheckIn', 'BiometricDevice']);
  expect(mergePolicyValue('attendancePolicy', { code: 'ATT', graceMinutes: 5 }, { graceMinutes: 10 })).toEqual({ code: 'ATT', graceMinutes: 10 });
});

test('keeps legacy sources compatible and respects an explicitly emptied binder', () => {
  expect(policySources({ policyDocumentId: 'one', policySourceHash: 'hash' })).toEqual([{ documentId: 'one', contentSha256: 'hash', name: 'Policy document' }]);
  expect(policySources({ policySources: [], policyDocumentId: 'one', policySourceHash: 'hash' })).toEqual([]);
});

test('no overtime conflicts with paid or compensatory modes and a reviewed choice removes the incompatible mode', () => {
  const paid = { ...proposal(['PaidOvertime']), field: 'overtimeModes' };
  const comp = { ...proposal(['CompensatoryOff']), field: 'overtimeModes' };
  const none = { ...proposal(['NotApplicable']), field: 'overtimeModes' };
  expect(proposalsConflict(paid, comp)).toBe(false);
  expect(proposalsConflict(paid, none)).toBe(true);
  expect(proposalsConflict(none, comp)).toBe(true);
  expect(mergePolicyValue('overtimeModes', ['PaidOvertime', 'CompensatoryOff'], ['NotApplicable'])).toEqual(['NotApplicable']);
  expect(mergePolicyValue('overtimeModes', ['NotApplicable'], ['PaidOvertime'])).toEqual(['PaidOvertime']);
});

test('preserves all document and field provenance through draft normalization for apply', () => {
  const provenance = { policySource: { documentId: 'one', contentSha256: 'hash' }, policySources: [{ documentId: 'one', contentSha256: 'hash', name: 'Binder.pdf' }], policyFieldSources: [{ documentId: 'one', contentSha256: 'hash', target: 'configuration', field: 'leavePolicies', sourceStart: 10, sourceLength: 25 }] };
  expect(normalizeDraft(provenance)).toMatchObject(provenance);
});
