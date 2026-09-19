import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  ATTENDANCE_DOMAINS,
  attendanceErrorSummary,
  regularizationQueueUnavailableMessage,
  unavailableMessage,
} from '../src/lib/attendanceLoadState';
import { LOGIN_CAPABILITIES, LOGIN_PREVIEW_DISCLOSURE } from '../src/lib/loginCapabilities';
import { payrollInsightEmptyCopy, payrollInsightState, payrollPeriodState } from '../src/lib/payrollInsightState';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

test.describe('browserless UI truthfulness contracts', () => {
  test('attendance models every independent data source and names partial failure', () => {
    expect(ATTENDANCE_DOMAINS).toHaveLength(10);
    const errors = { daily: 'failed', deviceSync: 'failed' } as const;
    expect(attendanceErrorSummary(errors)).toContain('2 attendance data sources unavailable');
    expect(attendanceErrorSummary(errors)).toContain('daily attendance');
    expect(unavailableMessage('deviceSync', errors)).toContain('device health unavailable');
    expect(regularizationQueueUnavailableMessage({ pendingRegularizations: 'failed' }, 0)).toContain('pending correction approvals unavailable');
    expect(regularizationQueueUnavailableMessage({ regularizations: 'failed' }, 0)).toContain('your correction requests unavailable');
    expect(regularizationQueueUnavailableMessage({ regularizations: 'failed' }, 2)).toBeNull();
  });

  test('attendance failure rendering never falls through to zero or empty-success states', () => {
    const attendance = read('src/views/AttendancePage.tsx');

    expect(attendance).toContain("dashboardUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("deviceSyncUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("devicesUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("dailyUnavailable ? 'Unavailable' : minutes(totalWorked)");
    expect(attendance).toContain("dashboardUnavailable ? 'Unavailable' : (summary?.overtimeEmployees ?? 0)");
    expect(attendance).toContain("action={insightsUnavailable ? 'Unavailable' : `${insights.length} open`}");
    expect(attendance).toContain("action={rawUnavailable ? 'Unavailable' : `${rawEvents.length} latest`}");
    expect(attendance).toContain("action={payrollSummaryUnavailable ? 'Unavailable' : `${payrollSummary.length} employees`}");
    expect(attendance).toContain("action={insightsUnavailable ? 'Unavailable' : `${insights.length} signals`}");
    expect(attendance).toContain("correctionQueueUnavailable");
  });

  test('payroll never equates empty or failed insight data with healthy payroll', () => {
    expect(payrollInsightState(false, true, 0)).toBe('unavailable');
    expect(payrollInsightEmptyCopy('unavailable')).toContain('does not prove payroll is clear');
    expect(payrollInsightEmptyCopy('empty')).toContain('Complete payroll validation');
    expect(payrollPeriodState(false, true)).toBe('no-run');
    expect(payrollPeriodState(true, true)).toBe('has-run');
    expect(payrollPeriodState(false, false)).toBe('unavailable');
    const payrollPage = read('src/views/PayrollPage.tsx');
    expect(payrollPage).not.toContain('all payroll and HR signals look normal');
    expect(payrollPage).toContain('Empty totals do not indicate a completed or healthy payroll');
  });

  test('pre-auth preview and capability claims are qualified', () => {
    expect(LOGIN_PREVIEW_DISCLOSURE).toContain('Illustrative sample data');
    expect(LOGIN_CAPABILITIES.find((item) => item.includes('Qiwa'))).toContain('integration-ready');
    expect(LOGIN_CAPABILITIES.find((item) => item.includes('Hijri'))).toContain('aware');
    const login = read('src/views/LoginPage.tsx');
    expect(login).toContain('overflow-x-hidden');
    expect(login).toContain('lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]');
    expect(login).toContain('min-w-0 flex-col items-center [justify-content:safe_center]');
  });

  test('public privacy and security claims stay evidence-bound', () => {
    const privacy = read('app/privacy/page.tsx');
    const security = read('app/security/page.tsx');

    expect(privacy).not.toContain('AES-256 encryption at rest for all sensitive fields');
    expect(privacy).not.toContain('Customers can request that their data be stored');
    expect(privacy).not.toContain('Regular penetration testing and vulnerability assessments');
    expect(privacy).toContain('must be confirmed in the applicable customer agreement');
    expect(privacy).toContain('does not claim ISO 27001 or SOC 2 Type II certification');

    expect(security).not.toContain('Every table that holds tenant-owned data');
    expect(security).not.toContain('TLS 1.2 minimum enforced');
    expect(security).not.toContain('Every push to main triggers a Docker build and deploy');
    expect(security).toContain('Selected PostgreSQL integration suites use Testcontainers when Docker is available');
    expect(security).toContain('Render auto-deploy is disabled');
  });

  test('critical surfaces carry semantic and server-truth guards', () => {
    const login = read('src/views/LoginPage.tsx');
    const people = read('src/views/EmployeesPage.tsx');
    const approvals = read('src/views/ApprovalsPage.tsx');
    const notifications = read('src/layouts/TopBar.tsx');
    const tabs = read('src/components/ui/RovingTabs.tsx');

    expect(login).toContain('aria-busy={loading}');
    expect(login).toContain('aria-pressed={showPw}');
    expect(people).toContain('aria-label={`Open profile for ${employee.fullName}`}');
    expect(people).toContain('Clear filters');
    expect(approvals).toContain('aria-pressed={queueFilter === key}');
    expect(approvals).not.toContain("alert('Please add a clear rejection reason");
    expect(notifications).toContain('No local status was changed');
    expect(notifications).toContain('group-focus-within:opacity-100');
    expect(approvals).toContain('<div role="alert" aria-live="assertive"');
    expect(tabs).toContain('role="tablist"');
    expect(tabs).toContain("event.key === 'Home'");
    expect(tabs).toContain('role="tabpanel"');
  });
});
