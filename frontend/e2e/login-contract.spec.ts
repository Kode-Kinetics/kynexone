import { test, expect, type Locator, type Page } from '@playwright/test';

/**
 * LOGIN CONTRACT — the tenant /login surface is being rebuilt from scratch with a new brand
 * concept. This spec is the safety net around that rebuild.
 *
 * ── What this file is for ───────────────────────────────────────────────────────────────────
 * Everything asserted here is load-bearing for something OUTSIDE the login page:
 *
 *  - `#li-em` / `#li-pw` / `#li-ws` are the ids that e2e/helpers.ts `tenantLoginLive()` fills, and
 *    that e2e/pilot-critical.spec.ts and e2e/tenant-auth.spec.ts fill directly. Rename one and the
 *    entire pilot lane logs in as nobody.
 *  - The autocomplete tokens are what password managers and the browser's own credential UI key
 *    off. `organization` on the workspace field is the only thing that stops a manager filling the
 *    tenant slug with a username.
 *  - `.tenant-login-shell` is referenced outside this component.
 *  - The five auth states (login, forgot, reset, mfa, mfa-enroll) are one component; a rebuild that
 *    only re-skins `login` silently strands the other four. This file guards the login↔forgot hop,
 *    which is the one a customer hits on day one.
 *
 * ── What this file deliberately does NOT assert ─────────────────────────────────────────────
 * No colours, no copy, no layout, no animation, no element the redesign is free to replace. The
 * headings, the marketing panel, the security bullets, the store badges, the brand scene — all of
 * it may change without touching this spec. Controls are found by id, by role + accessible name,
 * or by a `data-testid` hook the rebuild may add; never by a visual class or decorative string.
 *
 * If you are doing the rebuild: making this file pass is the bar, not a formality. If a contract
 * here genuinely has to change, change the consumers listed above in the same commit.
 */

// The login surface must be proven from a COLD, unauthenticated context. The `chromium` project
// loads the platform-admin storage state; inheriting it here would test a page that a real
// first-time visitor never sees.
test.use({ storageState: { cookies: [], origins: [] } });

const EMAIL = '#li-em';
const PASSWORD = '#li-pw';
const WORKSPACE = '#li-ws';
const SHELL = '.tenant-login-shell';

/** The form that owns the password field — survives any restructuring of the page around it. */
const loginForm = (page: Page): Locator => page.locator('form').filter({ has: page.locator(PASSWORD) });

/**
 * The primary submit control, without depending on its label. Prefers an explicit submit inside the
 * login form, falls back to a page-level submit (a redesign may hoist it out with `form=`).
 */
const submitControl = (page: Page): Locator =>
  loginForm(page)
    .locator('button[type="submit"], input[type="submit"], button:not([type])')
    .or(page.locator('button[type="submit"], input[type="submit"]'))
    .first();

/**
 * Computed accessible name, via Playwright's own accname implementation rather than a text match.
 * `getByLabel` alone would be too narrow: it sees aria-label / aria-labelledby / <label for>, but
 * not the `title`/`placeholder` fallbacks that the accessibility tree also resolves. Asserting on
 * the computed name lets the rebuild choose ANY valid labelling technique and still pass.
 */
async function accessibleName(locator: Locator): Promise<string> {
  const snapshot = await locator.ariaSnapshot();
  // e.g. `- textbox "Work email"` or `- textbox "Work email": someone@example.com`
  const match = snapshot.match(/^\s*-\s+[a-z]+(?:\s+"((?:[^"\\]|\\.)*)")?/);
  return (match?.[1] ?? '').trim();
}

/** How the accessible name is currently supplied — used to flag the weak cases, not to fail them. */
async function namingTechnique(page: Page, selector: string): Promise<string> {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return 'missing';
    if (el.getAttribute('aria-labelledby')) return 'aria-labelledby';
    if (el.getAttribute('aria-label')) return 'aria-label';
    if (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)) return 'label[for]';
    if (el.closest('label')) return 'wrapping-label';
    if (el.getAttribute('title')) return 'title';
    if (el.getAttribute('placeholder')) return 'placeholder-only';
    return 'none';
  }, selector);
}

