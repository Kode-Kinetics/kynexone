import type { ChecklistTask, NationalityCondition } from '../api/onboardingTemplates';
import { addDays } from './hire/hireTemplate';

export type PreviewClass = 'saudi' | 'gcc' | 'expat' | 'unknown';
export const PREVIEW_CLASSES: readonly PreviewClass[] = ['saudi', 'gcc', 'expat', 'unknown'];

export interface PreviewRow { title: string; titleAr?: string | null; ownerRole: ChecklistTask['ownerRole']; dueDate: string | null; blocksActivation: boolean }

/** Which tasks a hire of this class gets and when each is due (joining date + offset). Mirrors the server's planner so the editor can preview unsaved edits. */
export function previewTasks(tasks: readonly ChecklistTask[], cls: PreviewClass, startDate: string): PreviewRow[] {
  const applies = (c: NationalityCondition) => c === 'any' || c === cls;
  return tasks.filter(t => applies(t.condition.nationalityClass)).map(t => ({
    title: t.title, titleAr: t.titleAr, ownerRole: t.ownerRole, blocksActivation: t.blocksActivation,
    dueDate: startDate ? addDays(startDate, t.dueOffsetDays) : null,
  }));
}

/** Reordering is buttons, not drag only (WCAG 2.5.7). Returns a new list; an out-of-range move changes nothing. */
export function moveTask<T>(rows: readonly T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta;
  if (index < 0 || index >= rows.length || target < 0 || target >= rows.length) return [...rows];
  const next = [...rows];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

export function blankTask(): ChecklistTask {
  return { title: '', titleAr: '', description: '', descriptionAr: '', ownerRole: 'HR', dueOffsetDays: 0, blocksActivation: false, condition: { nationalityClass: 'any' }, linkSection: '', isStatutory: false, evidenceRequired: false };
}

export interface TaskIssue { index: number; field: 'title' | 'dueOffsetDays' | 'duplicate' }

/** Client-side mirror of the server's task rules, so an error shows before the request. */
export function validateTasks(tasks: readonly ChecklistTask[]): TaskIssue[] {
  const issues: TaskIssue[] = [];
  const seen = new Set<string>();
  tasks.forEach((t, index) => {
    const title = t.title.trim();
    if (!title || title.length > 200) issues.push({ index, field: 'title' });
    else if (seen.has(title)) issues.push({ index, field: 'duplicate' });
    else seen.add(title);
    if (!Number.isInteger(t.dueOffsetDays) || t.dueOffsetDays < -365 || t.dueOffsetDays > 365) issues.push({ index, field: 'dueOffsetDays' });
  });
  return issues;
}
