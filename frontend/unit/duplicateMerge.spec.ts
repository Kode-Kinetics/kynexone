import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { MERGE_NEEDS_DELETE_REASON, mergeRefusal } from '../src/lib/duplicateMerge';
import { translate, type MessageParams } from '../src/i18n/translations';

/**
 * Duplicate merge needs `employees.delete` (the backend refuses it otherwise, because a merge removes the duplicate
 * record). The screen must not offer a button that will be refused, must say why, and a 403 on the call must show the
 * same reason. Source-scan tests read EmployeesPage.tsx as text (same approach as peopleLinks.spec.ts).
 */
const page = fs.readFileSync(path.resolve(__dirname, '..', 'src/views/EmployeesPage.tsx'), 'utf8');
const en = (key: string, params?: MessageParams) => translate('en', key, params);
const ar = (key: string, params?: MessageParams) => translate('ar', key, params);
const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });

const REASON_EN = 'Merging removes the duplicate record, so it needs the Delete employees permission.';

test('the permission reason is the owner-approved sentence, with an Arabic translation', () => {
  expect(MERGE_NEEDS_DELETE_REASON).toBe(REASON_EN);
  expect(en(MERGE_NEEDS_DELETE_REASON)).toBe(REASON_EN);
  const arabic = ar(MERGE_NEEDS_DELETE_REASON);
  expect(arabic).not.toBe(REASON_EN);
  expect(arabic).toMatch(/[؀-ۿ]/);
});

test('a 403 on merge with an EMPTY body shows the permission reason, never the generic merge failure', () => {
  for (const body of [undefined, '', {}]) {
    const message = mergeRefusal(http(403, body), (k, p) => en(k, p), 'en');
    expect(message).toContain(REASON_EN);
    expect(message).not.toContain('Could not merge. Try again.');
  }
});

test('a 403 on merge shows the permission reason in Arabic', () => {
  const message = mergeRefusal(http(403, undefined), (k, p) => ar(k, p), 'ar');
  expect(message).toContain(ar(MERGE_NEEDS_DELETE_REASON));
  expect(message).not.toContain('Could not merge');
});

test('a 403 that carries the same permission sentence is not doubled', () => {
  const message = mergeRefusal(http(403, { code: 'forbidden', message: REASON_EN }), (k, p) => en(k, p), 'en');
  expect(message.split(REASON_EN).length - 1).toBe(1);
});

test('other failures keep the server sentence; an unexplained one is a refusal, not a bare string', () => {
  expect(mergeRefusal(http(409, { code: 'conflict', message: 'That record is already merged.' }), (k, p) => en(k, p), 'en'))
    .toContain('That record is already merged.');
  const unreachable = mergeRefusal({ isAxiosError: true }, (k, p) => en(k, p), 'en');
  expect(unreachable).not.toBe('Could not merge. Try again.');
  expect(unreachable.length).toBeGreaterThan(10);
});

test('mergeInto is guarded by the permission and uses mergeRefusal, not the raw server message', () => {
  const body = page.slice(page.indexOf('const mergeInto = async'), page.indexOf('const hasCurrentEditValue'));
  expect(body).toContain('canMergeDuplicates');
  expect(body).toContain('mergeRefusal(');
  expect(body).not.toContain("'Could not merge. Try again.'");
});

test("canMergeDuplicates is hasPermission('employees.delete')", () => {
  expect(page).toMatch(/const canMergeDuplicates = hasPermission\('employees\.delete'\)/);
});

test('the create dialog Merge only opens the existing record, so it stays open to edit users (no delete gate)', () => {
  const fn = page.slice(page.indexOf('const mergeFromCreate = '), page.indexOf('const saveEdit = async'));
  expect(fn).not.toContain('canMergeDuplicates');
  expect(fn).not.toContain('resolveDuplicate');
  const start = page.indexOf('onClick={() => mergeFromCreate(m.employeeId)}');
  expect(start).toBeGreaterThan(0);
  const around = page.slice(start - 300, start + 300);
  expect(around).not.toContain('canMergeDuplicates');
  expect(around).not.toContain('merge-needs-delete');
  expect(around).toContain("{t('Merge')}");
  expect(page).toContain("{t('View existing')}");
});

