import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import { BASE_URL, GROUP_SLUG, roleByKey, tokenPath } from './roles';

/**
 * WAVE 1 B3 — the Chrome role and isolation security gate.
 *
 * Every boundary below is proven TWICE: once through the direct API and once through the browser.
 * That pairing is the whole point. A hidden nav item is not authorization — the UI concealing a link
 * says nothing about whether the endpoint behind it refuses the call, and it is the endpoint that an
 * attacker reaches. Rule 15.
 *
 * Nothing here skips. If the stack is down, the setup project has already failed.
 */

const tokenFor = (key: string): string => {
  const p = tokenPath(key);
  if (!fs.existsSync(p)) {
    throw new Error(
      `No stored token for role '${key}'. The security-gate setup project must run first — `
      + `it authenticates each role exactly once to stay under the login rate limit.`,
    );
  }
  return JSON.parse(fs.readFileSync(p, 'utf8')).token;
};

/** A direct-API caller carrying a role's real bearer token. */
async function apiAs(key: string, extraHeaders: Record<string, string> = {}): Promise<APIRequestContext> {
  return pwRequest.newContext({
    baseURL: BASE_URL,
    timeout: 30_000,
    extraHTTPHeaders: { Authorization: `Bearer ${tokenFor(key)}`, ...extraHeaders },
  });
}

/** Employee codes are prefixed per company by the seeder, so they identify the owning entity. */
const codesFrom = (payload: any): string[] => {
  const rows = Array.isArray(payload) ? payload : payload?.items ?? payload?.data ?? [];
  return rows.map((r: any) => r.employeeCode ?? r.code ?? '').filter(Boolean);
};

test.describe('Chrome security gate — identity boundaries', () => {
  test('a tenant user cannot reach platform administration (API and browser)', async ({ browser }) => {
    const api = await apiAs('tenant-owner');
    try {
      // API: the platform audience gate must refuse a tenant-audience token outright.
      const resp = await api.get('/api/platform/tenants');
      // 404 is deliberately NOT accepted: "the route no longer exists" is not "access was denied",
      // and accepting it would let a renamed or deleted endpoint pass as a security guarantee.
      expect(
        [401, 403],
        `A tenant admin token reached /api/platform/tenants with ${resp.status()}. The platform `
        + `audience gate is the control that keeps a tenant token out of cross-tenant endpoints.`,
      ).toContain(resp.status());
    } finally {
      await api.dispose();
    }

    // Browser: the attacker's ACTUAL move. The platform shell only checks whether
    // `platform_access_token` is present, so a test that merely omits it proves nothing about the
    // server — it passes with every backend control deleted. Plant the tenant token under the platform
    // key and require the SERVER to refuse.
    const ctx = await browser.newContext({ baseURL: BASE_URL, storageState: 'e2e/.auth/tenant-owner.json' });
    const page = await ctx.newPage();
    await page.goto('/');
    await page.evaluate(t => localStorage.setItem('platform_access_token', t), tokenFor('tenant-owner'));

    const platformApiStatuses: number[] = [];
    page.on('response', r => {
      if (r.url().includes('/api/platform/')) platformApiStatuses.push(r.status());
    });

    await page.goto('/platform/dashboard');
    await page.waitForLoadState('networkidle');

    const shell = (await page.locator('body').innerText().catch(() => '')) ?? '';
    // Only a SIBLING tenant is a leak. The caller's own slug can legitimately appear in an account
    // header, and asserting on it made this fail for a non-security reason — the shell rendering is a
    // client-side concern; the server refusing is the security property, asserted below.
    expect(shell, 'a tenant token saw another tenant in the platform console')
      .not.toMatch(/emaar-test|tata-test/i);
    // Every platform API call made with a tenant token must have been refused by the server.
    expect(
      platformApiStatuses.filter(s => s === 200),
      `platform API returned 200 to a tenant token: ${platformApiStatuses.join(', ')}`,
    ).toHaveLength(0);
    await ctx.close();
  });

  test('an unauthenticated caller is refused by the API, and the SPA redirects away', async ({ browser }) => {
    const api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000 });
    try {
      expect((await api.get('/api/employees')).status()).toBe(401);
    } finally {
      await api.dispose();
    }

    const ctx = await browser.newContext({ baseURL: BASE_URL }); // no storage state
    const page = await ctx.newPage();
    await page.goto('/people');
    await page.waitForLoadState('networkidle');
    // The API half above is the security assertion. This half only proves the SPA's client-side route
    // guard redirects — no request reaches the backend without a token, so it is a UX check, not an
    // authorization one, and it is named as such rather than dressed up as a boundary proof.
    const anonBody = (await page.locator('body').innerText().catch(() => '')) ?? '';
    expect(page.url(), 'An anonymous browser stayed on /people.').not.toMatch(/\/people/);
    expect(anonBody, 'An anonymous page rendered employee codes.').not.toMatch(/ALM-[A-Z-]+-E\d/);
    await ctx.close();
  });
});

