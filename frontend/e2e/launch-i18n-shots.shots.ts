import { test, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';

/**
 * Arabic screenshot tour of the launch screens against a mocked API (nothing is read or written).
 * Run: LAUNCH_I18N_SHOTS=/tmp/claude/demo/i18n npx playwright test -c e2e/playwright.launch-i18n-shots.config.ts
 * Each screen is saved as <screen>-<project>.png and its leftover Latin words (outside code, ids and names) are logged.
 */
const SHOTS = process.env.LAUNCH_I18N_SHOTS || '/tmp/claude/demo/i18n';
const LOCALE = (process.env.LAUNCH_I18N_LOCALE || 'ar') as 'ar' | 'en';

const PERMS = ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'payroll.lock', 'employees.read', 'employees.write', 'leave.read', 'leave.write', 'leave.approve', 'leave.policy_manage',
  'attendance.read', 'attendance.write', 'attendance.manage', 'recruitment.read', 'recruitment.write', 'recruitment.approve', 'compliance.read', 'compliance.write', 'gosi.read', 'gosi.write', 'employees.access.issue', 'ess.read', 'ess.write'];
const USER = {
  id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', email: 'hr@example.invalid', fullName: 'Yaser Al-Ghamdi', roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, permissions: PERMS,
  companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
};
const COMPANY = { id: 'c1', name: 'Acme Arabia', legalNameEn: 'Acme Arabia LLC', tradeName: 'Acme Arabia', defaultCurrency: 'SAR', countryCode: 'SA', isActive: true, wpsEmployerId: '1234567890', gosiEmployerId: '55512345', code: 'ACME' };