test('Resigned stays in the ACTIVE list filter; only Terminated and Retired are dropped', () => {
  const statusOptions = /const statusOptions: StatusFilter\[\] = \[([^\]]*)\]/.exec(page);
  const filter = /const activeStatusFilterOptions: StatusFilter\[\] = statusOptions\.filter\(\(s\) => ([^)]*)\);/.exec(page);
  expect(statusOptions && filter).toBeTruthy();
  const all = [...statusOptions![1].matchAll(/'([^']*)'/g)].map((m) => m[1]);
  const dropped = [...filter![1].matchAll(/s !== '([^']*)'/g)].map((m) => m[1]).sort();
  expect(dropped).toEqual(['Retired', 'Terminated']);
  const kept = all.filter((s) => !dropped.includes(s));
  expect(kept).toContain('Resigned');
  expect(kept).not.toContain('Terminated');
  expect(kept).not.toContain('Retired');
});

// The real bodies the backend now sends (EmployeesController.ResolveDuplicate / Unresolve): each carries `error`, `code`,
// an English `message` and an Arabic `messageAr`. The screen shows the viewer's language once, with no doubled sentence.
const BODIES = {
  blocked: {
    error: 'merge_blocked_by_records', code: 'merge_blocked_by_records',
    message: 'This record cannot be merged because it owns records that would be left behind: open loans (2). Move or close them first, or keep this record.',
    messageAr: 'لا يمكن دمج هذا السجل لأنه يملك سجلات ستُترك خلفه: قروض مفتوحة (2). انقلها أو أغلقها أولاً، أو احتفظ بهذا السجل.',
    blockers: [{ type: 'open_loans', label: 'Open loans', labelAr: 'قروض مفتوحة', count: 2 }],
  },
  otherCompany: {
    error: 'merge_across_companies', code: 'merge_across_companies',
    message: 'These two records belong to different companies. A merge only combines duplicate entries of the same person within one company.',
    messageAr: 'السجلان يتبعان شركتين مختلفتين. الدمج يجمع فقط الإدخالات المكررة لنفس الشخص داخل شركة واحدة.',
  },
  needsDelete: {
    error: 'merge_requires_delete_permission', code: 'merge_requires_delete_permission',
    message: REASON_EN, messageAr: 'الدمج يزيل السجل المكرر، لذلك يتطلب صلاحية حذف الموظفين.', requiredPermission: 'employees.delete',
  },
};

test('a 409 refusal shows the server sentence in English and messageAr in Arabic, once', () => {
  for (const [status, body] of [[409, BODIES.blocked], [409, BODIES.otherCompany]] as const) {
    const english = mergeRefusal(http(status, body), (k, p) => en(k, p), 'en');
    expect(english).toContain(body.message);
    expect(english).not.toContain(body.messageAr);
    expect(english.split(body.message).length - 1).toBe(1);
    const arabic = mergeRefusal(http(status, body), (k, p) => ar(k, p), 'ar');
    expect(arabic).toContain(body.messageAr);
    expect(arabic.split(body.messageAr).length - 1).toBe(1);
    expect(arabic).not.toContain(body.message);
  }
});

test('the real 403 body is not doubled in either language', () => {
  const english = mergeRefusal(http(403, BODIES.needsDelete), (k, p) => en(k, p), 'en');
  expect(english.split(REASON_EN).length - 1).toBe(1);
  const arabic = mergeRefusal(http(403, BODIES.needsDelete), (k, p) => ar(k, p), 'ar');
  expect(arabic).toContain(BODIES.needsDelete.messageAr);
  expect(arabic.split(BODIES.needsDelete.messageAr).length - 1).toBe(1);
});