test.describe('Chrome security gate — company isolation', () => {
  test('a company-scoped user sees only their own company (API)', async () => {
    const api = await apiAs('company-hr-dairy');
    try {
      const resp = await api.get('/api/employees?pageSize=200');
      expect(resp.status()).toBe(200);
      const codes = codesFrom(await resp.json());
      expect(codes.length, 'the seeded company should have employees to compare against').toBeGreaterThan(0);
      expect(
        codes.filter(c => c.startsWith('ALM-BAKERY-KSA')),
        'A Dairy-scoped user received Bakery employee codes.',
      ).toHaveLength(0);
    } finally {
      await api.dispose();
    }
  });

  test('a company-scoped user cannot read a sibling company through the switcher header', async () => {
    // The X-Company-Id switcher may only ever NARROW. Naming a company the token does not grant must
    // fail closed — and must not silently widen, which is what Wave 1 B1 centralised.
    const bakeryId = await companyIdByCode('ALM-BAKERY-KSA');
    const api = await apiAs('company-hr-dairy', { 'X-Company-Id': bakeryId });
    try {
      const resp = await api.get('/api/employees?pageSize=200');
      expect([200, 403]).toContain(resp.status());
      if (resp.status() === 200) {
        const codes = codesFrom(await resp.json());
        expect(
          codes.filter(c => c.startsWith('ALM-BAKERY-KSA')),
          'Selecting an unauthorized company widened the caller\'s scope instead of failing closed.',
        ).toHaveLength(0);
      }
    } finally {
      await api.dispose();
    }
  });

  test('a garbage company header never 500s and never widens scope', async () => {
    for (const value of ['not-a-guid', '00000000-0000-0000-0000-000000000000', "'; drop table users;--"]) {
      const api = await apiAs('company-hr-dairy', { 'X-Company-Id': value });
      try {
        const resp = await api.get('/api/employees?pageSize=50');
        expect(resp.status(), `X-Company-Id='${value}' produced ${resp.status()}`).toBeLessThan(500);
        if (resp.status() === 200) {
          expect(
            codesFrom(await resp.json()).filter(c => c.startsWith('ALM-BAKERY-KSA')),
            `X-Company-Id='${value}' widened scope.`,
          ).toHaveLength(0);
        }
      } finally {
        await api.dispose();
      }
    }
  });

  test('a selected-companies user sees exactly their granted companies, never all five', async () => {
    const api = await apiAs('scoped-admin');
    try {
      const resp = await api.get('/api/companies');
      expect(resp.status()).toBe(200);
      const rows = await resp.json();
      const list = Array.isArray(rows) ? rows : rows?.items ?? [];
      // Exactly the two the seeder grants. "fewer than five" would also pass if the user were granted
      // the WRONG company, or only one of the two.
      const names = list.map((c: any) => c.legalNameEn ?? c.code).sort();
      expect(names, 'the scoped admin is granted exactly ALM-DAIRY-KSA and ALM-POULTRY-KSA')
        .toEqual(['ALM-DAIRY-KSA', 'ALM-POULTRY-KSA']);
    } finally {
      await api.dispose();
    }
  });

  test('a group user sees the whole group, which is what makes the scoped cases meaningful', async () => {
    const api = await apiAs('tenant-owner');
    try {
      const resp = await api.get('/api/companies');
      expect(resp.status()).toBe(200);
      const rows = await resp.json();
      const list = Array.isArray(rows) ? rows : rows?.items ?? [];
      expect(list.length, 'the Almarai group seed has 5 companies').toBe(5);
    } finally {
      await api.dispose();
    }
  });

  test('the UI honours company scope, and a hidden nav item is not the control', async ({ browser }) => {
    const ctx = await browser.newContext({
      baseURL: BASE_URL,
      storageState: 'e2e/.auth/company-hr-dairy.json',
    });
    const page = await ctx.newPage();

    const failures: string[] = [];
    page.on('response', r => {
      if (r.status() >= 500 && r.url().includes('/api/')) failures.push(`${r.status()} ${r.url()}`);
    });

    await page.goto('/people');
    await page.waitForLoadState('networkidle');
    const body = (await page.locator('body').innerText().catch(() => '')) ?? '';

    // Load-bearing in BOTH directions. Asserting only the absence of sibling codes would pass on a
    // blank page, an error page, or a 404 — which is exactly how the first version of this test passed
    // while pointing at a route that does not exist.
    expect(
      body,
      'the page rendered none of the caller\'s OWN company data, so the absence of sibling data '
      + 'proves nothing — this assertion would pass on a blank or error page.',
    ).toMatch(/ALM-DAIRY-KSA/);
    expect(body, 'the employees page rendered a sibling company\'s codes').not.toMatch(/ALM-BAKERY-KSA/);
    expect(failures, `server errors during the page load: ${failures.join(', ')}`).toHaveLength(0);
    await ctx.close();
  });
});

