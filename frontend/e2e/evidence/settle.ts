/**
 * SETTLED-STATE DETECTION — the half of the evidence harness that decides when a screen may be
 * photographed.
 *
 * Register item F12 records four ways an evidence bundle has lied. The first is the one this file
 * exists for: a screenshot of a LOADING SPINNER, filed as the completed transaction. It is an easy
 * lie to tell by accident, because `await button.click()` resolves the instant the click is
 * dispatched and `page.screenshot()` on the next line photographs whatever is on screen — which,
 * for every mutation in this product, is the spinner the click just started.
 *
 * So the harness never screenshots on demand. It screenshots when the page is SETTLED, and a page
 * is settled only when all three of these hold at once, continuously, for `quietMs`:
 *
 *   1. no request this page started is still in flight;
 *   2. no loading indicator is visible in the DOM (the product's own spinner/skeleton/aria-busy
 *      vocabulary — see LOADING_SELECTOR, plus a disabled "…" button, which is how the payroll
 *      module renders "Validating…", "Processing…", "Generating…");
 *   3. the rendered text of the region being captured has stopped changing.
 *
 * A page that never reaches that state does NOT produce a screenshot taken anyway. It throws, and
 * the step is recorded as failed with the reason — which requests were still open, which indicator
 * was still on screen. "It never settled" is a finding; a picture of it settling is a fiction.
 *
 * `attach()` must be called once per page before `settle()`; the evidence recorder does that for
 * every page handed to it, so specs never call it directly.
 */
import type { Page, Request } from '@playwright/test';

/**
 * The product's loading vocabulary. Tailwind spinner/skeleton classes plus the two ARIA signals.
 * `.animate-spin` is what PayrollPage renders while a tab's fetch is outstanding; `.animate-pulse`
 * is the skeleton the insight strip shows; `[aria-busy="true"]` is ModuleGate and the export panel.
 */
export const LOADING_SELECTOR =
  '[aria-busy="true"], [role="progressbar"], .animate-spin, .animate-pulse, [data-loading="true"]';

export interface SettleOptions {
  /** How long every condition must hold continuously. */
  quietMs?: number;
  /** How long to wait for that to happen before refusing. */
  timeoutMs?: number;
  /** The region whose text must stop changing. Defaults to `main`, falling back to `body`. */
  region?: string;
}

export interface SettleReport {
  /** Milliseconds from the call to the moment the page was continuously settled. */
  settledAfterMs: number;
  /** Requests that were in flight at some point during the wait, for the manifest. */
  waitedOnRequests: string[];
  /** Loading indicators that were visible at some point during the wait. */
  waitedOnIndicators: string[];
}

interface PageState {
  inflight: Map<Request, { url: string; startedAt: number }>;
}

const STATE = new WeakMap<Page, PageState>();

/** Requests that legitimately never finish and must not hold a capture hostage. */
const IGNORED_RESOURCE_TYPES = new Set(['websocket', 'eventsource', 'ping', 'beacon']);

/**
 * Start counting this page's in-flight requests. Idempotent: attaching twice would double-count
 * every request and no page would ever look quiet.
 */
export function attach(page: Page): void {
  if (STATE.has(page)) return;
  const state: PageState = { inflight: new Map() };
  STATE.set(page, state);

  page.on('request', (request) => {
    if (IGNORED_RESOURCE_TYPES.has(request.resourceType())) return;
    state.inflight.set(request, { url: request.url(), startedAt: Date.now() });
  });
  const done = (request: Request) => state.inflight.delete(request);
  page.on('requestfinished', done);
  page.on('requestfailed', done);
}

/** In-flight request URLs right now, newest last. Empty when the page is network-quiet. */
export function inflight(page: Page): string[] {
  const state = STATE.get(page);
  if (!state) return [];
  return [...state.inflight.values()].map((r) => r.url);
}

