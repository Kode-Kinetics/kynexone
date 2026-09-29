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
    /* These three used to pin Tailwind utilities on the old panel-grid
       sign-in page: overflow-x-hidden, an lg: two-column grid, and a
       safe-centred column. The V3 rebuild moved that layout out of utility
       classes and into src/styles/login-aurora.css, so the assertions were
       checking for strings in a file that no longer has any business
       containing them — and the suite has been red ever since.
       Same three guarantees, asserted where they now live. */
    const shellCss = read('src/styles/login-aurora.css');
    // 1. no horizontal scroll on the sign-in shell
    expect(shellCss).toContain('overflow-x: hidden');
    // 2. the stage is still two columns: brief + the card's own track
    // No `s` flag: [^}] already spans newlines, and the flag needs an es2018 target.
    expect(shellCss).toMatch(/\.lx-stage\b[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
    // 3. and it collapses to one column rather than overflowing when narrow
    expect(shellCss).toContain('grid-template-columns: minmax(0, 1fr);');
  });

  test('public privacy and security claims stay evidence-bound', () => {
    const privacy = read('app/privacy/page.tsx');
    const security = read('app/security/page.tsx');

    expect(privacy).not.toContain('AES-256 encryption at rest for all sensitive fields');
    expect(privacy).not.toContain('Customers can request that their data be stored');
    expect(privacy).not.toContain('Regular penetration testing and vulnerability assessments');
    expect(privacy).toContain('must be confirmed in the applicable customer agreement');
    expect(privacy).toContain('does not claim ISO 27001 or SOC 2 Type II certification');

    /* 2026-09-21 rewrite (scratchpad/privacy-policy.md). Each of these was a published promise the
       product did not keep: the retention sweep that would anonymise is switched off and the delete
       path stamps seven years, not 90 days; no legal-hold mechanism exists; no "Privacy & Cookies"
       screen exists and the app sets no cookies; bank details are permission-gated, not masked;
       access tokens last 30 minutes, not 24 hours. They stay out until the code makes them true. */
    expect(privacy).not.toContain('Anonymised within 90 days');
    expect(privacy.toLowerCase()).not.toContain('legal hold');
    expect(privacy).not.toContain('Privacy &amp; Cookies');
    expect(privacy).not.toContain('masked in user-facing responses');
    expect(privacy).not.toContain('Session tokens expire within 24 hours');
    expect(privacy).not.toContain('explicit consent is collected');
    // The disclosures a Saudi customer's counsel will look for, pinned so they cannot be softened away.
    expect(privacy).toContain('does not automatically delete or anonymise personal data');
    expect(privacy).toContain('None of the platform&apos;s data is stored in the Kingdom of Saudi Arabia');
    expect(privacy).toContain('Backblaze B2 in a United States region');
    /* This pin is a tripwire, not trivia: the page names the model that receives employee data,
       so it must track Render's AI_MODEL. It fired on 2026-09-23 when AI_MODEL was confirmed as
       deepseek-v4-pro:cloud while the page still said gpt-oss:120b -- the page had been wrong
       since the model was last switched. Update BOTH, or the disclosure goes stale again. */
    expect(privacy).toContain('deepseek-v4-pro:cloud');
    expect(privacy).toContain('does not remove or disguise personal data before sending it to the model');

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

    /* aria-busy={busy}, not aria-busy={loading}: the rebuild pulled the
       submit button out into a <Submit> part that takes the flag as `busy`
       (LoginPage still assigns `const busy = loading`). The guarantee — the
       submit control reports its pending state to assistive tech — is
       unchanged, so the assertion follows the rename rather than the page
       being reverted to satisfy a string match. */
    expect(login).toContain('const busy = loading');
    expect(login).toContain('aria-busy={busy}');
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