test.describe('Chrome security gate — permission boundaries', () => {
  test('the auditor is genuinely read-only (API), not merely lacking a button', async () => {
    const api = await apiAs('auditor');
    try {
      const read = await api.get('/api/employees?pageSize=5');
      expect(read.status(), 'an auditor must still be able to READ').toBe(200);

      const write = await api.post('/api/employees', {
        data: { fullName: 'Gate Probe', employeeCode: `GATE-${Date.now()}`, joiningDate: '2026-01-01' },
      });
      expect(
        [400, 403],
        `Auditor POST /api/employees returned ${write.status()} — a read-only role reached a write endpoint.`,
      ).toContain(write.status());
      // 400 would mean the payload was rejected before authorization, which proves nothing about the
      // boundary, so insist on the authorization answer specifically.
      expect(write.status(), 'the refusal must be an authorization refusal, not a validation one').toBe(403);
    } finally {
      await api.dispose();
    }
  });

  test('a payroll maker is refused the approve permission itself, not merely a missing run', async () => {
    const api = await apiAs('payroll-maker');
    try {
      const resp = await api.post(`/api/payroll/runs/${crypto.randomUUID()}/approve`, { data: { reason: 'gate probe' } });
      // 403 SPECIFICALLY. Accepting 404 as well would let this pass with the control deleted: the
      // permission attribute runs before the run lookup, so granting payroll.approve to the maker
      // makes the endpoint answer 404 for a nonexistent run — and a test that accepts 404 stays green
      // while the boundary is gone.
      expect(
        resp.status(),
        'the refusal must come from the permission gate, not from the run being absent',
      ).toBe(403);
    } finally {
      await api.dispose();
    }
  });

  test('the checker HAS the approve permission — the control that makes the maker test meaningful', async () => {
    const api = await apiAs('payroll-checker');
    try {
      const resp = await api.post(`/api/payroll/runs/${crypto.randomUUID()}/approve`, { data: { reason: 'gate probe' } });
      // A nonexistent run, so 404/400/422 are all fine — the point is that the checker gets PAST the
      // permission gate that refuses the maker. Without this, a blanket 403 on the whole controller
      // would satisfy the test above while breaking approvals for everyone.
      expect(
        resp.status(),
        'the checker must not be refused by the permission gate',
      ).not.toBe(403);
    } finally {
      await api.dispose();
    }
  });
});

