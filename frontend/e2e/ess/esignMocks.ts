import type { Page } from '@playwright/test';

/**
 * Mocked API for click-to-sign (P4): the employee's "Documents to sign" and sign screens, and HR's Signatures panel with "Sign in person".
 * Every /api/ request is intercepted; nothing touches a real tenant. Shapes follow 80-P2-P4-PLAN section 4.4.
 */
export const EMPLOYEE_NAME = 'Sam Rider';
export const EMPLOYEE_NAME_AR = 'سام رايدر';
export const EMPLOYEE_PASSWORD = 'employee-pass';
export const HR_PASSWORD = 'hr-pass';
export const DOC_SHA = 'a'.repeat(64);
// A tiny but structurally valid PDF.
const PDF = Buffer.from('%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF');

export interface EsignMockOptions {
  locale?: 'en' | 'ar';
  persona?: 'employee' | 'hr';
  /** The API's answer to the first sign attempt, to exercise a refusal. */
  signRefusal?: { status: number; code: string };
  /** Wrong password for the first N step-up attempts. */
  stepUpFailures?: number;
  rows?: any[];
  verifyValid?: boolean;
}

const row = (over: Record<string, unknown> = {}) => ({
  documentId: 'doc-1', title: 'Employment contract', fileName: 'contract.pdf', status: 'Awaiting', requestedAtUtc: '2026-10-26T09:00:00Z', integrityFlagged: false, ...over,
});
export const AWAITING = [row(), row({ documentId: 'doc-2', title: 'Code of conduct policy', fileName: 'code-of-conduct.pdf' })];
export const SIGNED = row({ documentId: 'doc-3', title: 'IT acceptable use', fileName: 'it.pdf', status: 'Signed', ackId: 'ack-3', signedAtUtc: '2026-10-25T10:00:00Z', method: 'esign_ess', typedName: EMPLOYEE_NAME, verificationCode: 'ABCD-EFGH-JKLM', certificateDocumentId: 'cert-3' });

const consentFor = (id: string) => ({
  consentVersion: 'ack-1',
  text: 'I confirm that I have read and understood the document above in full. By typing my full name and confirming with my password, I electronically acknowledge it. This is an electronic acknowledgement, not a qualified electronic signature.',
  textEn: 'I confirm that I have read and understood the document above in full. By typing my full name and confirming with my password, I electronically acknowledge it. This is an electronic acknowledgement, not a qualified electronic signature.',
  textAr: 'أقرّ بأنني قرأت المستند أعلاه وفهمته بالكامل. بكتابة اسمي الكامل وتأكيد كلمة المرور، أُقرّ به إلكترونياً. هذا إقرار إلكتروني وليس توقيعاً إلكترونياً مؤهلاً.',
  textSha256: 'b'.repeat(64), documentSha256: DOC_SHA, documentVersion: 1, title: id === 'doc-2' ? 'Code of conduct policy' : 'Employment contract', fileName: id === 'doc-2' ? 'code-of-conduct.pdf' : 'contract.pdf', integrityFlagged: false,
});

const hrEmployee = {
  id: 901, publicId: 'esign-employee', employeeCode: 'SIGN-901', fullName: EMPLOYEE_NAME, englishName: EMPLOYEE_NAME, arabicName: EMPLOYEE_NAME_AR,
  preferredName: 'Sam', gender: '', nationality: 'SA', maritalStatus: '', personalEmail: '', phone: '', workEmail: 'sam@example.test', companyId: 'c1', countryCode: 'SA', status: 'Active',
  salary: 0, department: 'Operations', designation: 'Specialist', branch: 'Riyadh', readinessState: 'Ready', activationBlockersCount: 0, profileCompletenessScore: 90, accessState: 'active',
  complianceRecords: [], history: [], transfers: [],
  documents: [
    { id: 'doc-1', employeeId: 901, documentType: 'Employment contract', fileName: 'contract.pdf', contentType: 'application/pdf', storageUrl: 'x', isRequired: false, approvalStatus: 'Approved', versionNumber: 1, uploadedAtUtc: '2026-10-20T00:00:00Z' },
    { id: 'doc-4', employeeId: 901, documentType: 'Handbook', fileName: 'handbook.pdf', contentType: 'application/pdf', storageUrl: 'x', isRequired: false, approvalStatus: 'Approved', versionNumber: 1, uploadedAtUtc: '2026-10-20T00:00:00Z' },
  ],
};

