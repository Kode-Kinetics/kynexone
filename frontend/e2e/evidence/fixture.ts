/**
 * The Playwright fixture that hands a spec an {@link EvidenceRecorder}, and writes both copies of
 * the bundle when the spec finishes — pass or fail.
 *
 * `evidenceTest` is the only entry point a spec needs:
 *
 *   evidenceTest('…', async ({ evidence, openAs }) => {
 *     const hr = await openAs(INTELLIFLOW_HR_MGR);
 *     await evidence.step({ id: 'approve', actor: hr.persona, page: hr.page, … });
 *   });
 *
 * `openAs` signs a persona in through the real login FORM rather than by injecting a token. On an
 * evidence run that distinction matters: the bundle claims a named person did this, and a session
 * minted out of band is not that person signing in. It also attaches the page to the settle
 * detector, so nothing the spec drives can be screenshotted before it is settled.
 *
 * The recorder is finished in teardown, not at the end of the test body, so a story that fails
 * halfway still produces a manifest and an index containing the failed step and its evidence.
 * Evidence of a failure is the point; a bundle that only exists when everything passed is a
 * marketing artefact.
 */
import { request as playwrightRequest, test as base, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { resolveTarget } from '../identity/env';
import { tenantLoginLive } from '../helpers';
import { personaFor, type FixtureUser } from '../world';
import { attach } from './settle';
import { bundlePaths, EvidenceRecorder } from './recorder';

export interface SignedInPersona {
  page: Page;
  persona: ReturnType<typeof personaFor>;
  /** A bearer token for the same identity, for API calls and persistence re-reads. */
  token: string;
  close: () => Promise<void>;
}

interface EvidenceFixtures {
  evidence: EvidenceRecorder;
  /** Sign a fixture user in, in their own browser context, through the login form. */
  openAs: (user: FixtureUser, tenantSlug: string) => Promise<SignedInPersona>;
  /** An API context pointed at the API directly, not through the frontend proxy. */
  api: APIRequestContext;
}

const { baseUrl, apiBaseUrl } = resolveTarget();

export const evidenceTest = base.extend<EvidenceFixtures>({
  api: async ({}, use) => {
    const context = await playwrightRequest.newContext({ baseURL: apiBaseUrl, timeout: 60_000 });
    await use(context);
    await context.dispose();
  },

  evidence: async ({ api }, use, testInfo) => {
    const { bundleDir, originalsDir } = bundlePaths();
    const recorder = new EvidenceRecorder({
      story: process.env.E2E_EVIDENCE_STORY?.trim() || testInfo.title,
      bundleDir,
      originalsDir,
      baseUrl,
      apiBaseUrl,
      expectedCommit: process.env.E2E_EXPECTED_COMMIT?.trim() || null,
      expectedDatabase: process.env.E2E_EXPECTED_DATABASE?.trim() || null,
      request: api,
    });
    await use(recorder);
    // Teardown: always write the bundle, including for a failed run.
    const run = await recorder.finish();
    testInfo.annotations.push({
      type: 'evidence',
      description:
        `${run.summary.steps} steps · ${run.summary.passed} passed, ${run.summary.failed} failed · `
        + `${run.summary.captured} captured, ${run.summary.noCapture} explicitly no-capture · bundle: ${bundleDir}`,
    });
  },

  openAs: async ({ browser, api }, use) => {
    const opened: SignedInPersona[] = [];
    const open = async (user: FixtureUser, tenantSlug: string): Promise<SignedInPersona> => {
      const context = await (browser as Browser).newContext({ baseURL: baseUrl });
      const page = await context.newPage();
      attach(page);
      await tenantLoginLive(page, user.email, user.password, tenantSlug);
      const login = await api.post('/api/auth/login', {
        data: { email: user.email, password: user.password, tenantSlug },
      });
      if (!login.ok()) {
        throw new Error(
          `[evidence] ${user.email} signed in through the form but the API login for its token `
          + `returned ${login.status()}: ${await login.text()}`,
        );
      }
      const body = await login.json();
      const signed: SignedInPersona = {
        page,
        persona: personaFor(user, tenantSlug),
        token: body.accessToken ?? body.token,
        close: () => context.close(),
      };
      opened.push(signed);
      return signed;
    };
    await use(open);
    for (const s of opened) await s.close().catch(() => { /* context already gone */ });
  },
});

export { expect } from '@playwright/test';
