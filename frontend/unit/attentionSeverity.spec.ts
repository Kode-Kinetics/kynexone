import { expect, test } from '@playwright/test';
import { buildAttention, attentionSeverityLabel } from '../src/components/dashboard/dashboardModel';
import type { DashboardFull } from '../src/api/dashboard';
import type { AIInsight } from '../src/api/intelligence';
import { translate, type MessageParams } from '../src/i18n/translations';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);

const data = (kpis: Record<string, unknown>) => ({
  overview: { complianceCriticalTotal: 0, alerts: [], approvalQueue: [], pendingApprovals: 0 },
  kpis: { pendingLeaveRequests: 0, pendingAttendanceCorrections: 0, attendanceExceptions: 0, expiringDocuments: 0, expiredDocuments: 0, missingDocuments: 0, qiwaEnabled: false, missingSalaryAssignments: 0, missingBankDetails: 0, ...kpis },
}) as unknown as DashboardFull;

const variance = {
  id: 'v1', tenantId: 't', module: 'Payroll', insightType: 'PayrollVariance', severity: 'Critical', employeeId: null, employeeName: '',
  title: 'Net payroll increased by 40.6%', summary: 'Net payroll for November 2026 is increased by 40.6% (1,000) vs. 3-month average (700).', dataJson: '{}', generatedBy: 'rules',
  isAcknowledged: false, createdAtUtc: '2026-11-13T17:42:41Z',
} as unknown as AIInsight;

test.describe('home attention severity', () => {
  test('missing documents need action; they are not shown as Critical', () => {
    const [item] = buildAttention(data({ missingDocuments: 9 }), []);
    expect(item.id).toBe('docs-missing');
    expect(item.severity).toBe('action');
    expect(attentionSeverityLabel(en, item.severity)).toBe('Action needed');
    expect(attentionSeverityLabel(ar, item.severity)).toMatch(/[؀-ۿ]/);
  });

  test('a payroll variance finding is informational, however the engine rated it', () => {
    const [item] = buildAttention(data({}), [variance]);
    expect(item.severity).toBe('notice');
    expect(attentionSeverityLabel(en, item.severity)).toBe('For review');
  });

  test('expired records stay critical and sort first', () => {
    const items = buildAttention(data({ missingDocuments: 9, expiredDocuments: 2 }), [variance]);
    expect(items.map((i) => i.severity)).toEqual(['critical', 'action', 'notice']);
    expect(attentionSeverityLabel(en, 'critical')).toBe('Critical');
  });
});