export async function bootEsign(page: Page, o: EsignMockOptions = {}, path?: string) {
  const persona = o.persona ?? 'employee';
  const log = { signs: [] as any[], deviceAuths: [] as any[], deviceSigns: [] as any[], requests: [] as any[], stepUps: [] as any[], headers: [] as Array<Record<string, string>>, errors: [] as string[], calls: [] as string[], verifies: 0 };
  let rows = [...(o.rows ?? [...AWAITING, SIGNED])];
  let failures = o.stepUpFailures ?? 0;
  let refusal = o.signRefusal;
  page.on('pageerror', e => log.errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) log.errors.push(m.text()); });
  await page.addInitScript(([locale]) => {
    localStorage.setItem('zayra_access_token', 'esign-browser-fixture');
    localStorage.setItem('kynexone.theme', 'light');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, [o.locale ?? 'en']);
  await page.addInitScript(() => { document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(st); }); });

  await page.route('**/api/**', async route => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const method = req.method();
    const ct = req.headers()['content-type'] ?? '';
    const body = req.postData() && ct.includes('application/json') ? req.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200, headers?: Record<string, string>) => route.fulfill({ status, json, headers });
    const pdf = () => route.fulfill({ status: 200, body: PDF, contentType: 'application/pdf', headers: { 'content-disposition': 'attachment; filename="doc.pdf"', 'x-content-type-options': 'nosniff' } });
    const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => reply({ type: 'about:blank', status, code, titleKey: `esign.error.${code}`, ...extra }, status);
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    log.calls.push(`${method} ${p}`);
    if (p === '/api/auth/me') return reply(persona === 'employee'
      ? { id: 'ess-user', employeeId: 501, tenantId: 't', tenantSlug: 'esign', fullName: EMPLOYEE_NAME, roles: ['Employee'], permissions: ['ess.read', 'ess.write'], companies: [] }
      : { id: 'hr-user', tenantId: 't', tenantSlug: 'esign', fullName: 'HR Officer', roles: ['HR Manager'], permissions: ['employees.read', 'employees.write', 'employees.documents', 'employees.sensitive'],
          isGroupScope: true, accountType: 'Group', companies: [{ id: 'c1', name: 'Saudi Co', code: 'S', countryCode: 'SA', isActive: true }] });
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules') return reply([]);
    if (p === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });

    // ── employee ──
    if (p === '/api/ess/signatures' && method === 'GET') return reply(rows);
    let m = /^\/api\/ess\/signatures\/([\w-]+)\/(consent|document|certificate|verify|sign)$/.exec(p);
    if (m) {
      const [, id, verb] = m;
      if (verb === 'consent') return reply(consentFor(id));
      if (verb === 'document') return pdf();
      if (verb === 'certificate') return pdf();
      if (verb === 'verify') { log.verifies++; return reply({ valid: o.verifyValid ?? true, documentIntact: o.verifyValid ?? true, chainIntact: true, recordHash: 'c'.repeat(64), checkedAtUtc: '2026-10-27T12:00:00Z' }); }
      if (verb === 'sign' && method === 'POST') {
        const h = { 'x-step-up': req.headers()['x-step-up'] ?? '', 'idempotency-key': req.headers()['idempotency-key'] ?? '' };
        log.headers.push(h);
        if (h['x-step-up'] !== 'step-up-token-1') return problem(401, 'step_up_required');
        if (refusal) { const r = refusal; refusal = undefined; return problem(r.status, r.code); }
        if (![EMPLOYEE_NAME, EMPLOYEE_NAME_AR].includes((body?.typedName ?? '').trim())) return problem(422, 'typed_name_mismatch');
        log.signs.push({ id, ...body });
        rows = rows.map(r => r.documentId === id ? { ...r, status: 'Signed', ackId: 'ack-new', signedAtUtc: '2026-10-27T12:00:00Z', typedName: body.typedName, verificationCode: 'WXYZ-2345-HJKL', certificateDocumentId: 'cert-new', method: 'esign_ess' } : r);
        return reply({ ackId: 'ack-new', signedAtUtc: '2026-10-27T12:00:00Z', recordHash: 'c'.repeat(64), certificateDocumentId: 'cert-new', verificationCode: 'WXYZ-2345-HJKL' }, 201);
      }
    }
    if (p === '/api/auth/step-up') {
      log.stepUps.push(body);
      if (failures > 0) { failures--; return reply({ type: 'about:blank', status: 401, code: 'invalid_credentials', titleKey: 'ess.completion.error.invalid_credentials' }, 401); }
      return reply({ stepUpToken: 'step-up-token-1', expiresAtUtc: '2026-10-27T12:05:00Z' });
    }

    // ── HR ──
    if (p === '/api/employees/901/signatures' && method === 'GET') return reply(rows);
    m = /^\/api\/employees\/901\/signatures\/([\w-]+)\/(consent|verify|sign-on-device)$/.exec(p);
    if (m) {
      const [, id, verb] = m;
      if (verb === 'consent') return reply(consentFor(id));
      if (verb === 'verify') { log.verifies++; return reply({ valid: o.verifyValid ?? true, documentIntact: o.verifyValid ?? true, chainIntact: true, recordHash: 'c'.repeat(64), checkedAtUtc: '2026-10-27T12:00:00Z' }); }
      if (verb === 'sign-on-device' && method === 'POST') {
        log.deviceSigns.push({ id, ...body, stepUp: req.headers()['x-step-up'] });
        if (req.headers()['x-step-up'] !== 'device-token-1') return problem(401, 'step_up_required');
        if (![EMPLOYEE_NAME, EMPLOYEE_NAME_AR].includes((body?.typedName ?? '').trim())) return problem(422, 'typed_name_mismatch');
        rows = rows.map(r => r.documentId === id ? { ...r, status: 'Signed', ackId: 'ack-dev', signedAtUtc: '2026-10-27T12:00:00Z', typedName: body.typedName, verificationCode: 'DEVC-2345-HJKL', certificateDocumentId: 'cert-dev', method: 'esign_hr_device' } : r);
        return reply({ ackId: 'ack-dev', signedAtUtc: '2026-10-27T12:00:00Z', recordHash: 'd'.repeat(64), certificateDocumentId: 'cert-dev', verificationCode: 'DEVC-2345-HJKL' }, 201);
      }
    }
    if (p === '/api/auth/step-up/employee-device' && method === 'POST') {
      log.deviceAuths.push(body);
      if (body?.password !== EMPLOYEE_PASSWORD) return problem(401, 'employee_device_auth_failed');
      return reply({ stepUpToken: 'device-token-1', expiresAtUtc: '2026-10-27T12:05:00Z' });
    }
    if (p === '/api/employees/901/signature-requests' && method === 'POST') {
      log.requests.push(body);
      rows = [row({ documentId: body.documentId, title: 'Handbook', fileName: 'handbook.pdf' }), ...rows];
      return reply({ documentId: body.documentId, requestedAtUtc: '2026-10-27T12:00:00Z' }, 201);
    }
    if (/^\/api\/employees\/documents\/[\w-]+\/download$/.test(p)) return pdf();
    if (p === '/api/employees/901') return reply(hrEmployee);
    if (p === '/api/employees') return reply(paged([hrEmployee]));
    if (p.endsWith('/readiness')) return reply({ employeeId: 901, state: 'Ready', score: 90, progress: { present: 2, requiredTotal: 2 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], warnings: [] });
    if (p === '/api/employees/field-catalog') return reply({ countryCode: 'SA', fields: [] });
    if (p === '/api/companies') return reply(paged([{ id: 'c1', legalNameEn: 'Saudi Co', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' }]));
    if (['/api/branches', '/api/departments', '/api/designations', '/api/grades', '/api/organization/cost-centers'].includes(p)) return reply(paged([]));
    if (p === '/api/admin/master-data/values') return reply([]);
    if (p.includes('/features/') || p === '/api/notifications' || p.endsWith('/documents') || p.endsWith('/history') || p.endsWith('/pay-scale')) return reply([]);
    return reply(paged([]));
  });
  await page.goto(path ?? (persona === 'employee' ? '/ess/sign' : '/people/901?section=compliance-documents'));
  return log;
}
