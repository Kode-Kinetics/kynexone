/**
 * Second-person pay approval for the e2e seeds. A hire's pay package is approved by someone other
 * than the person who entered it (payroll.approve plus employees.sensitive), and an employee cannot
 * be activated until it is. The seeds create people as the tenant admin, so each tenant gets one
 * extra "Payroll Manager" account here, used only to approve. Disposable stacks only (callers have
 * already refused anything else).
 */
export interface PayApprover {
  /** Approves the employee's pay package; an already-approved package (409) is not an error. */
  approve(employeeId: number, note?: string): Promise<void>;
}

type Json = any;

async function api(apiBase: string, method: string, path: string, token?: string, body?: unknown): Promise<{ status: number; body: Json; text: string }> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(`${apiBase.replace(/\/$/, '')}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  let parsed: Json = null;
  try { parsed = text ? JSON.parse(text) : null; } catch { /* kept in text */ }
  return { status: response.status, body: parsed, text };
}

export async function ensurePayApprover(opts: {
  apiBase: string; platformToken: string; tenantId: string; slug: string; password: string;
}): Promise<PayApprover> {
  const { apiBase, platformToken, tenantId, slug, password } = opts;
  const email = `pay-approver@${slug}.test`;
  const listed = await api(apiBase, 'GET', `/api/platform/tenants/${tenantId}/users`, platformToken);
  const rows: Json[] = Array.isArray(listed.body) ? listed.body : listed.body?.items ?? listed.body?.Items ?? [];
  if (!rows.some((u) => String(u.email ?? u.Email).toLowerCase() === email)) {
    const created = await api(apiBase, 'POST', `/api/platform/tenants/${tenantId}/users`, platformToken,
      { email, password, fullName: 'Pay Package Approver', roleName: 'Payroll Manager', entityScope: 'group' });
    if (![200, 201].includes(created.status)) throw new Error(`[pay-approver] create ${email} failed: HTTP ${created.status} ${created.text.slice(0, 300)}`);
  }
  return signInPayApprover(apiBase, slug, password);
}

/** Signs in the approver a previous provisioning run created (the seeds that follow the bootstrap use this). */
export async function signInPayApprover(apiBase: string, slug: string, password: string): Promise<PayApprover> {
  const email = `pay-approver@${slug}.test`;
  const login = await api(apiBase, 'POST', '/api/auth/login', undefined, { email, password, tenantSlug: slug });
  const token = login.body?.accessToken ?? login.body?.token;
  if (login.status !== 200 || !token) throw new Error(`[pay-approver] ${email} cannot sign in to '${slug}': HTTP ${login.status} ${login.text.slice(0, 200)}`);
  return {
    async approve(employeeId, note = 'E2E fixture: synthetic pay package checked by a second person') {
      const res = await api(apiBase, 'POST', `/api/employees/${employeeId}/compensation/approve`, token, { note });
      if (res.status === 200 || res.status === 409) return;
      throw new Error(`[pay-approver] approve employee ${employeeId} failed: HTTP ${res.status} ${res.text.slice(0, 300)}`);
    },
  };
}
