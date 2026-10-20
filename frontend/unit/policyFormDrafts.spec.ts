import { expect, test } from '@playwright/test';
import * as drafts from '../src/lib/policyFormDrafts';

const task = { id: 'orientation', taskTitle: 'Orientation briefing', taskDescription: 'Explain the policy.', category: 'Training', assignedToName: null, dueOffsetDays: null, isMandatory: null, condition: 'For new joiners', sourceQuote: 'New joiners receive an orientation briefing.', sourceStart: 20, sourceLength: 43, documentId: 'doc1', contentSha256: 'hash1', sourceName: 'Policy 3' };
const bundle = { tenantId: 'tenant1', userId: 'user1', companyId: 'company1', companyName: 'Evostel', accountType: 'SingleCompany', sources: [{ documentId: 'doc1', contentSha256: 'hash1', name: 'Policy 3' }], tasks: [task] };
const scope = { tenantId: 'tenant1', userId: 'user1', companyId: 'company1' };
const storage = () => { const values = new Map<string, string>(); return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } }; };

test('policy form handoff cannot be read by another tenant, user or company', () => {
  expect(drafts).toHaveProperty('savePolicyFormDraft');
  const store = storage(); drafts.savePolicyFormDraft(store, bundle);
  expect(drafts.readPolicyFormDraft(store, scope)?.tasks[0].taskTitle).toBe('Orientation briefing');
  for (const changed of [{ tenantId: 'tenant2' }, { userId: 'user2' }, { companyId: 'company2' }]) expect(drafts.readPolicyFormDraft(store, { ...scope, ...changed })).toBeNull();
  drafts.clearPolicyFormDraft(store, scope);
  expect(drafts.readPolicyFormDraft(store, scope)).toBeNull();
});

test('repeated handoff preserves edits and changed source requires explicit replacement', () => {
  expect(drafts).toHaveProperty('savePolicyFormDraft');
  const store = storage();
  const edited = { ...bundle, edits: { orientation: { ...drafts.policyTaskForm(task, 0), taskTitle: 'Edited by HR' } } };
  drafts.savePolicyFormDraft(store, edited);
  expect(drafts.savePolicyFormDraft(store, bundle).edits.orientation.taskTitle).toBe('Edited by HR');
  const revised = { ...bundle, sources: [{ ...bundle.sources[0], contentSha256: 'hash2' }], tasks: [{ ...task, contentSha256: 'hash2' }] };
  expect(() => drafts.savePolicyFormDraft(store, revised)).toThrow(/unsaved edits/i);
  expect(drafts.savePolicyFormDraft(store, revised, { replace: true }).edits).toEqual({});
});

test('unresolved deadline and mandatory flag stay unanswered rather than becoming zero or required', () => {
  expect(drafts).toHaveProperty('policyTaskForm');
  const form = drafts.policyTaskForm(task, 0);
  expect(form.dueOffsetDays).toBe(''); expect(form.isMandatory).toBeNull(); expect(form.assignedToName).toBe('');
  expect(drafts.policyTaskProblem(form, task, false)).toMatch(/deadline/i);
  expect(drafts.policyTaskProblem({ ...form, dueOffsetDays: 0 }, task, false)).toMatch(/mandatory/i);
  expect(drafts.policyTaskProblem({ ...form, dueOffsetDays: 0, isMandatory: false }, task, false)).toMatch(/condition/i);
  expect(drafts.policyTaskProblem({ ...form, dueOffsetDays: 0, isMandatory: false, applicabilityConfirmed: true }, task, false)).toBeNull();
});

test('silence in policy preserves existing owner and deadline while title matches require update confirmation', () => {
  expect(drafts).toHaveProperty('policyTaskForm');
  const form = drafts.policyTaskForm(task, 0, { assignedToName: 'Existing HR owner', dueOffsetDays: 5, isMandatory: true, orderIndex: 3 });
  expect(form.assignedToName).toBe('Existing HR owner'); expect(form.dueOffsetDays).toBe(5); expect(form.orderIndex).toBe(3); expect(form.isMandatory).toBe(true);
  expect(drafts.policyTaskProblem({ ...form, applicabilityConfirmed: true }, task, true)).toMatch(/existing task/i);
  expect(drafts.policyTaskProblem({ ...form, applicabilityConfirmed: true, overwriteConfirmed: true }, task, true)).toBeNull();
});

test('conditional policy text survives the ordinary task description without claiming automated enforcement', () => {
  expect(drafts.policyTaskForm(task, 0).taskDescription).toContain('For new joiners');
  expect(drafts).toHaveProperty('policyTaskDescription');
  expect(drafts.policyTaskDescription('Edited duty', 'Only for expatriates')).toBe('Edited duty\n\nPolicy condition: Only for expatriates');
  expect(drafts.policyTaskDescription('Only for expatriates', 'Only for expatriates')).toBe('Only for expatriates');
});

test('malformed source provenance is rejected and storage errors are not claimed as saved', () => {
  expect(drafts).toHaveProperty('savePolicyFormDraft');
  expect(() => drafts.savePolicyFormDraft(storage(), { ...bundle, tasks: [{ ...task, contentSha256: 'other' }] })).toThrow(/source/i);
  expect(() => drafts.savePolicyFormDraft({ ...storage(), setItem: () => { throw new Error('quota'); } }, bundle)).toThrow('quota');
});

test('failed browser storage retains latest in-memory edits for switching tasks without claiming persistence', () => {
  expect(drafts).toHaveProperty('retainPolicyFormDraft');
  const edited = { ...bundle, edits: { orientation: { ...drafts.policyTaskForm(task, 0), taskTitle: 'Unsaved HR correction' } } };
  const result = drafts.retainPolicyFormDraft({ ...storage(), setItem: () => { throw new Error('quota'); } }, edited);
  expect(result.bundle.edits?.orientation.taskTitle).toBe('Unsaved HR correction');
  expect(result.persisted).toBe(false);
  expect(result.error).toMatch(/page open/i);
});