/** Loading indicators currently VISIBLE, described for a human. Runs entirely in the page. */
async function visibleIndicators(page: Page, selector: string): Promise<string[]> {
  return page.evaluate((sel) => {
    const visible = (el: Element): boolean => {
      const rects = el.getClientRects();
      if (rects.length === 0) return false;
      const style = window.getComputedStyle(el);
      return style.visibility !== 'hidden' && style.display !== 'none' && style.opacity !== '0';
    };
    const describe = (el: Element): string => {
      const cls = (el.getAttribute('class') ?? '').split(/\s+/).filter((c) => /animate-|spin|pulse/.test(c));
      const aria = el.getAttribute('aria-label') ?? el.getAttribute('aria-busy');
      return `${el.tagName.toLowerCase()}${cls.length ? `.${cls.join('.')}` : ''}${aria ? `[${aria}]` : ''}`;
    };
    const found = [...document.querySelectorAll(sel)].filter(visible).map(describe);
    // "Validating…", "Processing…", "Generating…" — the module disables the button while the work
    // runs and restores its label afterwards, so a DISABLED button whose label ends in an ellipsis
    // is a mutation still in progress. An enabled "Override…" opens a dialog and is not one.
    for (const button of document.querySelectorAll('button')) {
      if (!(button as HTMLButtonElement).disabled) continue;
      const text = (button.textContent ?? '').trim();
      if (/[…]$|\.\.\.$/.test(text) && visible(button)) found.push(`button[disabled]:"${text}"`);
    }
    return [...new Set(found)];
  }, selector);
}

/** Visible text of the region being captured, used only to notice that it is still changing. */
async function regionText(page: Page, region: string): Promise<string> {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel) ?? document.body;
    return (el as HTMLElement).innerText ?? '';
  }, region);
}

/**
 * Wait until the page is settled, or throw naming exactly what never finished.
 *
 * Deliberately has NO "capture anyway" path and no caller-supplied escape hatch: the whole point of
 * the harness is that an unsettled screen produces a failure, not a photograph.
 */
export async function settle(page: Page, options: SettleOptions = {}): Promise<SettleReport> {
  const quietMs = options.quietMs ?? 400;
  const timeoutMs = options.timeoutMs ?? 30_000;
  const region = options.region ?? 'main';
  if (!STATE.has(page)) {
    throw new Error(
      '[evidence] settle() was called on a page the harness is not attached to, so its in-flight '
      + 'requests were never counted and "settled" could not be established. Hand every page to the '
      + 'recorder (it attaches on first use) instead of screenshotting a page it has never seen.',
    );
  }

  const started = Date.now();
  const deadline = started + timeoutMs;
  const waitedOnRequests = new Set<string>();
  const waitedOnIndicators = new Set<string>();
  let quietSince: number | null = null;
  let lastText: string | null = null;
  let lastReason = 'never evaluated';

  for (;;) {
    const open = inflight(page);
    const indicators = await visibleIndicators(page, LOADING_SELECTOR);
    const text = await regionText(page, region);
    open.forEach((u) => waitedOnRequests.add(u));
    indicators.forEach((i) => waitedOnIndicators.add(i));

    const textStable = lastText !== null && text === lastText;
    lastText = text;

    if (open.length === 0 && indicators.length === 0 && textStable) {
      quietSince ??= Date.now();
      if (Date.now() - quietSince >= quietMs) {
        return {
          settledAfterMs: Date.now() - started,
          waitedOnRequests: [...waitedOnRequests],
          waitedOnIndicators: [...waitedOnIndicators],
        };
      }
    } else {
      quietSince = null;
      lastReason = open.length
        ? `${open.length} request(s) still in flight: ${open.slice(0, 4).join(', ')}`
        : indicators.length
          ? `loading indicator(s) still visible: ${indicators.slice(0, 4).join(', ')}`
          : `the ${region} region's text is still changing`;
    }

    if (Date.now() >= deadline) {
      throw new Error(
        `[evidence] ${page.url()} never settled within ${timeoutMs}ms — ${lastReason}.\n`
        + 'REFUSING TO CAPTURE. A screenshot taken now would show work in progress and be filed as a '
        + 'completed transaction, which is the exact failure this harness exists to prevent. '
        + 'Treat this as a finding about the screen, not as a harness timeout to raise.',
      );
    }
    await page.waitForTimeout(120);
  }
}
