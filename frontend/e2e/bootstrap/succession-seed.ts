/**
 * SUC module seed — the succession scenario on top of the fixture world (run e2e/bootstrap/provision.ts first).
 *
 * API-only and idempotent, like provision.ts and learning-seed.ts (which it runs first: the learning flag, the
 * Quality department and the `lrn.manager` login linked to IntelliFlow's head of Operations). It creates NO succession
 * data — the journey designates, nominates, assesses, calibrates and builds slates through the UI. It adds the
 * world those screens need:
 *
 *   • the `succession` opt-in flag ON for IntelliFlow (tenant A) and Ras Al-Manar (tenant B, for denial checks);
 *   • two org positions WITH a company (CFO, Head of Quality) for designation, and one in tenant B;
 *   • a PUBLISHED appraisal cycle (through the real cycle → manager review → final approval → publish path) in which
 *     Liu Wei scores 86 (High) and Carlos Mendez 68 (Moderate) — the two competing candidates; everyone else 72;
 *   • a goal for Liu Wei, and an active learning path (one acknowledgement lesson) for the development-plan actions;
 *   • an HR Manager and an HR Director login LINKED to Quality employees (outside the candidates' reporting lines):
 *     candidate-level reads need a resolvable employee, because conflicts of interest are checked against it.
 *
 * Deterministic: fixed codes and names; the same run twice yields the same world.
 */
import { assertDisposableHost } from '../disposable-host.guard';
import { pageItems } from '../paging';
import {
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_EMP2, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG, INTELLIFLOW_SUPERVISOR,
  ORGANISATION_ADMIN, RASALMANAR_ADMIN, RASALMANAR_SLUG,
} from '../world';
import { LEARNING_MANAGER, login, seedLearningWorld } from './learning-seed';

const API = (process.env.E2E_API_BASE_URL ?? 'http://localhost:5117').replace(/\/$/, '');
const CYCLE_NAME = 'SUC FY2025 appraisal';
const PATH_CODE = 'SUC-LEAD';
const GOAL_TITLE = 'Run the FY2027 budget cycle end to end';
export const SCORES: Record<string, number> = { 'Liu Wei': 86, 'Carlos Mendez': 68 };
export const SUC_HR_MANAGER = {
  email: 'suc.hrmanager@intelliflow.com', password: process.env.E2E_SUC_HR_MANAGER_PASSWORD ?? 'SucHrManager@2026!x', role: 'HR Manager',
};
export const SUC_HR_DIRECTOR = {
  email: 'suc.hrdirector@intelliflow.com', password: process.env.E2E_SUC_HR_DIRECTOR_PASSWORD ?? 'SucHrDirector@2026!x', role: 'HR Director',
};

export interface SuccessionWorld {
  apiBase: string;
  tenantA: { id: string; slug: string; companyId: string };
  tenantB: { id: string; slug: string; positionId: string };
  positions: { cfo: string; hoq: string };
  candidates: { a: { id: number; name: string }; b: { id: number; name: string } };
  managerHead: { id: number; name: string };
  pathId: string;
  goalId: string;
  cycleId: string;
  logins: {
    hrManager: { email: string; password: string }; hrDirector: { email: string; password: string };
    manager: { email: string; password: string }; employeeA: { email: string; password: string }; admin: { email: string; password: string };
    employeeB: { email: string; password: string }; supervisor: { email: string; password: string };
    tenantBAdmin: { email: string; password: string };
  };
}

interface Res { status: number; body: any; text: string }

