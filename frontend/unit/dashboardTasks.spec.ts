import { test, expect } from '@playwright/test';
import type { DashboardFull } from '../src/api/dashboard';
import { buildTasks } from '../src/components/dashboard/taskList';
import { LOCALE_DICTS } from '../src/i18n/translations';

const NOW = Date.parse('2026-10-26T09:00:00Z');
const data = (over: { pending?: number; queue?: { id: string; dueAtUtc?: string | null }[]; expired?: number; expiring?: number }) => ({
  overview: { pendingApprovals: over.pending ?? 0, approvalQueue: (over.queue ?? []).map(q => ({ title: 't', module: 'Leave', createdAtUtc: '2026-10-01T00:00:00Z', ...q })) },
  kpis: { expiredDocuments: over.expired ?? 0, expiringDocuments: over.expiring ?? 0 },
}) as unknown as DashboardFull;

test.describe('dashboard task list', () => {
  test('empty when nothing needs doing, and when there is no data yet', () => {
    expect(buildTasks({ data: data({}), payrollValidationErrors: 0, now: NOW })).toEqual([]);
    expect(buildTasks({ data: null, payrollValidationErrors: null, now: NOW })).toEqual([]);
  });
  test('critical first: overdue approvals, payroll errors and expired documents come before routine reviews', () => {
    const tasks = buildTasks({
      data: data({ pending: 4, queue: [{ id: 'a', dueAtUtc: '2026-10-20T00:00:00Z' }, { id: 'b', dueAtUtc: '2026-11-20T00:00:00Z' }, { id: 'c' }], expired: 2, expiring: 5 }),
      payrollValidationErrors: 7, now: NOW,
    });
    expect(tasks.map(t => t.id)).toEqual(['payroll-validation', 'docs-expired', 'approvals-overdue', 'docs-expiring', 'approvals']);
    expect(tasks.find(t => t.id === 'approvals-overdue')?.count).toBe(1);
    expect(tasks.find(t => t.id === 'approvals')?.count).toBe(4);
  });
  test('a payroll check that failed or was not asked for adds no task and is never read as zero errors', () => {
    expect(buildTasks({ data: data({ pending: 1 }), payrollValidationErrors: null, now: NOW }).map(t => t.id)).toEqual(['approvals']);
  });
  test('every task string has an Arabic translation', () => {
    const tasks = buildTasks({ data: data({ pending: 4, queue: [{ id: 'a', dueAtUtc: '2026-10-20T00:00:00Z' }], expired: 2, expiring: 5 }), payrollValidationErrors: 7, now: NOW });
    for (const t of tasks) for (const k of [t.title, t.detail, t.cta]) {
      expect(LOCALE_DICTS.ar[k], k).toMatch(/[؀-ۿ]/);
      expect(LOCALE_DICTS.ar[k], k).not.toBe(LOCALE_DICTS.en[k]);
    }
  });
});