/** True when the element currently holds DOM focus. */
const isFocused = (locator: Locator): Promise<boolean> =>
  locator.evaluate((el) => el === document.activeElement);

test.describe('Login contract (must hold before AND after the rebrand)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/login');

    // Wait for the page to SETTLE, not merely to render. React's streaming SSR parks the shell in
    // a hidden staging container and then moves it into the body, so for a few hundred ms the
    // document genuinely holds TWO #li-em elements — one of them zero-sized. Asserting on the
    // first match during that window is how a login test flakes: `.first()` picks the hidden copy.
    // Both conditions must hold together: exactly one in the document, and it visible.
    //
    // (This is also why `e2e/helpers.ts` should not rely on `.first()` — see the report.)
    await expect
      .poll(
        async () => {
          const [total, visible] = await Promise.all([
            page.locator(EMAIL).count(),
            page.locator(`${EMAIL}:visible`).count(),
          ]);
          return `${total}/${visible}`;
        },
        {
          timeout: 20_000,
          message: 'the login form must settle to exactly one visible #li-em (total/visible)',
        },
      )
      .toBe('1/1');
  });

  test('the three credential fields exist, are visible and are enabled', async ({ page }) => {
    for (const selector of [EMAIL, PASSWORD, WORKSPACE]) {
      const field = page.locator(selector);
      await expect(field, `${selector} must be exactly one element`).toHaveCount(1);
      await expect(field, `${selector} must be visible`).toBeVisible();
      await expect(field, `${selector} must be enabled`).toBeEnabled();
      await expect(field, `${selector} must not be readonly`).not.toHaveAttribute('readonly', /.*/);
    }
  });

  test('the login shell class exists and wraps the credential fields', async ({ page }) => {
    const shell = page.locator(SHELL);
    await expect(shell, `${SHELL} must exist — it is referenced outside this component`).toHaveCount(1);
    await expect(shell).toBeVisible();
    // Guards against the class surviving on some unrelated wrapper while the form moves out of it.
    await expect(page.locator(`${SHELL} ${EMAIL}`)).toHaveCount(1);
    await expect(page.locator(`${SHELL} ${PASSWORD}`)).toHaveCount(1);
    await expect(page.locator(`${SHELL} ${WORKSPACE}`)).toHaveCount(1);
  });

  test('field types and autocomplete tokens are preserved', async ({ page }) => {
    await expect(page.locator(EMAIL)).toHaveAttribute('type', 'email');
    await expect(page.locator(EMAIL)).toHaveAttribute('autocomplete', 'email');

    await expect(page.locator(PASSWORD)).toHaveAttribute('type', 'password');
    await expect(page.locator(PASSWORD)).toHaveAttribute('autocomplete', 'current-password');

    // The workspace/tenant-slug field. `organization` is the contract; the input type itself is
    // free to change (text, search, a combobox input) as long as the token holds.
    await expect(page.locator(WORKSPACE)).toHaveAttribute('autocomplete', 'organization');
  });

  test('every credential field has a non-empty accessible name', async ({ page }) => {
    for (const selector of [EMAIL, PASSWORD, WORKSPACE]) {
      const name = await accessibleName(page.locator(selector));
      const technique = await namingTechnique(page, selector);
      expect(name, `${selector} has no accessible name (naming technique: ${technique})`).not.toBe('');

      // NOT a failure, but recorded on every run: a placeholder is a legitimate accname fallback
      // yet it disappears the moment the user types, and screen-reader support for it is uneven.
      // The rebuild should upgrade these to a real <label for> / aria-labelledby.
      if (technique === 'placeholder-only' || technique === 'title') {
        test.info().annotations.push({
          type: 'a11y-debt',
          description:
            `${selector} is named only by its ${technique} ("${name}"). Valid, but weak — ` +
            `prefer <label for> or aria-labelledby in the rebuild.`,
        });
      }
    }
  });

  test('email → password → workspace → submit are reachable by keyboard in that order', async ({ page }) => {
    const email = page.locator(EMAIL);
    const submit = submitControl(page);
    await expect(submit, 'a submit control must exist').toHaveCount(1);

    await email.focus();
    expect(await isFocused(email), '#li-em must be focusable').toBe(true);

    // Walk forward from the email field and record what focus lands on. Interleaved controls (a
    // "forgot password" link between the fields, a reveal toggle, a locale switcher) are fine —
    // only the RELATIVE order of the four contract controls is asserted.
    const order: string[] = ['li-em'];
    for (let step = 0; step < 20; step += 1) {
      await page.keyboard.press('Tab');
      if (await isFocused(submit)) { order.push('submit'); break; }
      const id = await page.evaluate(() => (document.activeElement as HTMLElement | null)?.id ?? '');
      if (id) order.push(id);
    }

    const seen = (token: string): number => order.indexOf(token);
    expect(seen('li-pw'), `#li-pw was never reached by Tab. Focus order: ${order.join(' → ')}`).toBeGreaterThan(-1);
    expect(seen('li-ws'), `#li-ws was never reached by Tab. Focus order: ${order.join(' → ')}`).toBeGreaterThan(-1);
    expect(seen('submit'), `the submit control was never reached by Tab. Focus order: ${order.join(' → ')}`).toBeGreaterThan(-1);
    expect(seen('li-pw'), `#li-pw must follow #li-em. Focus order: ${order.join(' → ')}`).toBeGreaterThan(seen('li-em'));
    expect(seen('li-ws'), `#li-ws must follow #li-pw. Focus order: ${order.join(' → ')}`).toBeGreaterThan(seen('li-pw'));
    expect(seen('submit'), `submit must follow #li-ws. Focus order: ${order.join(' → ')}`).toBeGreaterThan(seen('li-ws'));
  });

  test('the password reveal toggle flips #li-pw between password and text', async ({ page }) => {
    const password = page.locator(PASSWORD);
    const form = loginForm(page);
    // Any of the three sane implementations: a button, a checkbox, or a switch — identified by its
    // accessible name, plus an optional stable hook the rebuild may add.
    const toggle = form
      .getByTestId('login-password-toggle')
      .or(form.getByRole('button', { name: /show password|hide password|reveal|toggle password/i }))
      .or(form.getByRole('checkbox', { name: /show password|hide password|reveal/i }))
      .or(form.getByRole('switch', { name: /show password|hide password|reveal/i }))
      .first();

    await expect(toggle, 'the password reveal control must exist').toHaveCount(1);
    await expect(password).toHaveAttribute('type', 'password');

    await toggle.click();
    await expect(password, 'revealing must change #li-pw to type=text').toHaveAttribute('type', 'text');

    await toggle.click();
    await expect(password, 're-hiding must return #li-pw to type=password').toHaveAttribute('type', 'password');
  });

  test('"forgot password" reaches the forgot state and the back control returns to login', async ({ page }) => {
    const forgot = page
      .getByTestId('login-forgot')
      .or(page.getByRole('button', { name: /forgot|reset your password|trouble signing in/i }))
      .or(page.getByRole('link', { name: /forgot|reset your password|trouble signing in/i }))
      .first();
    await expect(forgot, 'a forgot-password control must exist').toHaveCount(1);

    await forgot.click();

    // The forgot state is identified structurally: the credential fields of the LOGIN state are
    // gone. No assertion on headings, icons or copy.
    await expect(page.locator(PASSWORD), 'the forgot state must not show the login password field')
      .toBeHidden({ timeout: 10_000 });
    await expect(page.locator(WORKSPACE + ':visible'), 'the login workspace field must leave with it')
      .toHaveCount(0);
    // …and the shell survives the state change.
    await expect(page.locator(SHELL)).toHaveCount(1);

    const back = page
      .getByTestId('login-back')
      .or(page.getByRole('button', { name: /back|return|cancel/i }))
      .or(page.getByRole('link', { name: /back|return|cancel/i }))
      .first();
    await expect(back, 'the forgot state must offer a way back to sign-in').toHaveCount(1);

    await back.click();

    await expect(page.locator(EMAIL)).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(PASSWORD)).toBeVisible();
    await expect(page.locator(WORKSPACE)).toBeVisible();
  });

  /**
   * The sign-in card must come to rest in BOUNDED WALL TIME after the pointer moves.
   *
   * The aurora scene applies a parallax to `.lx-pane` from inside its rAF loop, easing the
   * pointer signal toward its target. That ease used to close a fixed fraction per FRAME, so
   * it took a fixed ~68 frames to settle and its DURATION was whatever the machine's frame
   * interval happened to be. The scene is fill-rate-bound WebGL: with no GPU — CI's
   * SwiftShader, a VM, a Chrome install with the GPU blocklisted, which is the hardware a
   * customer is most likely to be on — it draws at 4-10fps, and 68 frames became 7-25
   * SECONDS of the credential card sliding under the cursor.
   *
   * That is what timed out this file and e2e/tenant-auth.spec.ts in the Browser Pilot lane:
   * a click cannot be delivered to a control that is still moving. It is a customer-facing
   * defect first and a test failure second.
   *
   * The budget is deliberately expressed against the MEASURED frame interval rather than as
   * a flat number of milliseconds, because that is the difference the bug is made of:
   *
   *   - a wall-time-bounded ease costs ~1.6s plus a few frames of granularity, at ANY rate;
   *   - a frame-bounded one costs ~68 frames, so its cost IS the frame interval times 68.
   *
   * `SETTLE_FIXED_MS + SETTLE_FRAMES × interval` sits above the first and under the second
   * at every interval slow enough for the defect to reach a customer. A flat budget cannot:
   * make it loose enough for a 700ms/frame machine and it stops catching a 100ms/frame one.
   *
   * On a developer machine with real GPU acceleration the scene runs at 60fps, 68 frames is
   * ~1.1s, and there is no defect to find — this passes either way. CI has no GPU, so the
   * lane that gates the release is the one that always exercises it. It does NOT assert a
   * frame rate, a duration a designer may retune, or that the scene exists at all.
   */
  test('the sign-in card stops moving within a bounded time, whatever the frame rate', async ({ page }) => {
    /** The wall-clock part of the ease: what it costs when frames are free. */
    const SETTLE_FIXED_MS = 2_500;
    /** Frames of slack for granularity — the final step, plus the two identical frames an
     *  actionability check needs to see before it will call the element stable. */
    const SETTLE_FRAMES = 5;
    /** Hard stop on the measurement itself, well inside the 30s test timeout so a failure
     *  reports these numbers instead of dying as an unexplained timeout. */
    const MEASURE_CAP_MS = 20_000;

    // The scene is code-split and never server-rendered, so it is not driving the pane the
    // instant the form is visible. Measuring before its first draw would compare '' to ''
    // and call that settled. Wait for it to actually own the transform.
    //
    // Below SCENE_MIN_WIDTH, or once the frame-budget watchdog has retired it, the scene is
    // gone and the pane is never transformed at all — which is the outcome this test wants,
    // so there is nothing left to assert. That is a pass, not a skip.
    const driving = await page
      .waitForFunction(
        () => {
          const el = document.querySelector('.lx-pane') as HTMLElement | null;
          return !!el && el.style.transform !== '';
        },
        null,
        { timeout: 15_000 },
      )
      .then(() => true)
      .catch(() => false);
    if (!driving) return;

    const submit = submitControl(page);
    const box = await submit.boundingBox();
    expect(box, 'the submit control must have a box to aim at').not.toBeNull();
    // Exactly what click() does first: put the pointer on the control. That is the input the
    // parallax responds to, and the moment from which the customer is waiting.
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);

    const settle = await page.evaluate(async (budget: number) => {
      const el = document.querySelector('.lx-pane') as HTMLElement | null;
      if (!el) return { settled: true, ms: 0, frames: 0, meanFrameMs: 0 };
      const t0 = performance.now();
      let frames = 0, lastChangeMs = 0, run = 0, last = el.style.transform;
      return await new Promise<{ settled: boolean; ms: number; frames: number; meanFrameMs: number }>((resolve) => {
        const tick = (): void => {
          frames += 1;
          const now = performance.now();
          const t = el.style.transform;
          if (t !== last) { last = t; lastChangeMs = now - t0; run = 0; } else run += 1;
          // Two consecutive identical frames is the same condition a click's actionability
          // check waits on before it will dispatch.
          if (run >= 2) {
            resolve({ settled: true, ms: Math.round(lastChangeMs), frames, meanFrameMs: Math.round((now - t0) / frames) });
          } else if (now - t0 > budget) {
            resolve({ settled: false, ms: Math.round(now - t0), frames, meanFrameMs: Math.round((now - t0) / frames) });
          } else requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      });
    }, MEASURE_CAP_MS);

    const budget = SETTLE_FIXED_MS + SETTLE_FRAMES * settle.meanFrameMs;
    const detail =
      `.lx-pane took ${settle.settled ? `${settle.ms}ms` : `over ${settle.ms}ms`} to come to rest `
      + `after the pointer stopped — ${settle.frames} frames at ~${settle.meanFrameMs}ms/frame, `
      + `against a budget of ${Math.round(budget)}ms. The parallax ease must be frame-rate `
      + 'INDEPENDENT. If the settle is costing a fixed number of frames again, then every '
      + 'machine that cannot draw this scene quickly — which is most of them, with no GPU — '
      + 'holds the customer in front of a sign-in card that is still moving, for proportionally '
      + 'longer the slower it is.';

    expect(settle.settled, detail).toBe(true);
    expect(settle.ms, detail).toBeLessThan(budget);
  });

  test('submitting shows a busy/disabled state, then releases it', async ({ page }) => {
    // Hold the login response open so the in-flight state is observable, and answer it ourselves:
    // no real credentials are spent and the API's 10-per-60s login limiter is untouched.
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => { release = resolve; });
    await page.route('**/api/auth/login**', async (route) => {
      await held;
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'Invalid credentials.' }),
      });
    });

    await page.locator(EMAIL).fill('contract-probe@example.com');
    await page.locator(PASSWORD).fill('not-a-real-password');
    await page.locator(WORKSPACE).fill('contract-probe');

    const submit = submitControl(page);
    await expect(submit).toBeEnabled();
    await submit.click();

    // Busy may be expressed as `disabled`, `aria-disabled` or `aria-busy` — all three are valid,
    // so the redesign is free to keep the button enabled and merely mark it busy.
    await expect
      .poll(
        async () => {
          const [disabled, ariaDisabled, ariaBusy] = await Promise.all([
            submit.isDisabled(),
            submit.getAttribute('aria-disabled'),
            submit.getAttribute('aria-busy'),
          ]);
          return disabled || ariaDisabled === 'true' || ariaBusy === 'true';
        },
        {
          timeout: 5_000,
          message: 'submit must signal in-flight work (disabled / aria-disabled / aria-busy) — ' +
            'without it, an impatient user double-submits the login',
        },
      )
      .toBe(true);

    release();

    // A rejected sign-in must hand the form back. A spinner that never clears is the same outage
    // to a customer as a 500.
    await expect(submit, 'the submit control must recover after the request settles')
      .toBeEnabled({ timeout: 10_000 });
    await expect(page.locator(EMAIL)).toBeVisible();
  });
});
