import { HANDLED, PERMS, USER, type Handler } from './demo-polish.fixture';

/**
 * The mocked API of the Bank / WPS Files tab: a locked run, one Saudi payment batch of nine employees, the bank-file gate, the employer's
 * setup, the check and the file. Shared by payroll-ux-waves (the UX waves) and bank-guided (the one-step-at-a-time flow).
 */
export const RUN_ID = '11111111-1111-4111-8111-111111111111';
export const BATCH_ID = 'b1';
export const COMPANY_ROW = { id: 'c1', name: 'Acme Arabia', legalNameEn: 'Acme Arabia LLC', tradeName: 'Acme Arabia', defaultCurrency: 'SAR', countryCode: 'SA', isActive: true };
export const RUN = {
  id: RUN_ID, companyId: 'c1', year: 2026, month: 11, status: 'Locked', runType: 'Regular', totalGrossSalary: 144856.25, totalDeductions: 11267.35, totalNetSalary: 133588.9,
  totalEmployerStatutoryCost: 10206.25, employeeCount: 9, createdAtUtc: '2026-11-14T00:50:00Z', processedAtUtc: '2026-11-14T00:51:00Z', lockedAtUtc: '2026-11-14T00:53:00Z',
};
export const BATCH = {
  id: BATCH_ID, payrollRunId: RUN_ID, batchNumber: 'PAY-202611-220353', paymentMethod: 'WPS', totalAmount: 133588.9, currency: 'SAR', status: 'Created', wpsStatus: 'Draft',
  effectiveWpsStatus: 'Draft', wpsStatusLabel: 'Draft', wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null, createdAtUtc: '2026-11-14T01:05:00Z',
  acceptanceEvidenceCount: 0, isSaudi: true, generatedFileLabel: null, paymentExclusions: [], excludedTotal: 0, allowedNextStatuses: ['Generated'],
};
export const NAMES = ['Maha Almutairi', 'Layla Al-Harbi', 'Fahad Alharbi', 'Nora Alharbi', 'Khalid Alotaibi', 'Sara Alzahrani', 'Omar Aldosari', 'Huda Alqahtani', 'Yousef Alshehri'];
export const RECORDS = NAMES.map((name, i) => ({
  id: `r${i + 1}`, paymentBatchId: BATCH_ID, employeeId: 200 + i, employeeName: name, employeeCode: `EVT2-${String(i + 1).padStart(4, '0')}`, amount: 12000 + i * 350,
  iban: `SA03800000006080101675${10 + i}`, wpsReference: `REF-${i + 1}`, status: 'Pending',
}));
export const SETTINGS = {
  formatId: 'anb-connect-csv-v1', molEstablishmentId: '7-1234567', mainAccountNumber: '0108061198800026', organizationName: 'Acme Arabia', organizationAddress1: 'Riyadh',
  organizationAddress2: 'Olaya', organizationAddress3: 'Building 10', companyName: 'Acme Arabia LLC', narrative: 'November payroll', batchType: 'PAYROLL', autoWpsUpload: false,
  nationalUnifiedNo: '', debitAccountIban: '',
};
export const FORMATS = [{ id: 'anb-connect-csv-v1', name: 'ANB Connect payroll payment — header.csv + body.csv', bank: 'Arab National Bank (ANB)', channel: 'ANB Connect API channel (payroll-payment). Not the ANB corporate-portal WPY upload.',
  sourceUrl: 'https://connect.anb.com.sa/apis/api/payroll-payment', reviewedOn: '2026-09-26', acceptanceStatus: 'specification-implemented; bank-acceptance-not-verified' }];

export type Opts = {
  perms?: string[];
  roles?: string[];
  availability?: { enabled: boolean } | 'fail' | (() => { enabled: boolean } | 'fail');
  batches?: unknown[] | 'fail' | 'forbidden';
  records?: unknown[] | 'fail' | 'forbidden';
  /** Delay (ms) before the batch list answers, to look at the loading state. */
  slowBatches?: number;
  posts?: string[];
  extra?: Handler;
  stages?: 'open' | 'folded' | 'default';
  /** The check's answer (default: passes). */
  validation?: { canExport: boolean; errors: { code: string; message: string; employeeId?: number; field?: string }[] };
  runStatus?: string;
  /** The employer's saved bank setup (default: complete). */
  settings?: Record<string, unknown>;
  /** A bank file that already exists for the batch (default: none). */
  existingExport?: unknown;
};