const RUN = (id: string, month: number, status: string, over: Record<string, unknown> = {}) => ({
  id, companyId: 'c1', year: 2026, month, status, runType: 'Regular', totalGrossSalary: 412500, totalDeductions: 41250, totalNetSalary: 371250, totalEmployerStatutoryCost: 37125, employeeCount: 42,
  createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: status === 'Draft' ? null : '2026-10-02T08:00:00Z', lockedAtUtc: status === 'Locked' ? '2026-10-28T08:00:00Z' : null, ...over,
});
const RUNS = [RUN('11111111-1111-4111-8111-111111111110', 10, 'Approved'), RUN('11111111-1111-4111-8111-111111111109', 9, 'Locked'), RUN('11111111-1111-4111-8111-111111111111', 11, 'Processed'), RUN('11111111-1111-4111-8111-111111111112', 12, 'Draft')];
const BATCH = { id: 'b1', payrollRunId: '11111111-1111-4111-8111-111111111109', batchNumber: 'PB-2026-09-001', paymentMethod: 'WPS', totalAmount: 371250, currency: 'SAR', status: 'Generated', wpsStatus: 'Generated', wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-10-01T08:00:00Z', acceptanceEvidenceCount: 1, isSaudi: true, generatedFileLabel: 'ANB Connect payroll instruction', effectiveWpsStatus: 'Generated', wpsStatusLabel: '' };

const OPENING = { id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive', employmentType: 'Full-Time', headCount: 2, filledCount: 1, description: 'Own the Riyadh pipeline.', requirements: '', responsibilities: '', salaryFrom: 6000, salaryTo: 9000, location: 'Riyadh', status: 'Open', assignedHrName: 'Noura', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 3, remaining: 1 };
const APP = (over: Record<string, unknown> = {}) => ({ id: 'app-1', version: 3, jobOpeningId: 'op-1', jobTitle: 'Sales Executive', candidateId: 'c-1', candidateName: 'Nora Alharbi', candidateEmail: 'nora@example.invalid', stage: 'Interview', stageOrder: 3, status: 'Active', rejectionReason: '', offeredSalary: null, appliedAtUtc: '2026-11-01T08:00:00Z', stageChangedAtUtc: null, hiredAtUtc: null, onboardingDraftId: null, ...over });
const DRAFT = (id: string, status: string, over: Record<string, unknown> = {}) => ({ id, status, currentStep: 'Review', name: 'Khalid Alotaibi', arabicName: '', department: 'Sales', designation: 'Sales Executive', branch: 'Riyadh', joiningDate: '2026-12-01', source: 'Recruitment', applicationId: 'app-1', jobTitle: 'Sales Executive', createdByUserId: 'u2', createdByName: 'Noura Alsaud', isMine: false, canApprove: true, approveBlockedReason: null, profileCompletenessScore: 80, createdAtUtc: '2026-11-05T08:00:00Z', submittedAtUtc: '2026-11-06T08:00:00Z', decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeId: null, activatedEmployeeCode: null, ...over });

const NITAQAT_STANDING = {
  companyId: 'c1', companyName: 'Acme Arabia LLC', asOf: '2026-11-10', activityCode: '4711', activityNameEn: 'Retail trade', activityNameAr: 'تجارة التجزئة', sizeTierCode: 'MEDIUM', sizeTierNameEn: 'Medium', sizeTierRank: 3,
  saudiWeighted: 22, totalWeighted: 70, achievedPercent: 31.4, rawSaudiHeadcount: 20, rawTotalHeadcount: 72, band: 'MediumGreen', bandRank: 3, restrictsServices: false, consequenceSummary: 'No service restrictions.',
  currentBandFloorPercent: 28, nextBandUp: { band: 'HighGreen', requiredPercent: 36, percentGap: 4.6, saudiHiresNeeded: 5 }, bandBelow: { band: 'LowGreen', requiredPercent: 20, percentGap: 11.4 },
  scenario: { saudiHiresToNextBand: 5, expatHiresBeforeDowngrade: 12, saudiLeaversBeforeDowngrade: 3, nextBand: 'HighGreen', bandBelow: 'LowGreen' },
  breakdown: [{ label: 'Saudi (full time)', heads: 20, weightEach: 1, weighted: 20 }, { label: 'Non-Saudi (full time)', heads: 52, weightEach: 1, weighted: 48 }],
  allInputsVerified: false, unverifiedInputs: ['Size tier thresholds', 'Activity curve constants'], qiwaReportedBand: 'HighGreen', qiwaReportedOn: '2026-11-08', disagreesWithQiwa: true, bandingMethod: 'size_tier_grid', bandingMethodNote: 'Manually loaded table.', thresholdsVerified: true,
};


function mockApi(page: Page, unmocked: Set<string>, ctx: { screen: string }) {
  return page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    const paged = (items: unknown[]) => json({ items, total: items.length, page: 1, pageSize: 100 });
    if (req.method() !== 'GET') return json({ id: 'saved' });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (p === '/api/features/disabled-keys') return json(['release_a']);
    if (p.startsWith('/api/features')) return json([]);
    if (p === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'ar', defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/notifications') return json([]);
    if (p === '/api/companies' || p === '/api/companies/all') return paged([COMPANY]);
    // payroll
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/runs') return paged(RUNS);
    if (p === '/api/payroll/overview') return json({ year: 2026, month: 10, totalCompanies: 1, totalActiveEmployees: 42, totalGrossPayroll: 412500, totalNetPayroll: 371250, totalValidationErrors: 2, totalPendingApprovals: 1,
      companies: [{ companyId: 'c1', companyName: 'Acme Arabia LLC', tradeName: 'Acme Arabia', currency: 'SAR', activeEmployees: 42, employeesWithSalary: 40, employeesMissingSalary: 2, salaryCoveragePercent: 95, payrollRunStatus: 'Approved', grossPayroll: 412500, totalDeductions: 41250, netPayroll: 371250, validationErrors: 2, validationWarnings: 3, pendingApprovals: 1, wpsEmployerId: '1234567890', gosiEmployerId: '55512345', hasPayrollRun: true }] });
    if (p === '/api/payroll/readiness') return json({ year: 2026, month: 10, companyId: null, completionPercent: 83, isReadyForProcessing: false, totalActiveEmployees: 42, employeesWithSalary: 40, salaryCoveragePercent: 95, validationErrors: 2, payrollRunStatus: 'Approved',
      steps: [{ step: 1, label: 'Grade packages defined', complete: true, detail: '6 grades' }, { step: 2, label: 'Salaries assigned', complete: false, detail: '2 employees missing salary' }, { step: 3, label: 'Payroll run created', complete: true, detail: '' }] });
    if (p === '/api/payroll/reports/summary') return json({ totalRuns: 4, lockedRuns: 1, totalEmployeesPaid: 42, totalGrossYtd: 3712500, totalNetYtd: 3341250, ytdByCompany: [] });
    if (p === '/api/payroll/payment-batches') return json([BATCH]);
    if (/^\/api\/payroll\/payment-batches\/[^/]+\/records$/.test(p)) return json([{ id: 'pr1', employeeId: 7, employeeCode: 'EMP-0007', employeeName: 'Khalid Alotaibi', amount: 8100, iban: 'SA0380000000608010167519', wpsReference: '', status: 'Pending', paymentMethod: 'WPS' }]);
    if (p === '/api/payroll/reports/reconciliation') return json({ runId: '11111111-1111-4111-8111-111111111111', period: '2026-11', priorPeriod: 'Oct 2026', currentHeadcount: 42, priorHeadcount: 40, joinerCount: 3, leaverCount: 1, flaggedVariances: 1, currentTotalGross: 412500, priorTotalGross: 400000, currentTotalNet: 371250, priorTotalNet: 360000, variances: [{ employeeId: 7, employeeName: 'Khalid Alotaibi', priorGross: 8000, currentGross: 9100, variancePct: 13.75, priorNet: 7200, currentNet: 8100, flag: 'High variance' }] });
    const m = /^\/api\/payroll\/runs\/([^/]+)\/(.+)$/.exec(p);
    if (m) {
      const tail = m[2];
      if (tail === 'validate') return json([{ id: 'e1', employeeId: 101, severity: 'Error', code: 'MISSING_IBAN', message: 'Missing IBAN', isResolved: false }, { id: 'w1', employeeId: 7, severity: 'Warning', code: 'W', message: 'Late arrivals held', isResolved: false }]);
      if (tail === 'validation-overrides') return json({ runId: m[1], overrides: [], overridableCodes: [], nonOverridableCodes: [] });
      if (tail === 'approvals') return json([{ id: 'a1', step: 'HR / Payroll Review', status: 'Approved', actorName: 'Noura', createdAtUtc: '2026-10-03T08:00:00Z', comments: '' }]);
      if (tail === 'population') return json({ runId: m[1], includedCount: 42, excludedCount: 2, notEligibleCount: 0, included: [], excluded: [], notEligible: [] });
      if (tail === 'processing') return json({ runId: m[1], jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null });
      if (tail === 'slips') return json({ items: [{ id: 's1', runId: m[1], employeeId: 7, employeeCode: 'EMP-0007', employeeName: 'Khalid Alotaibi', department: 'Sales', basicSalary: 6000, housingAllowance: 1500, transportAllowance: 600, otherAllowances: 1000, grossSalary: 9100, deductions: 1000, netSalary: 8100, status: 'Calculated', employeeStatutoryTotal: 600, employerStatutoryTotal: 700, loanDeductions: 0, ytdGross: 82000, ytdDeductions: 9000, ytdNet: 73000, deductionLines: [] }], total: 1, page: 1, pageSize: 100 });
      if (tail === 'payslips') return json([{ id: 'ps1', employeeId: 7, employeeName: 'Khalid Alotaibi', payslipNumber: 'PS-0007', language: 'ar', essPublished: true, essPublishedAtUtc: '2026-10-29T08:00:00Z', generatedAtUtc: '2026-10-28T08:00:00Z' }]);
      if (tail === 'register') return json({ rows: [] });
    }
    // recruitment
    if (p === '/api/recruitment/openings') return json({ items: [OPENING], total: 1, page: 1, pageSize: 50 });
    if (p === '/api/recruitment/openings/op-1') return json({ opening: OPENING, stageCounts: [] });
    if (p === '/api/recruitment/applications') return json({ items: [APP()], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/recruitment/applications/kanban/op-1') return json({ stages: ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'].map((stage, order) => ({ stage, order, applications: stage === 'Interview' ? [APP()] : [] })), rejected: [] });
    if (p === '/api/recruitment/dashboard' || p === '/api/recruitment/summary') return json({ activeApplications: 12, offersPending: 2, hiredThisMonth: 3, openRequisitions: 4, convertedRequisitions: 2, openings: [], pipeline: [], sources: [], avgTimeToHireDays: 21, totalHiredYtd: 9 });
    if (p === '/api/recruitment/requisitions') return json({ items: [{ id: 'rq1', requisitionNumber: 'REQ-0001', departmentName: 'Sales', designation: 'Sales Executive', headCount: 2, status: 'Approved', justification: 'Growth', requestedByName: 'Noura', createdAtUtc: '2026-10-15T08:00:00Z', targetJoinDate: '2026-12-15', budgetFrom: 6000, budgetTo: 9000, requiredSkills: '' }], total: 1, page: 1, pageSize: 50 });
    if (p === '/api/recruitment/candidates') return json({ items: [{ id: 'c-1', firstName: 'Nora', lastName: 'Alharbi', email: 'nora@example.invalid', phone: '+966 50 123 4567', currentTitle: 'Sales Associate', currentCompany: 'Retail Co', totalExperienceYears: 4, educationLevel: 'Bachelor', source: 'Direct', resumeUrl: '' }], total: 1, page: 1, pageSize: 50 });
    if (p === '/api/recruitment/interviews') return json({ items: [{ id: 'iv1', applicationId: 'app-1', candidateName: 'Nora Alharbi', jobTitle: 'Sales Executive', interviewType: 'Technical', interviewerNames: 'Sarah Ahmed', scheduledAtUtc: '2026-11-20T09:00:00Z', durationMinutes: 45, status: 'Scheduled', overallRating: null, recommendation: '', feedbackNotes: '' }], total: 1, page: 1, pageSize: 20 });
    if (p === '/api/recruitment/offers/history') return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (p === '/api/recruitment/onboarding/checklists') return json([{ id: 'ck-1', code: 'STD', name: 'Standard onboarding', description: '', applicableTo: 'All', departmentName: '', isActive: true }]);
    if (p === '/api/recruitment/onboarding/summary') return json({ total: 6, pending: 2, inProgress: 1, completed: 3, blocked: 0, mandatory: 4, mandatoryCompleted: 2, completionPct: 50 });
    if (p === '/api/recruitment/onboarding/tasks') return json({ total: 1, items: [{ id: 't1', taskTitle: 'Complete employment contract', category: 'Document', assignedToName: 'Noura', dueDate: '2026-12-01', isMandatory: true, status: 'Pending' }] });
    if (p === '/api/recruitment/onboarding/checklists/ck-1/template-tasks') return json([]);
    if (p.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    // new hires
    if (p === '/api/employees/drafts') return json({ items: [DRAFT('d1', 'PendingHrApproval'), DRAFT('d2', 'Draft', { source: 'Manual', name: 'Maha Alzahrani', submittedAtUtc: null, canApprove: false })], total: 2, page: 1, pageSize: 25, counts: { awaitingApproval: 1, draft: 1, activated: 4, rejected: 0, cancelled: 1 } });
    if (p === '/api/employees/drafts/d2') return json({ summary: DRAFT('d2', 'Draft', { source: 'Manual', name: 'Maha Alzahrani', canApprove: false }), documentCount: 0, draft: { id: 'd2', status: 'Draft', englishName: 'Maha Alzahrani', arabicName: '', personalEmail: '', workEmail: '', phone: '', nationality: 'Saudi', countryCode: 'SA', department: 'Sales', designation: 'Sales Executive', branch: 'Riyadh', workLocation: '', joiningDate: '2026-12-01', contractType: 'Permanent', probationEndDate: null, salary: null }, activationCheck: null });
    if (p === '/api/employees/drafts/d1') return json({ summary: DRAFT('d1', 'PendingHrApproval'), documentCount: 3, draft: { id: 'd1', status: 'PendingHrApproval', englishName: 'Khalid Alotaibi', arabicName: '', personalEmail: 'khalid@example.invalid', workEmail: '', phone: '', nationality: 'Saudi', countryCode: 'SA', department: 'Sales', designation: 'Sales Executive', branch: 'Riyadh', workLocation: '', joiningDate: '2026-12-01', contractType: 'Permanent', probationEndDate: null, salary: 8100 },
      activationCheck: { canActivate: false, resolvedCompanyName: 'Acme Arabia LLC', problems: [{ key: 'grade', label: 'Grade', reason: 'No grade is set', fix: 'Choose a grade on the draft.' }], advisories: ['Add a work email'] } });
    // leave
    if (p === '/api/leave/types') return json([{ id: 'lt1', code: 'ANNUAL', nameEn: 'Annual Leave', nameAr: 'الإجازة السنوية', category: 'Annual', isPaid: true, isActive: true, companyId: 'c1', isHalfDayAllowed: true, isHourlyAllowed: false, requiresAttachment: false, maxConsecutiveDays: 30, colorCode: '#10b981' }, { id: 'lt2', code: 'UNPAID', nameEn: 'Unpaid Leave', nameAr: 'إجازة بدون أجر', category: 'Unpaid', isPaid: false, isActive: true, companyId: 'c1', isHalfDayAllowed: false, isHourlyAllowed: true, requiresAttachment: true, maxConsecutiveDays: 0, colorCode: '#f59e0b' }]);
    if (p === '/api/leave/requests') return json({ items: [{ id: 'lr1', employeeId: 7, employeeName: 'Khalid Alotaibi', leaveTypeId: 'lt1', leaveTypeName: 'Annual Leave', startDate: '2026-11-20', endDate: '2026-11-24', totalDays: 5, status: 'PendingManagerApproval', reason: 'Family trip', isEmergency: false, kind: 'Leave', createdAtUtc: '2026-11-05T08:00:00Z' }], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/leave/balances') return json({ items: [{ employeeId: 7, employeeName: 'Khalid Alotaibi', leaveTypeId: 'lt1', leaveTypeName: 'Annual Leave', year: 2026, entitledDays: 21, carriedForwardDays: 3, usedDays: 8, pendingDays: 2, balanceDays: 14, encashedDays: 0 }], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/leave/policies') return json({ items: [{ id: 'lp1', name: 'Saudi Annual Leave — Full-Time', leaveTypeId: 'lt1', countryCode: 'SA', annualEntitlementDays: 21, accrualMethod: 'Monthly', departmentName: '', grade: '', employmentType: '', companyId: 'c1', isActive: true, isEncashable: true, includesWeekends: false, includesPublicHolidays: false, payrollImpact: 'None' }], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/leave/holidays/calendars') return json([{ id: 'hc1', name: 'Saudi Public Holidays 2026', year: 2026, countryCode: 'SA', companyId: 'c1', holidays: [{ id: 'h1', name: 'Saudi National Day', date: '2026-09-23', hijriDate: '', holidayType: 'Public' }] }]);
    if (p === '/api/ess/leave/balance') return json([{ leaveTypeId: 'lt1', leaveTypeName: 'Annual Leave', entitled: 21, used: 8, pending: 2, available: 14 }, { leaveTypeId: 'lt3', leaveTypeName: 'Maternity Leave', entitled: 0, used: 0, pending: 0, available: 0, statutoryEntitlementDays: 84 }]);
    if (p === '/api/leave/calendar') return json([{ id: 'ce1', employeeId: 7, employeeName: 'Khalid Alotaibi', leaveTypeName: 'Annual Leave', colorCode: '#10b981', startDate: '2026-11-18', endDate: '2026-11-20', status: 'Approved', totalDays: 3, department: 'Sales' }]);
    if (p === '/api/leave/calendar/team' || p === '/api/leave/calendar/today') return json({});
    if (p === '/api/leave/dashboard' || p === '/api/leave/summary') return json({ onLeaveToday: [], pendingApprovals: 1, unauthorizedAbsences: 0, pendingEncashments: 0 });
    if (p.startsWith('/api/leave/')) return json({ items: [], total: 0, page: 1, pageSize: 100 });
    // saudi compliance / gosi
    if (p === '/api/saudi-compliance/nitaqat') return json({ ok: true, standing: NITAQAT_STANDING, refusal: null });
    if (p === '/api/saudi-compliance/nitaqat/trend') return json({ ok: true, companyId: 'c1', points: [{ asOfDate: '2026-10-10', achievedPercent: 30.1, saudiWeighted: 21, totalWeighted: 70, band: 'MediumGreen', bandRank: 3 }, { asOfDate: '2026-11-10', achievedPercent: 31.4, saudiWeighted: 22, totalWeighted: 70, band: 'MediumGreen', bandRank: 3 }], changePercentagePoints: 1.3, direction: 'Up', projectedBandWarning: null, refusal: null });
    if (p === '/api/saudi-compliance/nitaqat/activities') return json({ ok: true, activities: [{ code: '4711', nameEn: 'Retail trade', nameAr: 'تجارة التجزئة', activityGroup: 'Trade', hasCompleteGrid: true }], coverage: { activitiesTotal: 40, activitiesWithCompleteGrid: 38 } });
    if (p === '/api/saudi-compliance/dashboard') return json({
      overall: { complianceScore: 72, urgentActionCount: 3, lastEvaluatedAt: '2026-11-10T08:00:00Z', enabledModules: ['QIWA', 'WPS', 'GOSI'], scoreBreakdown: [
        { module: 'QIWA', weightPercent: 40, score: 80, pointsContributed: 32, measurable: true, basis: '34 of 42 employees pass the Qiwa data check.' },
        { module: 'WPS', weightPercent: 30, score: 60, pointsContributed: 18, measurable: true, basis: '2 employees have no IBAN.' },
        { module: 'GOSI', weightPercent: 30, score: 0, pointsContributed: 0, measurable: false, basis: 'No GOSI records to assess yet.' }] },
      qiwa: { featureEnabled: true, credentialConfigured: false, connectionStatus: 'Simulated', lastConnectedAt: null, totalEmployees: 42, readyForSync: 34, blockedFromSync: 8, readinessPercent: 81, failedSyncCount: 1, lastSuccessfulSync: null, isLiveIntegration: false, lastSimulatedSync: '2026-11-09T08:00:00Z',
        blockedEmployees: [{ employeeId: 7, employeeCode: 'EMP-0007', fullName: 'Khalid Alotaibi', missingFields: ['Contract number'] }] },
      wps: { lastRunStatus: 'Completed', lastRunPeriod: 'Oct 2026', pendingApprovals: 1, missingIbanCount: 2, exportHistoryCount: 3, lastExportDate: '2026-11-01', lastExportStatus: 'Generated', blockingIssues: [] },
      gosi: { employeesMissingGosiRef: 4, employeesMissingGosiEmployerId: 0, gosiEmployerIdConfigured: true, readyCount: 38, blockedCount: 4, warningCount: 1, readinessPercent: 90, gccEmployeeCount: 2, varianceCount: 1, warnings: [], blockedEmployees: [{ employeeId: 9, employeeCode: 'EMP-0009', fullName: 'Maha Alzahrani', blockingIssueCodes: ['GOSI_REF_MISSING'] }] },
      actionItems: [{ id: 'a1', severity: 'High', module: 'WPS', title: 'Employees missing IBAN', description: '2 employees have no IBAN on file.', affectedCount: 2, recommendedAction: 'Add the IBAN', route: '/people', permissionRequired: 'employees.write', canAct: true, evaluatedAt: '2026-11-10T08:00:00Z' }],
    });
    if (p.startsWith('/api/saudi-compliance')) return json({});
    // attendance
    if (p === '/api/attendance/devices') return json({ items: [{ id: 'dv1', deviceName: 'Main Gate Biometric', deviceType: 'Biometric fingerprint + face', vendor: 'ZKTeco', serialNumber: 'ZK-001', locationName: 'Riyadh HQ', ipAddress: '10.0.0.5', endpointUrl: '', port: 4370, apiKeyReference: 'ref', syncMethod: 'Push API', syncFrequency: 'Real-time (Push)', lastSyncStatus: 'Success', lastSyncAtUtc: '2026-11-10T07:00:00Z', errorLog: '', isActive: true }], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/attendance/dashboard') return json({ date: '2026-11-10', activeEmployees: 42, present: 36, absent: 3, late: 4, missingPunch: 2, overtimeEmployees: 5, deviceErrors: 1, pendingRegularizations: 2 });
    if (p === '/api/attendance/today') return json({ date: '2026-11-10', totalActive: 42, present: 36, absent: 3, onLeave: 3, late: 4 });
    if (p === '/api/attendance/daily') return json({ items: [{ id: 'ad1', employeeId: 7, employeeName: 'Khalid Alotaibi', department: 'Sales', branch: 'Riyadh', workDate: '2026-11-10', firstInUtc: '2026-11-10T05:05:00Z', lastOutUtc: '2026-11-10T14:10:00Z', totalWorkedMinutes: 485, lateMinutes: 5, earlyExitMinutes: 0, overtimeMinutes: 20, undertimeMinutes: 0, missingPunch: false, status: 'Late', manualCorrectionStatus: '', isPayrollLocked: false }], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/attendance/events/raw' || p === '/api/attendance/regularization/pending-approval' || p === '/api/attendance/regularization/my' || p === '/api/attendance/exceptions') return json({ items: [], total: 0, page: 1, pageSize: 100 });
    if (p === '/api/payroll/grade-packages') return json({ source: 'GradePayScale', grades: [{ gradeId: 'g1', gradeCode: 'G5', gradeName: 'Senior', gradeNameAr: 'أول', companyId: 'c1', companyName: 'Acme Arabia LLC', currency: 'SAR', minSalary: 7000, maxSalary: 12000, isActive: true, monthlyGross: 9000, monthlyDeductions: 600, outsideRange: false, lines: [{ code: 'BASIC', name: 'Basic salary', componentType: 'Earning', calculationType: 'Fixed', amount: 6000, percentage: 0 }, { code: 'HOUSING', name: 'Housing allowance', componentType: 'Earning', calculationType: 'Fixed', amount: 2000, percentage: 0 }] }], olderStructures: [] });
    if (p === '/api/payroll/final-settlements') return json({ outstandingTotal: 18000, note: '', rows: [{ id: 'fs1', employeeId: 8, employeeCode: 'EMP-0008', employeeName: 'Faisal Alharbi', companyId: 'c1', offboardingId: 'ob1', lastWorkingDay: '2026-10-31', settlementDueDate: '2026-11-15', terminationReason: 'Resignation', status: 'Approved', currency: 'SAR', netPayable: 18000, payrollRunId: null, paymentBatchId: null, paidAtUtc: null, isOverdue: false, overdueDays: 0 }] });
    if (/^\/api\/payroll\/runs\/[^/]+\/gl-journal$/.test(p)) return json({ runId: 'r', period: '2026-09', currency: 'SAR', entries: [{ componentCode: 'BASIC', componentName: 'Basic salary', glAccount: '5100', glAccountName: 'Salaries expense', entryType: 'DR', amount: 250000 }, { componentCode: 'NET', componentName: 'Net pay payable', glAccount: '2100', glAccountName: 'Salaries payable', entryType: 'CR', amount: 250000 }], totalDebits: 250000, totalCredits: 250000, isBalanced: true, isPosted: true });
    if (p === '/api/payroll/employee-salary-structures') return json([{ id: 'es1', employeeId: 7, employeeCode: 'EMP-0007', employeeName: 'Khalid Alotaibi', department: 'Sales', companyId: 'c1', salaryStructureId: 'ss1', basicSalary: 6000, housingAllowance: 1500, transportAllowance: 600, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0, fixedDeduction: 0, currency: 'SAR', effectiveDate: '2026-01-01', isActive: true, createdAtUtc: '2026-01-01T00:00:00Z' }]);
    unmocked.add(`${ctx.screen}: ${p}`);
    if (p.startsWith('/api/payroll/') || p.startsWith('/api/finance/') || p.startsWith('/api/attendance/')) return json([]);
    return json({ items: [], total: 0, page: 1, pageSize: 100 });
  });
}

async function tour(page: Page, name: string, url: string, after?: (page: Page) => Promise<void>) {
  const unmocked = new Set<string>();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text().slice(0, 300)); });
  await page.addInitScript((locale: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, LOCALE);
  await mockApi(page, unmocked, { screen: name });
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3500);
  if (after) await after(page);
  await page.waitForTimeout(1200);
  fs.mkdirSync(SHOTS, { recursive: true });
  const project = test.info().project.name;
  await page.screenshot({ path: `${SHOTS}/${name}-${project}.png`, fullPage: true });
  // Latin words left in rendered text, ignoring code-like tokens.
  const latin = await page.evaluate(() => {
    const out: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let n: Node | null;
    while ((n = walker.nextNode())) {
      const el = n.parentElement; if (!el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'CODE'].includes(el.tagName)) continue;
      const text = (n.textContent || '').trim(); if (!text) continue;
      const words = text.match(/[A-Za-z]{3,}/g); if (words) out.push(text.slice(0, 90));
    }
    return out;
  });
  const dir = await page.evaluate(() => document.documentElement.dir);
  fs.writeFileSync(`${SHOTS}/${name}-${project}.latin.txt`, `dir=${dir}\nlatin lines: ${latin.length}\n${latin.join('\n')}\n--- unmocked\n${[...unmocked].join('\n')}\n--- errors\n${errors.join('\n')}\n`);
}

const click = (label: string | RegExp) => async (page: Page) => { await page.getByRole('tab', { name: label }).first().click().catch(() => page.getByText(label, { exact: false }).first().click()); };

const clickBtn = (name: string) => async (page: Page) => { await page.getByRole('button', { name, exact: true }).first().click(); };

const SCREENS: [string, string, ((page: Page) => Promise<void>)?][] = [
  ['payroll-dashboard', '/payroll'],
  ['payroll-runs', '/payroll?tab=runs&run=11111111-1111-4111-8111-111111111110'],
  ['payroll-validation', '/payroll?tab=validation&run=11111111-1111-4111-8111-111111111110'],
  ['payroll-approvals', '/payroll?tab=approvals&run=11111111-1111-4111-8111-111111111110'],
  ['payroll-payslips', '/payroll?tab=payslips&run=11111111-1111-4111-8111-111111111110'],
  ['payroll-bank-wps', '/payroll?tab=bank-wps&run=11111111-1111-4111-8111-111111111109'],
  ['payroll-payment-tracking', '/payroll?tab=payment-tracking'],
  ['payroll-reports', '/payroll?tab=reports'],
  ['payroll-eosb', '/payroll?tab=eosb'],
  ['payroll-gl-journal', '/payroll?tab=gl-journal&run=11111111-1111-4111-8111-111111111109'],
  ['payroll-reconciliation', '/payroll?tab=reconciliation&run=11111111-1111-4111-8111-111111111111'],
  ['payroll-final-settlement', '/payroll?tab=final-settlement'],
  ['payroll-grade-packages', '/payroll?tab=salary-structures'],
  ['payroll-employee-salary', '/payroll?tab=employee-salary'],
  ['leave-dashboard', '/leave'],
  ['leave-balance', '/leave', click(/رصيد الإجازات|Leave Balance/)],
  ['leave-apply', '/leave', click(/تقديم طلب إجازة|Apply Leave/)],
  ['leave-requests', '/leave', click(/جميع الطلبات|My Requests/)],
  ['leave-approvals', '/leave', click(/الاعتمادات|Approvals/)],
  ['leave-calendar', '/leave', click(/التقويم|Calendar/)],
  ['leave-encashment', '/leave', click(/صرف الإجازات|Encashment/)],
  ['leave-compoff', '/leave', click(/إجازة تعويضية|Comp-Off/)],
  ['leave-absences', '/leave', click(/^\s*الغياب|Absences/)],
  ['leave-insights', '/leave', click(/التقارير|Insights/)],
  ['setup-leave-policies', '/setup?tab=leavePolicies&companyId=c1'],
  ['ess-leave', '/ess/leave'],
  ['saudi-compliance', '/saudi-compliance'],
  ['saudi-compliance-nitaqat', '/saudi-compliance', click(/Saudization|السعودة/)],
  ['gosi-filing', '/gosi-filing'],
  ['attendance', '/attendance'],
  ['attendance-devices', '/setup?tab=attendanceDevices'],
  ['recruitment', '/recruitment'],
  ['recruitment-requisitions', '/recruitment', clickBtn('طلبات التوظيف')],
  ['recruitment-openings', '/recruitment', clickBtn('الوظائف الشاغرة')],
  ['recruitment-talent', '/recruitment', clickBtn('مجمع المواهب')],
  ['recruitment-interviews', '/recruitment', clickBtn('المقابلات')],
  ['recruitment-assessments', '/recruitment', clickBtn('التقييمات')],
  ['recruitment-offers', '/recruitment', clickBtn('العروض')],
  ['recruitment-workforce', '/recruitment', clickBtn('تخطيط القوى العاملة')],
  ['recruitment-onboarding', '/recruitment/onboarding'],
  ['new-hires', '/people/new-hires'],
  ['new-hires-review', '/people/new-hires', async (page) => { await page.getByRole('button', { name: /View|عرض/ }).first().click(); await page.waitForTimeout(1500); }],
];

for (const [name, url, after] of SCREENS) {
  if (process.env.LAUNCH_I18N_ONLY && !new RegExp(process.env.LAUNCH_I18N_ONLY).test(name)) continue;
  test(name, async ({ page }) => { await tour(page, name, url, after); });
}
