import { expect, test } from '@playwright/test';
import { blankTask, moveTask, previewTasks, validateTasks } from '../src/lib/onboardingChecklist';
import type { ChecklistTask } from '../src/api/onboardingTemplates';

const task = (title: string, offset = 0, cls: ChecklistTask['condition']['nationalityClass'] = 'any', blocks = false): ChecklistTask =>
  ({ ...blankTask(), title, dueOffsetDays: offset, condition: { nationalityClass: cls }, blocksActivation: blocks });

test('preview: a Saudi hire gets no expat task, an expat does, an unknown nationality only the tasks for everyone', () => {
  const tasks = [task('Mailbox', -1), task('Iqama', 30, 'expat'), task('GOSI', 0)];
  expect(previewTasks(tasks, 'saudi', '2026-11-08').map(r => r.title)).toEqual(['Mailbox', 'GOSI']);
  expect(previewTasks(tasks, 'expat', '2026-11-08').map(r => r.title)).toEqual(['Mailbox', 'Iqama', 'GOSI']);
  expect(previewTasks(tasks, 'unknown', '2026-11-08').map(r => r.title)).toEqual(['Mailbox', 'GOSI']);
});

test('preview: dues are the start date plus the offset; no start date means no due date yet', () => {
  const rows = previewTasks([task('A', -7), task('B', 5)], 'saudi', '2026-11-08');
  expect(rows.map(r => r.dueDate)).toEqual(['2026-11-01', '2026-11-13']);
  expect(previewTasks([task('A', 1)], 'saudi', '').map(r => r.dueDate)).toEqual([null]);
});

test('moveTask swaps neighbours and ignores moves off either end', () => {
  expect(moveTask(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c']);
  expect(moveTask(['a', 'b', 'c'], 1, 1)).toEqual(['a', 'c', 'b']);
  expect(moveTask(['a', 'b', 'c'], 0, -1)).toEqual(['a', 'b', 'c']);
  expect(moveTask(['a', 'b', 'c'], 2, 1)).toEqual(['a', 'b', 'c']);
});

test('validateTasks flags a blank title, a repeated title and an offset out of range', () => {
  expect(validateTasks([task('A'), task('A'), task(' '), task('B', 999)]).map(i => `${i.index}:${i.field}`)).toEqual(['1:duplicate', '2:title', '3:dueOffsetDays']);
  expect(validateTasks([task('A', -365), task('B', 365)])).toEqual([]);
});

test('a new task never blocks activation by default (CD4)', () => {
  expect(blankTask().blocksActivation).toBe(false);
});