export const bankHandler = (o: Opts = {}): Handler => (path, search, method, route) => {
  if (method !== 'GET') o.posts?.push(`${method} ${path}`);
  if (o.extra) { const x = o.extra(path, search, method, route); if (x !== undefined) return x; }
  if (path === '/api/auth/me') return { ...USER, roles: o.roles ?? USER.roles, permissions: o.perms ?? [...PERMS, 'payroll.export', 'payroll.structure_manage'] };
  if (path === '/api/payroll/runs') return { items: [{ ...RUN, status: o.runStatus ?? RUN.status }], total: 1, page: 1, pageSize: 100 };
  if (path === '/api/payroll/companies') return [COMPANY_ROW];
  if (path === '/api/payroll/readiness') return { year: 2026, month: 11, companyId: null, completionPercent: 100, isReadyForProcessing: true, totalActiveEmployees: 9, employeesWithSalary: 9, salaryCoveragePercent: 100, validationErrors: 0, payrollRunStatus: 'Locked', steps: [] };
  // Every other Payroll section renders on empty, well-formed data (the shell is tested on all sixteen).
  if (path === '/api/payroll/grade-packages') return { source: 'GradePayScale', grades: [], olderStructures: [] };
  if (path === '/api/payroll/salary-structures' || path === '/api/payroll/employee-salary-structures' || path === '/api/payroll/salary-changes') return [];
  if (path === '/api/payroll/salary-changes/approver-status') return { hasEligibleApprover: true };
  if (path === '/api/payroll/final-settlements') return { outstandingTotal: 0, note: '', rows: [] };
  if (path === '/api/finance/gl/journal-exports/formats' || path === '/api/finance/gl/journal-exports') return [];
  if (/^\/api\/payroll\/runs\/[^/]+\/gl-journal$/.test(path)) return { runId: RUN_ID, period: '2026-11', currency: 'SAR', entries: [], totalDebits: 0, totalCredits: 0, isBalanced: true, isPosted: true };
  if (path === '/api/payroll/reports/summary') return { totalRuns: 1, lockedRuns: 1, totalEmployeesPaid: 9, totalGrossYtd: 144856.25, totalNetYtd: 133588.9 };
  if (path === '/api/payroll/overview') return { year: 2026, month: 11, totalCompanies: 1, totalActiveEmployees: 9, totalGrossPayroll: 144856.25, totalNetPayroll: 133588.9, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] };
  if (/^\/api\/payroll\/runs\/[^/]+\/processing$/.test(path)) return { runId: RUN_ID, jobId: null, status: 'Idle', processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null };
  if (/^\/api\/payroll\/runs\/[^/]+\/(validate|approvals)$/.test(path)) return [];
  if (/^\/api\/payroll\/runs\/[^/]+\/validation-overrides$/.test(path)) return { runId: RUN_ID, overrides: [], overridableCodes: [], nonOverridableCodes: [] };
  if (path === '/api/payroll/payment-batches') {
    if (o.batches === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    if (o.batches === 'forbidden') return (void route.fulfill({ status: 403, json: {} }), HANDLED);
    const all = (o.batches ?? [BATCH]) as { payrollRunId: string }[];
    const run = search.get('runId');
    const body = run ? all.filter(b => b.payrollRunId === run) : all;
    if (o.slowBatches) { setTimeout(() => void route.fulfill({ json: body }).catch(() => undefined), o.slowBatches); return HANDLED; }
    return body;
  }
  if (path === `/api/payroll/payment-batches/${BATCH_ID}/records`) {
    if (o.records === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    if (o.records === 'forbidden') return (void route.fulfill({ status: 403, json: {} }), HANDLED);
    return o.records ?? RECORDS;
  }
  const bp = '/api/payroll/bank-exports';
  if (path === `${bp}/batches/${BATCH_ID}/availability`) {
    const a = typeof o.availability === 'function' ? o.availability() : o.availability;
    if (a === 'fail') return (void route.fulfill({ status: 500, json: { message: 'boom' } }), HANDLED);
    return a ?? { enabled: true };
  }
  if (path === `${bp}/formats`) return FORMATS;
  if (path === `${bp}/companies/c1/settings`) return o.settings ?? SETTINGS;
  if (path === `${bp}/companies/c1/templates`) return [];
  if (path === `${bp}/batches/${BATCH_ID}/context`) return { companyId: 'c1', companyName: 'Acme Arabia', countryCode: 'SA', runStatus: 'Locked', existingExport: o.existingExport ?? null };
  if (path === `${bp}/batches/${BATCH_ID}/validate`) return { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1', ...(o.validation ?? {}) };
  if (path === `${bp}/batches/${BATCH_ID}/download`) {
    void route.fulfill({ status: 200, headers: { 'content-type': 'application/zip', 'content-disposition': 'attachment; filename="bank-file.zip"' }, body: 'PK' });
    return HANDLED;
  }
  if (path === `/api/payroll/payment-batches/${BATCH_ID}/wps-status` && method === 'POST') return { batchId: BATCH_ID, wpsStatus: 'Submitted' };
  if (path === `${bp}/batches/${BATCH_ID}/generate`) {
    const body = JSON.parse(route.request().postData() ?? '{}') as { batchReference: string; paymentDate: string };
    return { id: '44444444-4444-4444-8444-444444444444', formatId: 'anb-connect-csv-v1', files: [{ name: 'header.csv', sha256: 'a'.repeat(64) }, { name: 'body.csv', sha256: 'b'.repeat(64) }],
      batchReference: body.batchReference, paymentDate: body.paymentDate, employeeCount: 9, totalAmount: 133588.9, downloadUrl: `${bp}/batches/${BATCH_ID}/download` };
  }
  return undefined;
};