async function call(method: string, path: string, opts: { token?: string; body?: unknown; companyId?: string } = {}): Promise<Res> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const response = await fetch(`${API}${path}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await response.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* kept in text */ }
  return { status: response.status, body, text };
}

function ok(res: Res, what: string, accept = [200, 201, 204]): Res {
  if (!accept.includes(res.status)) throw new Error(`[succession-seed] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 600)}`);
  return res;
}

/** Switch an opt-in feature on for the one organisation (as its administrator) when any caller still sees it off. */
async function enableFlag(tokens: Array<{ token: string; tenantId: string }>, key: string) {
  let off = false;
  for (const t of tokens) {
    const disabled = ok(await call('GET', '/api/features/disabled-keys', { token: t.token }), 'read feature flags').body as string[];
    if (disabled.includes(key)) off = true;
  }
  if (!off) return;
  const signIn = ok(await call('POST', '/api/auth/login', {
    body: { email: ORGANISATION_ADMIN.email, password: ORGANISATION_ADMIN.password },
  }), 'organisation administrator login');
  const organisationAdmin = signIn.body?.accessToken ?? signIn.body?.token;
  ok(await call('PUT', `/api/tenant-admin/features/${key}`, { token: organisationAdmin, body: { isEnabled: true } }), `enable ${key}`);
}

async function ensurePosition(token: string, companyId: string, code: string, title: string, departmentId?: string): Promise<string> {
  const list = pageItems(ok(await call('GET', '/api/positions?page=1&pageSize=200', { token, companyId }), 'list positions').body);
  const found = list.find((p: any) => p.code === code);
  if (found) return found.id;
  const created = ok(await call('POST', '/api/positions', {
    token, companyId,
    body: { code, title, companyId, departmentId: departmentId ?? null, fte: 1, budgetedMonthlyCost: 45000, currency: 'SAR', effectiveFrom: '2025-01-01' },
  }), `create position ${code}`, [200, 201]).body;
  return created.id;
}

/** A login for an existing employee, through the product's own path: work email → invitation → acceptance. Idempotent. */
async function ensureLinkedLogin(admin: string, companyId: string, employee: { id: number; code: string },
  persona: { email: string; password: string; role: string }) {
  const signsIn = await call('POST', '/api/auth/login', { body: { email: persona.email, password: persona.password } });
  if (signsIn.status === 200) return;
  ok(await call('POST', '/api/employee-access/work-emails', {
    token: admin, companyId, body: { rows: [{ employeeCode: employee.code, workEmail: persona.email }], dryRun: false },
  }), `set the work email of ${employee.code}`);
  const invite = ok(await call('POST', '/api/access/employee-logins/invite', {
    token: admin, companyId,
    body: { employeeId: employee.id, email: persona.email, accessMode: 'FullPortal', roles: [persona.role], confirmedWorkEmail: true },
  }), `invite ${employee.code} as ${persona.role}`, [200, 201]);
  const invitationToken = decodeURIComponent(String(invite.body.invitationUrl ?? '').split('#token=')[1] ?? '');
  if (!invitationToken) throw new Error(`[succession-seed] The ${persona.role} invitation came back without a link.`);
  ok(await call('POST', '/api/auth/accept-invitation', {
    body: { invitationToken, newPassword: persona.password },
  }), `accept the ${persona.role} invitation`, [200, 204]);
}

/** A published appraisal cycle through the product's own path: launch → manager reviews → final approval → publish. */
async function ensurePublishedCycle(admin: string): Promise<string> {
  // No X-Company-Id: a cycle spans every company, and launching it needs the group scope a company selection narrows.
  const cycles = pageItems(ok(await call('GET', '/api/performance/cycles?page=1&pageSize=100', { token: admin }), 'list cycles').body);
  let cycle = cycles.find((c: any) => c.name === CYCLE_NAME);
  if (cycle?.status === 'Published' || cycle?.status === 'Closed') return cycle.id;
  if (!cycle) {
    const templates = pageItems(ok(await call('GET', '/api/performance/templates', { token: admin }), 'list templates').body);
    let template = templates.find((t: any) => t.name === 'SUC balanced scorecard');
    if (!template) {
      template = ok(await call('POST', '/api/performance/templates', {
        token: admin,
        body: {
          name: 'SUC balanced scorecard', departmentName: null, designationTitle: null, grade: null, kpiWeight: 40, competencyWeight: 30,
          attendanceWeight: 10, productivityWeight: 10, feedbackWeight: 5, disciplineWeight: 5, minPassingScore: 60, requiresCalibration: false,
          requires360Feedback: false, isDefault: false, ratingLabels: null,
        },
      }), 'create scorecard template', [200, 201]).body;
    }
    cycle = ok(await call('POST', '/api/performance/cycles', {
      token: admin,
      body: {
        name: CYCLE_NAME, cycleType: 'Annual', reviewPeriodStart: '2025-01-01', reviewPeriodEnd: '2025-12-31', enableCalibration: false,
        enable360Feedback: false, enableSelfAssessment: false, enableForcedDistribution: false, selfAssessmentDeadline: null,
        managerReviewDeadline: null, calibrationDeadline: null, defaultScorecardTemplateId: template.id, notes: 'Succession acceptance seed',
      },
    }), 'create cycle', [200, 201]).body;
  }
  if (cycle.status === 'Draft') ok(await call('POST', `/api/performance/cycles/${cycle.id}/launch`, { token: admin }), 'launch cycle');
  const reviews = async () => pageItems(ok(await call('GET', `/api/performance/reviews?cycleId=${cycle.id}&pageSize=500`, { token: admin }), 'list reviews').body);
  let state = ok(await call('GET', `/api/performance/cycles/${cycle.id}`, { token: admin }), 'read cycle').body?.cycle?.status;
  if (state === 'Active') {
    ok(await call('POST', `/api/performance/cycles/${cycle.id}/advance`, { token: admin }), 'advance to InReview');
    state = 'InReview';
  }
  if (state === 'InReview') {
    for (const r of await reviews()) {
      if (r.status === 'ManagerReviewComplete') continue;
      const score = SCORES[r.employeeName] ?? 72;
      ok(await call('POST', `/api/performance/reviews/${r.id}/manager-review`, {
        token: admin,
        body: { kpiScore: score, competencyScore: score, attendanceScore: score, productivityScore: score, feedbackScore: score, disciplineScore: score,
          managerNotes: 'Succession acceptance seed', reviewerManagerId: null, reviewerManagerName: null, competencyRatings: null },
      }), `manager review for ${r.employeeName}`);
    }
    ok(await call('POST', `/api/performance/cycles/${cycle.id}/advance`, { token: admin }), 'advance to FinalApproval');
    state = 'FinalApproval';
  }
  if (state === 'FinalApproval') {
    for (const r of await reviews()) {
      if (r.status !== 'FinalApproval') continue;
      ok(await call('POST', `/api/performance/reviews/${r.id}/publish`, { token: admin }), `publish review of ${r.employeeName}`);
    }
    ok(await call('POST', `/api/performance/cycles/${cycle.id}/advance`, { token: admin }), 'advance to Published');
  }
  return cycle.id;
}

async function ensureLearningPath(hr: string, companyId: string): Promise<string> {
  const paths = ok(await call('GET', '/api/learning/paths', { token: hr, companyId }), 'list paths').body as any[];
  const existing = (pageItems(paths) as any[]).find((p: any) => p.code === PATH_CODE) ?? (Array.isArray(paths) ? paths.find((p: any) => p.code === PATH_CODE) : null);
  if (existing?.status === 'Active') return existing.id;
  let pathId = existing?.id as string | undefined;
  if (!pathId) {
    const course = ok(await call('POST', '/api/learning/courses', {
      token: hr, companyId,
      body: {
        code: 'SUC-LEAD-101', title: 'Leading finance teams', titleAr: 'قيادة فرق المالية', description: 'What a finance leader is accountable for.',
        estimatedMinutes: 15, contentKind: 'Lesson', lessonHtml: '<h2>Accountability</h2><p>A finance leader owns the close, the controls and the people.</p>',
        completionRule: 'Acknowledgement', issuesCertificate: false,
      },
    }), 'create course', [200, 201]).body;
    const published = ok(await call('POST', `/api/learning/courses/${course.id}/publish`, { token: hr, companyId }), 'publish course').body;
    pathId = ok(await call('POST', '/api/learning/paths', {
      token: hr, companyId,
      body: { code: PATH_CODE, name: 'Finance leadership essentials', nameAr: 'أساسيات القيادة المالية', renewalWindowDays: 30,
        items: [{ courseId: published.id, isRequired: true, prerequisiteSequence: null }] },
    }), 'create path', [200, 201]).body.id;
  }
  ok(await call('POST', `/api/learning/paths/${pathId}/activate`, { token: hr, companyId }), 'activate path');
  return pathId!;
}

export async function seedSuccessionWorld(): Promise<SuccessionWorld> {
  assertDisposableHost(API, 'seed the succession scenario');
  const learning = await seedLearningWorld();
  const companyId = learning.tenantA.companyId;

  const admin = await login(INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
  const adminB = await login(RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);
  await enableFlag([{ token: admin, tenantId: learning.tenantA.id }, { token: adminB, tenantId: learning.tenantB.id }], 'succession');

  const cfo = await ensurePosition(admin, companyId, 'SUC-CFO', 'Chief Financial Officer');
  const hoq = await ensurePosition(admin, companyId, 'SUC-HOQ', 'Head of Quality', learning.departments.quality);
  const companiesB = pageItems(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: adminB }), 'list companies (B)').body);
  const positionB = await ensurePosition(adminB, companiesB[0].id, 'SUC-B-CFO', 'Ras Al-Manar CFO');

  const cycleId = await ensurePublishedCycle(admin);

  const hr = await login(INTELLIFLOW_HR_MGR.email, INTELLIFLOW_HR_MGR.password, INTELLIFLOW_SLUG);
  const pathId = await ensureLearningPath(hr, companyId);

  const [, qualityHr, qualityDirector] = learning.employees.quality;
  await ensureLinkedLogin(admin, companyId, qualityHr, SUC_HR_MANAGER);
  await ensureLinkedLogin(admin, companyId, qualityDirector, SUC_HR_DIRECTOR);

  const a = learning.employees.ops1;
  const goals = pageItems(ok(await call('GET', `/api/performance/goals?employeeId=${a.id}`, { token: admin, companyId }), 'list goals').body);
  let goal = goals.find((g: any) => g.title === GOAL_TITLE);
  if (!goal) {
    goal = ok(await call('POST', '/api/performance/goals', {
      token: admin, companyId,
      body: { employeeId: a.id, employeeName: a.name, cycleId: null, title: GOAL_TITLE, description: 'Own the budget calendar and sign-off.',
        category: 'Individual', kpiType: 'Qualitative', measurementUnit: '%', targetValue: 100, actualValue: 0, weight: 100, dueDate: '2027-03-31' },
    }), 'create goal', [200, 201]).body;
  }

  return {
    apiBase: API,
    tenantA: { id: learning.tenantA.id, slug: INTELLIFLOW_SLUG, companyId },
    tenantB: { id: learning.tenantB.id, slug: RASALMANAR_SLUG, positionId: positionB },
    positions: { cfo, hoq },
    candidates: { a: { id: a.id, name: a.name }, b: { id: learning.employees.ops2.id, name: learning.employees.ops2.name } },
    managerHead: { id: learning.employees.managerHead.id, name: learning.employees.managerHead.name },
    pathId, goalId: goal.id, cycleId,
    logins: {
      hrManager: { email: SUC_HR_MANAGER.email, password: SUC_HR_MANAGER.password },
      hrDirector: { email: SUC_HR_DIRECTOR.email, password: SUC_HR_DIRECTOR.password },
      manager: { email: LEARNING_MANAGER.email, password: LEARNING_MANAGER.password },
      admin: { email: INTELLIFLOW_ADMIN.email, password: INTELLIFLOW_ADMIN.password },
      employeeA: { email: INTELLIFLOW_EMP1.email, password: INTELLIFLOW_EMP1.password },
      employeeB: { email: INTELLIFLOW_EMP2.email, password: INTELLIFLOW_EMP2.password },
      supervisor: { email: INTELLIFLOW_SUPERVISOR.email, password: INTELLIFLOW_SUPERVISOR.password },
      tenantBAdmin: { email: RASALMANAR_ADMIN.email, password: RASALMANAR_ADMIN.password },
    },
  };
}
