import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { payslipLineName, payslipMonthLabel } from '../src/lib/essPayslip';
import {
  ESS_LEAVE_PATH, ESS_OVERTIME_PATH, ESS_REQUESTS_PATH,
  canCancelLeave, hrRequestStatus, leaveStatus, overtimeStatus, overtimeWindow, splitMinutes,
} from '../src/lib/essSelfService';
import { essNavItems, essPages } from '../src/routes/essSections';

const root = join(__dirname, '..');
const read = (rel: string) => readFileSync(join(root, rel), 'utf8');

// The self-service "Apply Leave", "OT Request" and "My Requests" buttons went to /leave, /overtime and
// /hr-requests: HR's screens, gated on leave.*, overtime.* and approvals.*, none of which the Employee
// role holds. Every employee who clicked got "Access Denied".
test('self-service buttons open the employee pages, never the HR screens', () => {
  const ess = read('src/views/EmployeeSelfServicePage.tsx');
  expect(ess).not.toMatch(/['"]\/(leave|overtime|hr-requests|payroll)['"]/);
  // Apply Leave button, Leave Balance tile and the balances card's link; OT quick action; requests.
  expect(ess.match(/href=\{ESS_LEAVE_PATH\}/g)?.length).toBe(3);
  expect(ess).toMatch(/href: ESS_OVERTIME_PATH/);
  expect(ess.match(/href=\{ESS_REQUESTS_PATH\}/g)?.length).toBe(2); // hero button, the card's link
  // Each recent request and HR's reply open that request; a missing punch starts a correction request.
  expect(ess).toMatch(/`\$\{ESS_REQUESTS_PATH\}\?open=\$\{encodeURIComponent\(r\.id\)\}`/);
  expect(ess).toMatch(/`\$\{ESS_REQUESTS_PATH\}\?subject=/);
  expect(read('src/views/MyRequestsPage.tsx')).toMatch(/q\.get\('open'\)[\s\S]*openThread\(open\)/);
});

test('each employee page is gated on ess.read and reached from inside Self-Service, not the sidebar', () => {
  const nav = read('src/routes/navigation.ts');
  const paths = essPages.map((p) => p.path);
  for (const path of [ESS_LEAVE_PATH, ESS_OVERTIME_PATH, ESS_REQUESTS_PATH]) expect(paths, path).toContain(path);
  for (const path of paths) {
    const page = read(join('app/(dashboard)', path.slice(1), 'page.tsx'));
    expect(page, path).toMatch(/<PermissionGate permissions=\{\['ess\.read'\]\}>/);
    if (path !== '/ess') expect(nav, `${path} belongs in the workspace tabs, not the sidebar`).not.toMatch(new RegExp(`path: '${path}'`));
  }
  expect(nav).toMatch(/path: '\/ess', requiredPermissions: \['ess\.read'\]/);
  // The command palette still finds every workspace page, each behind ess.read.
  for (const item of essNavItems) expect(item.requiredPermissions).toEqual(['ess.read']);
  expect(essNavItems.map((i) => i.path).sort()).toEqual(paths.filter((p) => p !== '/ess').sort());
  // HR's Request Center is no longer offered to ess.read: its page admits only approvals.*.
  expect(nav).toMatch(/path: '\/hr-requests', requiredPermissions: \['approvals\.read', 'approvals\.write', 'approvals\.decide'\]/);
});

test('the lists ask for the caller\'s own records, so a manager sees theirs, not the team\'s', () => {
  const api = read('src/api/ess.ts');
  expect(api).toMatch(/'\/api\/leave\/requests', \{ params: \{ employeeId, page, pageSize \} \}/);
  expect(api).toMatch(/'\/api\/overtime\/requests', \{ params: \{ employeeId, page, pageSize \} \}/);
  expect(api).toMatch(/'\/api\/ess\/leave\/request'/);
});

test('an overnight overtime block ends the next day instead of going negative', () => {
  const w = overtimeWindow('2026-10-05', '22:00', '02:00')!;
  expect(new Date(w.endTimeUtc).getTime() - new Date(w.startTimeUtc).getTime()).toBe(4 * 60 * 60 * 1000);
  const same = overtimeWindow('2026-10-05', '17:00', '19:30')!;
  expect(new Date(same.endTimeUtc).getTime() - new Date(same.startTimeUtc).getTime()).toBe(150 * 60 * 1000);
  expect(overtimeWindow('2026-10-05', '', '19:30')).toBeNull();
});

test('only a request still waiting for approval can be cancelled from self-service', () => {
  for (const s of ['Draft', 'Submitted', 'PendingManagerApproval', 'PendingHRApproval']) expect(canCancelLeave(s), s).toBe(true);
  for (const s of ['Approved', 'Rejected', 'Cancelled', 'Withdrawn', 'CancellationRequested']) expect(canCancelLeave(s), s).toBe(false);
  expect(leaveStatus('PendingManagerApproval').label).toBe('Waiting for approval');
  expect(overtimeStatus('PendingManager').label).toBe('Waiting for approval');
  expect(hrRequestStatus('Overdue — not responded').tone).toBe('rose');
  expect(hrRequestStatus('Awaiting HR response').label).toBe('Waiting for HR to reply');
});

// Every string on the employee pages goes through t() with a whole-sentence key that has an Arabic entry.
test('every string on the employee pages has English and Arabic, with the same placeholders', () => {
  const files = [
    'src/views/MyLeavePage.tsx', 'src/views/MyOvertimePage.tsx', 'src/views/MyRequestsPage.tsx', 'src/views/MyDocumentsPage.tsx',
    'src/views/EmployeeSelfServicePage.tsx', 'src/components/ess/EssParts.tsx', 'src/components/ess/EssWorkspaceNav.tsx',
  ];
  const keys = new Set<string>();
  for (const f of files) for (const m of read(f).matchAll(/\bt\('((?:[^'\\]|\\.)*)'[,)]/g)) keys.add(m[1].replace(/\\'/g, "'"));
  for (const m of read('src/lib/essSelfService.ts').matchAll(/label: '([^']+)'/g)) keys.add(m[1]);
  for (const k of ['My Leave', 'My Overtime', 'My HR Requests']) keys.add(k);
  // The self-service home page's buttons and quick actions.
  const home = read('src/views/EmployeeSelfServicePage.tsx');
  for (const k of ['Apply Leave', 'View Payslip', 'Raise a request', 'My Requests', 'Request leave', 'Request overtime', 'Request an HR letter', 'Ask for a correction']) {
    expect(home, k).toContain(`t('${k}')`);
  }
  expect(keys.size).toBeGreaterThan(80);
  const { en, ar } = LOCALE_DICTS;
  const args = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');
  const missing = [...keys].filter((k) => !(k in en) || !(k in ar) || ar[k] === k || args(ar[k]) !== args(k));
  expect(missing).toEqual([]);
});

test('OT Request on the self-service page follows the Overtime module switch', () => {
  const home = read('src/views/EmployeeSelfServicePage.tsx');
  expect(home).toMatch(/const overtimeOn = isFeatureEnabled\('overtime'\);/);
  expect(home).toMatch(/\.\.\.\(canWrite && overtimeOn \? \[\{[^\]]*href: ESS_OVERTIME_PATH \}\] : \[\]\)/);
});

test('the submit forms are offered only with ess.write', () => {
  for (const f of ['src/views/MyLeavePage.tsx', 'src/views/MyOvertimePage.tsx', 'src/views/MyRequestsPage.tsx']) {
    const src = read(f);
    expect(src, f).toMatch(/const canWrite = useCanWriteEss\(\);/);
    expect(src, f).toMatch(/!canWrite \? \(?\s*<EssReadOnly \/>/);
  }
  expect(read('src/components/ess/EssParts.tsx')).toMatch(/hasPermission\('ess\.write'\)/);
});

test('durations and payslip months are built for the translation, not in English', () => {
  expect(splitMinutes(150)).toEqual({ hours: 2, minutes: 30 });
  expect(splitMinutes(-5)).toEqual({ hours: 0, minutes: 0 });
  expect(payslipMonthLabel(2026, 9, 'en')).toBe('September 2026');
  const ar = payslipMonthLabel(2026, 9, 'ar');
  expect(ar).toContain('2026');
  expect(ar).not.toMatch(/[A-Za-z]/);
  expect(payslipMonthLabel(0, 0, 'ar', 'Sep 2026')).toBe('Sep 2026');
  expect(read('src/views/MyPayslipsPage.tsx')).not.toMatch(/\{(s|detail)\.periodLabel/);
});

test('leave type names use the Arabic name from the leave types', () => {
  const src = read('src/views/MyLeavePage.tsx');
  expect(src).not.toMatch(/\{[br]\.leaveTypeName\}/);
  expect(src).toMatch(/typeNameById\(b\.leaveTypeId, b\.leaveTypeName\)/);
  expect(src).toMatch(/typeNameById\(r\.leaveTypeId, r\.leaveTypeName\)/);
});

test('the self-service home offers its write actions only with ess.write, and raises HR requests on /ess/requests', () => {
  const home = read('src/views/EmployeeSelfServicePage.tsx');
  expect(home).toMatch(/const canWrite = useCanWriteEss\(\);/);
  expect(home).toMatch(/\{canWrite && \(\s*<Link href=\{ESS_LEAVE_PATH\}/);
  // Request a document moved to its own tab (/ess/documents): the form, or the read-only notice.
  expect(home).not.toMatch(/essDocumentsApi/);
  expect(read('src/views/MyDocumentsPage.tsx')).toMatch(/!canWrite \? \(\s*<EssReadOnly \/>/);
  // No inline HR request form or thread any more: the card links to the employee's requests page.
  expect(home).not.toMatch(/createHrRequest|addHrRequestComment|hrRequestDetail/);
  expect(home).toMatch(/testId="ess-home-hr-requests"/);
});

test('payslip lines read in Arabic from the catalogue, or a translated standard name', () => {
  const t = (k: string) => (k === 'Net Pay' ? 'صافي الراتب' : k);
  expect(payslipLineName({ name: 'Housing', nameAr: 'بدل السكن' }, 'ar', t)).toBe('بدل السكن');
  expect(payslipLineName({ name: 'Net Pay' }, 'ar', t)).toBe('صافي الراتب');
  expect(payslipLineName({ name: 'Housing', nameAr: 'بدل السكن' }, 'en', t)).toBe('Housing');
});
