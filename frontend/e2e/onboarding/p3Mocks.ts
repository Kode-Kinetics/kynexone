import type { Page } from '@playwright/test';

/** Mocked API for the WP-P3 Setup screens; it keeps its own state so a spec can create, copy, remove and restore. */
export const CO1 = { id: 'co-1', legalNameEn: 'Alpha Trading', legalNameAr: 'ألفا للتجارة', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
export const CO2 = { id: 'co-2', legalNameEn: 'Beta Services', legalNameAr: 'بيتا للخدمات', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
export const STARTER_TASKS = [
  { id: 's1', title: 'GOSI registration', titleAr: 'التسجيل في التأمينات', ownerRole: 'Gov', dueOffsetDays: 0, blocksActivation: false, condition: { nationalityClass: 'any' }, isStatutory: true, evidenceRequired: false },
  { id: 's2', title: 'Issue the Iqama through Muqeem', titleAr: 'إصدار الإقامة', ownerRole: 'Gov', dueOffsetDays: 30, blocksActivation: false, condition: { nationalityClass: 'expat' }, isStatutory: true, evidenceRequired: false },
  { id: 's3', title: 'Company email and laptop', titleAr: 'البريد والحاسوب', ownerRole: 'IT', dueOffsetDays: -1, blocksActivation: false, condition: { nationalityClass: 'any' }, isStatutory: false, evidenceRequired: false },
];

export interface State { checklists: any[]; hire: any[]; writes: Array<{ method: string; path: string; body: any }> }

export async function bootSetup(page: Page, url: string, opts: { locale?: 'en' | 'ar'; permissions?: string[]; seedChecklists?: any[]; seedHire?: any[] } = {}) {
  const st: State = { checklists: opts.seedChecklists ?? [], hire: opts.seedHire ?? [], writes: [] };
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript((locale: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    document.addEventListener('DOMContentLoaded', () => { const s = document.createElement('style'); s.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(s); });
  }, opts.locale ?? 'en');
  const summary = (c: any) => ({ id: c.id, companyId: c.companyId, name: c.name, nameAr: c.nameAr ?? null, isDefault: c.isDefault, isActive: c.isActive, isDeleted: c.isDeleted, taskCount: c.tasks.length });
  await page.route('**/api/**', async route => {
    const req = route.request(); const url2 = new URL(req.url()); const p = url2.pathname; const m = req.method();
    const body = req.postData() ? req.postDataJSON() : undefined;
    const json = (j: unknown, status = 200) => route.fulfill({ status, json: j });
    if (m !== 'GET') st.writes.push({ method: m, path: p, body });
    if (p === '/api/auth/me') return json({ id: 'u', tenantId: 't', tenantSlug: 'f', fullName: 'Reviewer', roles: ['Admin'], accountType: 'Group', isGroupScope: true,
      permissions: opts.permissions ?? ['dashboard.read', 'employees.read', 'employees.write', 'organization.read', 'organization.write', 'onboarding.templates.manage'],
      companies: [CO1, CO2].map(c => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: 'SA', isActive: true })) });
    if (p === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (p === '/api/features/disabled-keys') return json([]);
    if (p === '/api/companies') return json(paged([CO1, CO2]));
    if (p === '/api/tenant-admin/localization') return json({ countryCode: 'SA', currencyCode: 'SAR' });
    if (p === '/api/branches') return json(paged([{ id: 'b1', companyId: 'co-1', code: 'RYD', nameEn: 'Riyadh', nameAr: 'الرياض', isActive: true }]));
    if (p === '/api/departments') return json(paged([{ id: 'd1', companyId: 'co-1', code: 'OPS', nameEn: 'Operations', nameAr: 'العمليات', isActive: true }]));
    if (p === '/api/designations') return json(paged([{ id: 'ds1', companyId: 'co-1', code: 'CSH', titleEn: 'Cashier', titleAr: 'أمين صندوق', isActive: true }]));
    if (p === '/api/grades') return json(paged([{ id: 'g1', companyId: 'co-1', code: 'G3', name: 'Grade 3', isActive: true }]));
    // checklists
    if (p === '/api/onboarding/checklist-templates/starters' && m === 'GET') return json([{ key: 'ksa', name: 'Saudi Arabia starter checklist', nameAr: 'قائمة', tasks: STARTER_TASKS }]);
    const copyStarter = p.match(/^\/api\/onboarding\/checklist-templates\/starters\/ksa\/copy-to\/(.+)$/);
    if (copyStarter && m === 'POST') {
      const c = { id: `cl-${st.checklists.length + 1}`, companyId: copyStarter[1], name: 'Saudi Arabia starter checklist', nameAr: null, isDefault: false, isActive: true, isDeleted: false, tasks: STARTER_TASKS };
      st.checklists.push(c); return json(c);
    }
    if (p === '/api/onboarding/checklist-templates' && m === 'GET') {
      const co = url2.searchParams.get('companyId'); const withDeleted = url2.searchParams.get('includeDeleted') === 'true';
      return json(st.checklists.filter(c => (!co || c.companyId === co) && (withDeleted || !c.isDeleted)).map(summary));
    }
    if (p === '/api/onboarding/checklist-templates' && m === 'POST') {
      const c = { id: `cl-${st.checklists.length + 1}`, ...body, nameAr: body.nameAr ?? null, isActive: true, isDeleted: false, tasks: body.tasks.map((t: any, i: number) => ({ ...t, id: `t${i}` })) };
      st.checklists.push(c); return json(c, 201);
    }
    const one = p.match(/^\/api\/onboarding\/checklist-templates\/([^/]+)$/);
    if (one) {
      const c = st.checklists.find(x => x.id === one[1]);
      if (!c) return json({}, 404);
      if (m === 'GET') return json(c);
      if (m === 'PUT') { Object.assign(c, body, { tasks: body.tasks.map((t: any, i: number) => ({ ...t, id: t.id ?? `n${i}` })) }); return json(c); }
      if (m === 'DELETE') { c.isActive = false; const used = c.used ?? 0; if (used === 0) c.isDeleted = true; return json({ outcome: used === 0 ? 'deleted' : 'turnedOff', used }); }
    }
    const restore = p.match(/^\/api\/onboarding\/checklist-templates\/([^/]+)\/restore$/);
    if (restore && m === 'POST') { const c = st.checklists.find(x => x.id === restore[1]); c.isDeleted = false; c.isActive = true; return json(c); }
    const copy = p.match(/^\/api\/onboarding\/checklist-templates\/([^/]+)\/copy-to\/(.+)$/);
    if (copy && m === 'POST') { const s = st.checklists.find(x => x.id === copy[1]); const c = { ...s, id: `cl-${st.checklists.length + 1}`, companyId: copy[2], isDefault: false }; st.checklists.push(c); return json(c); }
    // hire templates
    if (p === '/api/onboarding/hire-templates' && m === 'GET') {
      const co = url2.searchParams.get('companyId'); const withDeleted = url2.searchParams.get('includeDeleted') === 'true';
      return json(st.hire.filter(h => (!co || h.companyId === co) && (withDeleted || !h.isDeleted)));
    }
    if (p === '/api/onboarding/hire-templates' && m === 'POST') {
      const h = { id: `ht-${st.hire.length + 1}`, ...body, essCompletionSections: body.essCompletionSections ?? [], isDeleted: false, checklistTaskCount: 0 };
      st.hire.push(h); return json(h, 201);
    }
    const hOne = p.match(/^\/api\/onboarding\/hire-templates\/([^/]+)$/);
    if (hOne && m === 'DELETE') { const h = st.hire.find(x => x.id === hOne[1]); h.isActive = false; h.isDeleted = true; return json({ outcome: 'deleted', used: 0 }); }
    const hRestore = p.match(/^\/api\/onboarding\/hire-templates\/([^/]+)\/restore$/);
    if (hRestore && m === 'POST') { const h = st.hire.find(x => x.id === hRestore[1]); h.isDeleted = false; h.isActive = true; return json(h); }
    return json(p.includes('/features/') ? [] : paged([]));
  });
  await page.goto(url);
  return { st, errors };
}