test.describe('Chrome security gate — session lifecycle', () => {
  test('logout invalidates browser access', async ({ browser }) => {
    const ctx = await browser.newContext({ baseURL: BASE_URL, storageState: 'e2e/.auth/group-hr.json' });
    const page = await ctx.newPage();

    await page.goto('/people');
    await page.waitForLoadState('networkidle');
    // POSITIVE CONTROL FIRST. Without proving the session was ever live, this test passes on an empty
    // storage state — the same "passes because nothing was ever visible" failure this suite already hit.
    const liveBody = (await page.locator('body').innerText().catch(() => '')) ?? '';
    // The authenticated shell is the positive control: an anonymous visitor is redirected to /login and
    // never renders this navigation. Asserting on employee rows instead made the check hostage to
    // whether the table had finished loading, which is a timing fact rather than a security one.
    expect(liveBody, 'the session was never authenticated, so clearing it proves nothing')
      .toMatch(/Group Overview|Payroll|Org Chart/);

    await page.evaluate(() => localStorage.clear());
    await page.goto('/people');
    await page.waitForLoadState('networkidle');

    const clearedBody = (await page.locator('body').innerText().catch(() => '')) ?? '';
    expect(page.url(), 'a cleared session stayed on the protected page').not.toMatch(/\/people/);
    expect(clearedBody, 'a cleared session still rendered the authenticated shell')
      .not.toMatch(/Group Overview/);
    await ctx.close();
  });

  test('a structurally invalid token is refused, not accepted as anonymous-but-ok', async () => {
    const api = await pwRequest.newContext({
      baseURL: BASE_URL,
      extraHTTPHeaders: { Authorization: 'Bearer not.a.real.token' },
    });
    try {
      expect((await api.get('/api/employees')).status()).toBe(401);
    } finally {
      await api.dispose();
    }
  });
});

/** Resolve a company id by code, using a group token that is legitimately allowed to see all five. */
async function companyIdByCode(code: string): Promise<string> {
  const api = await apiAs('tenant-owner');
  try {
    const resp = await api.get('/api/companies');
    const rows = await resp.json();
    const list = Array.isArray(rows) ? rows : rows?.items ?? [];
    const match = list.find((c: any) => (c.legalNameEn ?? c.code ?? c.companyCode) === code);
    if (!match) throw new Error(`Seeded company '${code}' not found — the enterprise seed did not run.`);
    return match.id ?? match.companyId;
  } finally {
    await api.dispose();
  }
}

/**
 * The gate's own tripwire. Playwright's `testMatch` selects by filename, so renaming or moving
 * `isolation.spec.ts` leaves the `security-gate` project resolving ZERO tests while `security-setup`
 * still resolves ten — and the job reports "10 passed" with not one authorization assertion executed.
 * A required check that can pass while testing nothing is the failure mode this whole suite exists to
 * avoid, so the count is asserted rather than assumed.
 */
const GATE_TEST_FLOOR = 12;

test('the gate actually contains its boundary tests', async () => {
  // Playwright runs from the frontend root, so this relative path is stable and needs no
  // module-system-specific __filename / import.meta juggling.
  const source = fs.readFileSync('e2e/security-gate/isolation.spec.ts', 'utf8');
  const count = (source.match(/^\s*test\(/gm) ?? []).length;
  expect(
    count,
    `Only ${count} tests found in the gate spec — expected at least ${GATE_TEST_FLOOR}. If tests were `
    + `intentionally removed, lower the floor deliberately; do not let a vanished suite report green.`,
  ).toBeGreaterThanOrEqual(GATE_TEST_FLOOR);
});
