import type { Page } from '@playwright/test';

/**
 * Mocked API for ESS "Complete your profile" and the HR review inbox (P2). Every /api/ request is intercepted;
 * nothing touches a real tenant. Shapes follow 80-P2-P4-PLAN section 2.3.
 */
export type SectionState = 'Missing' | 'Confirm' | 'Pending' | 'ChangesRequested' | 'Accepted' | 'AwaitingFinance';
export interface MockSection { key: string; state: SectionState; requiresStepUp?: boolean; reviewNote?: string; fields: Array<Record<string, any>>; documents?: any[] }

const F = (key: string, kind: string, value = '', extra: Record<string, any> = {}) => ({
  key, labelKey: `ess.completion.field.${key}`, label: key, value, required: true, kind, maxLength: 120, sensitive: false, ...extra,
});

export const SECTIONS: Record<string, MockSection> = {
  person: { key: 'person', state: 'Confirm', fields: [F('personalEmail', 'Email', 'sam@example.test'), F('phone', 'Phone', '+966500000001'), F('maritalStatus', 'Enum', 'Single', { required: false, options: ['Single', 'Married', 'Divorced', 'Widowed'] })] },
  emergency: { key: 'emergency', state: 'Missing', fields: [F('emergencyContactName', 'Text'), F('emergencyContactPhone', 'Phone')] },
  arabicName: { key: 'arabicName', state: 'Missing', fields: [F('arabicName', 'Text', '', { maxLength: 150 })] },
  id: { key: 'id', state: 'Missing', requiresStepUp: true, fields: [F('nationalId', 'NationalId', '', { maxLength: 10, sensitive: true })] },
  bank: { key: 'bank', state: 'Confirm', requiresStepUp: true, fields: [F('iban', 'Iban', 'SA** **** **** **** **** 7519', { maxLength: 34, sensitive: true }), F('bankName', 'BankName', 'SNB')] },
  documents: { key: 'documents', state: 'Missing', fields: [] },
};

const SAUDI_ID = SECTIONS.id;

export interface EssMockOptions {
  locale?: 'en' | 'ar';
  persona?: 'employee' | 'hr';
  sections?: string[];
  states?: Record<string, SectionState>;
  note?: string;
  /** The API predates the section model: /api/ess/completion answers the generic paged shape. */
  legacyApi?: boolean;
  /** Wrong password for the first N step-up attempts. */
  stepUpFailures?: number;
  hrItems?: any[];
  hrPermissions?: string[];
  /** Identity fields of an expatriate (Iqama, passport, expiries) instead of the Saudi National ID. */
  idClass?: 'saudi' | 'expat';
  /** CD58: the inbox omits document ids for a caller without employees.sensitive. */
  hideDocIds?: boolean;
  /** What POST .../documents/{id}/rescan answers: 200 with a scanStatus, or an error status (409 = no longer pending). */
  rescan?: { status: number; scanStatus?: string };
}

export const INBOX_ITEMS = [
  { requestId: 'req-person', employee: { id: 11, code: 'E-0011', name: 'Sara Al-Qahtani' }, section: 'person', status: 'PendingHR', submittedAtUtc: '2026-10-27T08:00:00Z', goesToFinance: false,
    diff: [{ field: 'phone', labelKey: 'ess.completion.field.phone', before: '+966500000099', after: '+966500000001', changed: true }, { field: 'personalEmail', labelKey: 'ess.completion.field.personalEmail', before: 'sara@example.test', after: 'sara@example.test', changed: false }], documents: [] },
  { requestId: 'req-bank', employee: { id: 12, code: 'E-0012', name: 'Omar Hassan' }, section: 'bank', status: 'PendingHR', submittedAtUtc: '2026-10-27T09:00:00Z', goesToFinance: true,
    diff: [{ field: 'iban', labelKey: 'ess.completion.field.iban', before: 'SA** **** **** **** **** 7519', after: 'SA** **** **** **** **** 1234', changed: true }, { field: 'bankName', labelKey: 'ess.completion.field.bankName', before: 'SNB', after: 'Riyad Bank', changed: true }],
    documents: [{ id: 'doc-1', kind: 'bank_letter', scanStatus: 'Clean', fileName: 'letter.pdf' }] },
];

export async function bootEss(page: Page, o: EssMockOptions = {}, path?: string) {
  const persona = o.persona ?? 'employee';
  const log = { submits: [] as any[], uploads: [] as string[], stepUps: [] as any[], decisions: [] as Array<{ id: string; verb: string; body: any }>, headers: [] as Array<Record<string, string>>, errors: [] as string[], calls: [] as string[], rescans: [] as string[] };
  const keys = o.sections ?? ['person', 'emergency', 'arabicName', 'id', 'bank', 'documents'];
  const state: Record<string, SectionState> = { ...Object.fromEntries(keys.map(k => [k, SECTIONS[k].state])), ...(o.states ?? {}) };
  SECTIONS.id = SAUDI_ID;   // the table is module-level and shared by every test in the worker
  if (o.idClass === 'expat') SECTIONS.id = { ...SECTIONS.id, fields: [F('iqamaNumber', 'Iqama', '', { maxLength: 10, sensitive: true, required: false }), F('iqamaExpiry', 'Date', '2027-02-02', { maxLength: 10, required: false }), F('passportNumber', 'Passport', '', { maxLength: 20, sensitive: true, required: false }), F('passportExpiry', 'Date', '', { maxLength: 10, required: false })] };
  let inbox = [...(o.hrItems ?? INBOX_ITEMS)];
  let failures = o.stepUpFailures ?? 0;
  const docs: any[] = [];
  page.on('pageerror', e => log.errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) log.errors.push(m.text()); });
  await page.addInitScript(([locale]) => {
    localStorage.setItem('zayra_access_token', 'ess-browser-fixture');
    localStorage.setItem('kynexone.theme', 'light');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, [o.locale ?? 'en']);
  await page.addInitScript(() => { document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(st); }); });
  const view = () => {
    const sections = keys.map(k => ({
      ...SECTIONS[k], state: state[k], reviewNote: state[k] === 'ChangesRequested' ? (o.note ?? 'Please use your mobile number.') : null,
      documents: k === 'documents' || k === 'bank' ? docs.filter(d => d.section === k).map(({ section: _s, ...d }) => d) : [], requestId: null, submittedAtUtc: null,
    })).filter(s => true);
    const done = sections.filter(s => s.state === 'Accepted').length;
    return { progress: { done, total: sections.length }, noticeVersion: 'ess-1', hosting: 'us-east', sections };
  };
  await page.route('**/api/**', async route => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const method = req.method();
    const ct = req.headers()['content-type'] ?? '';
    const body = req.postData() && ct.includes('application/json') ? req.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200, headers?: Record<string, string>) => route.fulfill({ status, json, headers });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    log.calls.push(`${method} ${p}`);
    if (p === '/api/auth/me') return reply(persona === 'employee'
      ? { id: 'ess-user', employeeId: 501, tenantId: 't', tenantSlug: 'ess', fullName: 'Sam Rider', roles: ['Employee'], permissions: ['ess.read', 'ess.write'], companies: [] }
      : { id: 'hr-user', tenantId: 't', tenantSlug: 'ess', fullName: 'HR Reviewer', roles: ['HR Manager'], permissions: o.hrPermissions ?? ['employees.read', 'employees.write', 'employees.sensitive', 'employees.override_validation'],
          isGroupScope: true, accountType: 'Group', companies: [{ id: 'c1', name: 'Saudi Co', code: 'S', countryCode: 'SA', isActive: true }, { id: 'c2', name: 'UAE Co', code: 'U', countryCode: 'AE', isActive: true }] });
    if (p === '/api/features/disabled-keys') return reply([]);
    if (p === '/api/features/modules') return reply([]);
    if (p === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/ess/completion' && method === 'GET') return o.legacyApi ? reply(paged([])) : reply(view());
    if (p === '/api/auth/step-up') {
      log.stepUps.push(body);
      if (failures > 0) { failures--; return reply({ type: 'about:blank', status: 401, code: 'invalid_credentials', titleKey: 'ess.completion.error.invalid_credentials' }, 401); }
      return reply({ stepUpToken: 'step-up-token-1', expiresAtUtc: '2026-10-27T12:10:00Z' });
    }
    let m = /^\/api\/ess\/completion\/(\w+)\/submit$/.exec(p);
    if (m && method === 'POST') {
      const key = m[1]; const sec = SECTIONS[key];
      log.headers.push({ 'x-step-up': req.headers()['x-step-up'] ?? '' });
      if (sec?.requiresStepUp && req.headers()['x-step-up'] !== 'step-up-token-1')
        return reply({ type: 'about:blank', status: 401, code: 'step_up_required', titleKey: 'ess.completion.error.step_up_required' }, 401, { 'WWW-Authenticate': 'StepUp purpose="ess.sensitive"' });
      if (body?.values?.phone === 'bad') return reply({ type: 'about:blank', status: 422, code: 'validation_failed', titleKey: 'ess.completion.error.validation_failed', errors: { phone: ['phone_invalid'] } }, 422);
      log.submits.push({ key, ...body });
      state[key] = 'Pending';
      return reply({ ...sec, state: 'Pending', requestId: 'req-1', fields: sec.fields, documents: [] });
    }
    if (p === '/api/ess/completion/documents' && method === 'POST') {
      const text = req.postData() ?? '';
      log.uploads.push(text);
      const section = /name="section"\r\n\r\n(\w+)/.exec(text)?.[1] ?? 'documents';
      const kind = /name="kind"\r\n\r\n(\w+)/.exec(text)?.[1] ?? 'other';
      if (SECTIONS[section]?.requiresStepUp && req.headers()['x-step-up'] !== 'step-up-token-1')
        return reply({ type: 'about:blank', status: 401, code: 'step_up_required', titleKey: 'ess.completion.error.step_up_required' }, 401);
      const doc = { id: `doc-${docs.length + 1}`, kind, scanStatus: 'Clean', section };
      docs.push(doc);
      return reply({ id: doc.id, kind, scanStatus: 'Clean' }, 201);
    }
    if (p === '/api/hr/profile-completion/inbox') return reply(o.hideDocIds ? inbox.map(i => ({ ...i, documents: (i.documents ?? []).map(({ id: _id, ...d }: any) => d) })) : inbox);
    m = /^\/api\/hr\/profile-completion\/documents\/([\w-]+)\/rescan$/.exec(p);
    if (m && method === 'POST') {
      log.rescans.push(m[1]);
      const r = o.rescan ?? { status: 200, scanStatus: 'clean' };
      if (r.status !== 200) return reply({ type: 'about:blank', status: r.status, code: r.status === 409 ? 'document_quarantined' : 'server_error' }, r.status);
      inbox = inbox.map(i => ({ ...i, documents: (i.documents ?? []).map((d: any) => d.id === m![1] ? { ...d, scanStatus: r.scanStatus } : d) }));
      return reply({ id: m[1], scanStatus: r.scanStatus });
    }
    m = /^\/api\/ess\/profile-change-requests\/([\w-]+)\/(approve|reject)$/.exec(p);
    if (m && method === 'POST') {
      log.decisions.push({ id: m[1], verb: m[2], body });
      inbox = inbox.filter(i => i.requestId !== m![1]);
      return m[1] === 'req-bank' && m[2] === 'approve' ? reply({ outcome: 'AwaitingFinance', changeRequestId: 'chg-1' }, 202) : reply({ id: m[1], status: m[2] === 'approve' ? 'Approved' : 'Rejected' });
    }
    if (p.endsWith('/documents') || p.includes('/features/') || p === '/api/notifications') return reply([]);
    return reply(paged([]));
  });
  await page.goto(path ?? (persona === 'employee' ? '/ess/onboarding' : '/people/onboarding-review'));
  return log;
}

export const ID_INBOX_ITEM = {
  requestId: 'req-id', employee: { id: 13, code: 'E-0013', name: 'Imran Khan' }, section: 'id', status: 'PendingHR', submittedAtUtc: '2026-10-27T10:00:00Z', goesToFinance: false,
  diff: [
    { field: 'iqamaNumber', labelKey: 'ess.completion.field.iqamaNumber', before: '', after: '******0006', changed: true },
    { field: 'iqamaExpiry', labelKey: 'ess.completion.field.iqamaExpiry', before: '2026-02-02', after: '2027-02-02', changed: true },
  ],
  documents: [],
};
