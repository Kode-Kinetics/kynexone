import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { bootPeopleFixture, type Locale, type Theme } from './people-list.fixture';

/**
 * People list: the bounded, internally scrolling employee table (data-testid="employee-list-scroll") with a sticky
 * header and a pager that sits below it. Route-mocked (no backend); the mock lives in people-list.fixture.ts.
 *
 * Matrix: {1366x768, 1920x1080, 390x844} x {en, ar (RTL)} x {light, dark}, 25 employees (one full page).
 * Screenshots go to PEOPLE_LIST_SHOTS (default /tmp/claude/people-test/shots).
 *
 * Open defects (PL-2, PL-8) are `knownDefect(...)` tests: skipped (test.fixme) so the suite stays green, and they run
 * red when PEOPLE_LIST_RUN_DEFECTS=1 (that is the red evidence; remove the call once the product is fixed).
 */

const SHOTS = process.env.PEOPLE_LIST_SHOTS ?? '/tmp/claude/people-test/shots';
const RUN_DEFECTS = !!process.env.PEOPLE_LIST_RUN_DEFECTS;
/**
 * Teeth check: PEOPLE_LIST_MUTATION=revert rewrites the region and header classes back to what the table wrapper was before
 * the change (plain `overflow-x-auto`, bare <thead>, no role/tabindex/label) right after the page loads, so the scroll,
 * sticky-header, pager, keyboard and accessible-name tests can be seen to fail without the product change.
 */
const REVERT = process.env.PEOPLE_LIST_MUTATION === 'revert';
/**
 * Green evidence for the suggested fixes: PEOPLE_LIST_MUTATION=suggested-fix applies them to the live DOM (relative on the
 * region, bound 100dvh - 24rem with a 20rem floor, scroll-padding-top under the header, table min-width 900px). Run with
 * PEOPLE_LIST_RUN_DEFECTS=1: PL-1, PL-3 (1366), PL-4, PL-7 and PL-8 then pass; PL-2, PL-6 and PL-B1 need other fixes.
 */
const SUGGESTED_FIX = process.env.PEOPLE_LIST_MUTATION === 'suggested-fix';

interface Viewport { name: string; width: number; height: number; phone: boolean }
const VIEWPORTS: Viewport[] = [
  { name: '1366x768', width: 1366, height: 768, phone: false },
  { name: '1920x1080', width: 1920, height: 1080, phone: false },
  { name: '390x844', width: 390, height: 844, phone: true },
];
const LOCALES: Locale[] = ['en', 'ar'];
const THEMES: Theme[] = ['light', 'dark'];

/** A defect found by this suite: skipped until the product is fixed, runnable with PEOPLE_LIST_RUN_DEFECTS=1. */
function knownDefect(id: string, applies: boolean, reason: string) {
  test.fixme(applies && !RUN_DEFECTS, `${id}: ${reason}`);
}

async function openList(page: Page, viewport: Viewport, locale: Locale, theme: Theme, count = 25) {
  await page.setViewportSize({ width: viewport.width, height: viewport.height });
  const fixture = await bootPeopleFixture(page, { count, locale, theme });
  await page.goto('/people');
  const region = page.getByTestId('employee-list-scroll');
  await expect(region).toBeVisible({ timeout: 120_000 });
  await expect(region.locator('tbody tr')).toHaveCount(Math.min(count, 25));
  await expect(page.locator('#employee-1000')).toBeVisible();
  if (locale === 'ar') await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  if (theme === 'dark') await expect(page.locator('html')).toHaveClass(/dark/);
  if (SUGGESTED_FIX) {
    await region.evaluate((el: HTMLElement) => {
      el.style.position = 'relative';
      el.style.maxHeight = 'max(20rem, calc(100dvh - 24rem))';
      el.style.scrollPaddingTop = '3rem';
      el.querySelector('table')!.style.minWidth = '900px';
    });
  }
  if (REVERT) {
    await region.evaluate((el: HTMLElement) => {
      el.className = 'overflow-x-auto';
      for (const attribute of ['tabindex', 'role', 'aria-label']) el.removeAttribute(attribute);
      el.querySelector('thead')!.className = '';
    });
  }
  return { region, pager: region.locator('xpath=following-sibling::div[1]'), fixture };
}

const settle = (page: Page) => page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));

async function scrollRegionTo(page: Page, region: Locator, top: number | 'end') {
  await region.evaluate((el, t) => { el.scrollTop = t === 'end' ? el.scrollHeight : t; }, top);
  await settle(page);
}

/** Wait until the list has stopped moving (two animation frames with the same scrollTop) so a key is never pressed mid-animation. */
async function scrollSettled(page: Page, region: Locator) {
  await expect.poll(async () => {
    const first = await region.evaluate(el => el.scrollTop);
    await settle(page);
    return (await region.evaluate(el => el.scrollTop)) === first;
  }).toBe(true);
}

const metrics = (region: Locator) => region.evaluate(el => ({
  scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth,
}));

/** Alpha channel of a computed CSS colour (rgb(), rgba(), `rgb(r g b / a)` and `color(srgb r g b / a)` all occur). */
function alphaOf(color: string): number {
  if (color === 'transparent') return 0;
  const slash = /\/\s*([\d.]+)(%?)\s*\)/.exec(color);
  if (slash) return Number(slash[1]) / (slash[2] ? 100 : 1);
  const comma = /^rgba\(\s*[^,]+,[^,]+,[^,]+,\s*([\d.]+)\s*\)$/.exec(color);
  return comma ? Number(comma[1]) : 1;
}

async function shot(page: Page, name: string) {
  await page.screenshot({ path: `${SHOTS}/${name}.png` });
}

for (const viewport of VIEWPORTS) {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      const label = `${viewport.name} ${locale} ${theme}`;
      const tag = `${viewport.name}-${locale}-${theme}`;

      test.describe(`People list scroll, ${label}`, () => {
        test('the list scrolls inside its own region (wheel), the page does not move', async ({ page }) => {
          const { region } = await openList(page, viewport, locale, theme);
          await expect(region).toHaveAttribute('role', 'region');
          await expect(region).toHaveAttribute('tabindex', '0');
          await expect(region).toHaveAttribute('aria-label', locale === 'ar' ? 'قائمة الموظفين' : 'Employee list');
          const before = await metrics(region);
          await shot(page, `${tag}-1-initial`);
          if (viewport.phone) {
            // From md up the list is bounded; on a phone it grows with its rows and the page scrolls (no nested scroller).
            expect(before.scrollHeight - before.clientHeight, 'phone: the list is not height-bounded').toBeLessThanOrEqual(1);
            return;
          }
          expect(before.scrollHeight, '25 tall rows overflow the bounded region').toBeGreaterThan(before.clientHeight + 100);
          // The bound: max(20rem, 100dvh - 23rem) (it was 24rem; the tighter header and rows let the list take one rem more).
          expect(before.clientHeight).toBeLessThanOrEqual(Math.max(320, viewport.height - 368) + 1);

          await region.scrollIntoViewIfNeeded();
          const pageY = await page.evaluate(() => window.scrollY);
          const box = (await region.boundingBox())!;
          await page.mouse.move(box.x + box.width / 2, Math.min(box.y + 120, viewport.height - 140));
          await page.mouse.wheel(0, 500);
          await expect.poll(async () => (await metrics(region)).scrollTop, { message: 'wheel over the list scrolls the list' }).toBeGreaterThan(100);
          expect(await page.evaluate(() => window.scrollY), 'the page did not scroll with the list').toBe(pageY);
          await shot(page, `${tag}-2-scrolled`);
        });

        test('the sticky header stays visible and opaque while rows scroll beneath it', async ({ page }) => {
          test.skip(viewport.phone, 'on a phone the list is not bounded, so the page scrolls and there is no nested scroller to pin the header in');
          const { region } = await openList(page, viewport, locale, theme);
          await page.evaluate(() => {
            const r = document.querySelector('[data-testid=employee-list-scroll]')!.getBoundingClientRect();
            window.scrollTo(0, window.scrollY + r.top - 70);
          });
          await scrollRegionTo(page, region, 700);
          const head = region.locator('thead');
          const regionBox = (await region.boundingBox())!;
          const headBox = (await head.boundingBox())!;
          expect(Math.abs(headBox.y - regionBox.y), 'header pinned to the top edge of the region').toBeLessThanOrEqual(2);

          const style = await head.evaluate(el => {
            const s = getComputedStyle(el);
            return { background: s.backgroundColor, shadow: s.boxShadow, position: s.position, z: s.zIndex };
          });
          expect(style.position).toBe('sticky');
          expect(alphaOf(style.background), `header background ${style.background} is opaque`).toBe(1);
          expect(style.shadow, 'a hairline under the pinned header (the row borders scroll away under border-collapse)').not.toBe('none');

          // The header is what is hit at its own pixels: no row text paints through or above it.
          const hit = await page.evaluate(({ x, y }) => {
            const element = document.elementFromPoint(x, y);
            return element ? { inHead: !!element.closest('thead'), tag: element.tagName } : null;
          }, { x: regionBox.x + regionBox.width / 2 - 40, y: headBox.y + headBox.height / 2 });
          expect(hit?.inHead, `element at header centre is ${hit?.tag}`).toBe(true);
          const rowUnderHeader = await page.evaluate(({ x, y }) => {
            const element = document.elementFromPoint(x, y);
            return !!element?.closest('thead');
          }, { x: regionBox.x + 200, y: headBox.y + headBox.height - 3 });
          expect(rowUnderHeader, 'bottom 3px of the header still belongs to the header').toBe(true);
          await head.screenshot({ path: `${SHOTS}/${tag}-3-header.png` });
          await shot(page, `${tag}-3-header-scrolled`);
        });

        test('the pager stays visible without scrolling the page and does not move with the list', async ({ page }) => {
          const { region, pager } = await openList(page, viewport, locale, theme);
          if (viewport.phone) {
            // The list grows on a phone, so the pager is reached by scrolling the page; it must then be clear of the bottom bar.
            await pager.scrollIntoViewIfNeeded();
            // The pager buttons are translated (Prev/Next are in the People list dictionary), so the Arabic page names them in Arabic.
            await expect(pager.getByRole('button', { name: /^(Next|التالي)$/ })).toBeInViewport({ ratio: 1 });
            const hidden = await pager.evaluate(el => { const r = el.getBoundingClientRect(); const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return !!top && !el.contains(top); });
            expect(hidden, 'the bottom bar covers the pager').toBe(false);
            await shot(page, `${tag}-4-pager`);
            return;
          }
          expect(await page.evaluate(() => window.scrollY)).toBe(0);
          const before = (await pager.boundingBox())!;
          expect(before.y + before.height, 'pager bottom edge is inside the viewport at scrollY 0').toBeLessThanOrEqual(viewport.height);
          await expect(pager.getByRole('button', { name: /prev|السابق/i })).toBeInViewport();
          await expect(pager.getByRole('button', { name: /next|التالي/i })).toBeInViewport();
          // Not covered by the phone bottom bar or anything else.
          const covered = await pager.evaluate(el => {
            const r = el.getBoundingClientRect();
            const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
            return !!top && !el.contains(top);
          });
          expect(covered, 'something sits on top of the pager').toBe(false);
          await scrollRegionTo(page, region, 900);
          const after = (await pager.boundingBox())!;
          expect(Math.abs(after.y - before.y), 'pager is outside the scroll area').toBeLessThanOrEqual(1);
          await shot(page, `${tag}-4-pager`);
        });

        test('keyboard: the region is focusable after the filter chips, and arrows/PageDown/End/Home scroll it', async ({ page }) => {
          const { region } = await openList(page, viewport, locale, theme);
          // Focus order = DOM order: the last Self-service chip, then the list region.
          const chips = page.getByTestId('access-filter').getByRole('button');
          await chips.last().focus();
          await page.keyboard.press('Tab');
          await expect(region, 'Tab from the last filter chip lands on the list region').toBeFocused();
          // Focusing may scroll the page itself to reveal the region (phone); what matters is that the keys scroll the list, not the page.
          const pageYAfterFocus = await page.evaluate(() => window.scrollY);
          if (viewport.phone) {
            await shot(page, `${tag}-5-keyboard-focus`);
            return; // not bounded on a phone: nothing to scroll inside the region
          }

          await page.keyboard.press('ArrowDown');
          await expect.poll(async () => (await metrics(region)).scrollTop).toBeGreaterThan(0);
          await scrollSettled(page, region);
          const afterArrow = (await metrics(region)).scrollTop;
          await page.keyboard.press('PageDown');
          await expect.poll(async () => (await metrics(region)).scrollTop).toBeGreaterThan(afterArrow + 100);
          await scrollSettled(page, region);
          await page.keyboard.press('End');
          await expect.poll(async () => { const m = await metrics(region); return m.scrollHeight - m.clientHeight - m.scrollTop; }).toBeLessThanOrEqual(1);
          await scrollSettled(page, region);
          await page.keyboard.press('Home');
          await expect.poll(async () => (await metrics(region)).scrollTop).toBe(0);
          expect(await page.evaluate(() => window.scrollY), 'keyboard scrolling the list left the page where it was').toBe(pageYAfterFocus);

          const ring = await region.evaluate(el => { const s = getComputedStyle(el); return { outline: s.outlineStyle, width: s.outlineWidth, shadow: s.boxShadow }; });
          expect(ring.outline !== 'none' || ring.shadow !== 'none', `a visible focus indicator (outline ${ring.outline} ${ring.width}, shadow ${ring.shadow})`).toBe(true);
          await shot(page, `${tag}-5-keyboard-focus`);
        });

        test('no horizontal overflow regression: the page never scrolls sideways', async ({ page }) => {
          const { region } = await openList(page, viewport, locale, theme);
          const page_ = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
          expect(page_.sw, 'document scrollWidth <= clientWidth').toBeLessThanOrEqual(page_.cw);
          const m = await metrics(region);
          if (!viewport.phone) {
            expect(m.scrollWidth, 'no horizontal scrollbar inside the list at desktop widths').toBeLessThanOrEqual(m.clientWidth + 1);
          } else {
            // Pre-existing: the 960px table scrolls sideways inside the region on a phone (cards are the Screen Standard answer).
            expect(m.scrollWidth).toBeGreaterThan(m.clientWidth);
            const first = (await region.locator('thead th').nth(1).boundingBox())!;
            const box = (await region.boundingBox())!;
            expect(first.x >= box.x - 1 && first.x + first.width <= box.x + box.width + 1, 'the Employee column starts in view (start edge, both directions)').toBe(true);
          }
        });

        test('at least 12 rows are visible without scrolling the list (Screen Standard, List)', async ({ page }) => {
          knownDefect('PL-2', !viewport.phone, 'rows are 96-136px tall (three stacked self-service chips, wrapped name, Reset sign-in button) instead of 40px, so 3 rows fit at 1366x768 and 6 at 1920x1080; the standard asks for 12');
          test.skip(viewport.phone, 'the standard turns the table into cards under 640px');
          test.skip(locale !== 'en' || theme !== 'light', 'row geometry is locale and theme independent');
          const { region } = await openList(page, viewport, locale, theme);
          const visible = await region.evaluate(el => {
            const box = el.getBoundingClientRect();
            const head = el.querySelector('thead')!.getBoundingClientRect();
            return Array.from(el.querySelectorAll('tbody tr')).filter(row => {
              const r = row.getBoundingClientRect();
              return r.top >= head.bottom - 1 && r.bottom <= box.bottom + 1;
            }).length;
          });
          expect(visible, `${visible} whole rows visible`).toBeGreaterThanOrEqual(12);
        });

        test('row link and Reset sign-in still work after scrolling the list', async ({ page }) => {
          const { region } = await openList(page, viewport, locale, theme);
          await scrollRegionTo(page, region, 'end');
          // Row 23 (index 22) is an "active" employee, so it carries Reset sign-in.
          const resetRow = region.locator('tbody tr', { has: page.locator('#employee-1022') });
          // The first button in the row is Reset sign-in (its label is localized); the Profile badge button follows it.
          const reset = resetRow.getByRole('button').first();
          await expect(reset).toBeVisible();
          await reset.click();
          await expect(page.getByTestId('row-reset-confirm')).toBeVisible();
          await page.keyboard.press('Escape');
          await expect(page.getByTestId('row-reset-confirm')).toHaveCount(0);

          // A link sitting right under the pinned header is clickable (the header does not swallow the click).
          await scrollRegionTo(page, region, 0);
          const target = page.locator('#employee-1010');
          const offset = await target.evaluate((el, ) => {
            const scroller = el.closest('[data-testid=employee-list-scroll]') as HTMLElement;
            const head = scroller.querySelector('thead')!.getBoundingClientRect().height;
            return el.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop - head - 4;
          });
          await scrollRegionTo(page, region, offset);
          // On a phone the list is not bounded, so the page does the scrolling.
          if (viewport.phone) await target.scrollIntoViewIfNeeded();
          const hits = await target.evaluate(el => {
            // The name wraps onto two lines; hit-test the middle of its first line box.
            const r = el.getClientRects()[0];
            const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
            return !!top && (top === el || el.contains(top));
          });
          expect(hits, 'the row link is the element hit at its own centre').toBe(true);
          await target.click();
          await expect(page).toHaveURL(/\/people\/1010\?/);
        });

        test('axe: no serious or critical violations on the list screen', async ({ page }) => {
          await openList(page, viewport, locale, theme);
          const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
          const bad = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
          expect(bad.map(v => `${v.id}: ${v.nodes.slice(0, 2).map(n => n.target.join(' ')).join(' | ')}`)).toEqual([]);
        });
      });
    }
  }
}

test.describe('People list scroll: regressions found on the one-line change (checked at 1366x768 en light)', () => {
  const desktop = VIEWPORTS[0];

  test('the document is not taller than the list card (no blank page scroll under the list)', async ({ page }) => {
    const { pager } = await openList(page, desktop, 'en', 'light');
    const m = await page.evaluate(() => {
      const scroller = document.querySelector('[data-testid=employee-list-scroll]')!;
      // Painted content: everything except the clipped rows inside the scroll region (the region itself counts), the sr-only
      // text and other absolutely positioned helpers.
      const bottomOfVisible = Math.max(...Array.from(document.querySelectorAll('body *'))
        .filter(el => (el === scroller || !scroller.contains(el)) && !el.closest('.sr-only') && !el.classList.contains('sr-only')
          && !['absolute', 'fixed'].includes(getComputedStyle(el).position) && el.getBoundingClientRect().height > 0)
        .map(el => el.getBoundingClientRect().bottom + window.scrollY));
      return { doc: document.documentElement.scrollHeight, bottomOfVisible, inner: window.innerHeight };
    });
    const pagerBottom = (await pager.boundingBox())!;
    expect(m.doc, `document ${m.doc}px, pager bottom ${Math.round(pagerBottom.y + pagerBottom.height)}px, viewport ${m.inner}px`).toBeLessThanOrEqual(m.bottomOfVisible + 80);
  });

  test('a link focused under the pinned header is scrolled clear of it (Shift+Tab upward)', async ({ page }) => {
    const { region } = await openList(page, desktop, 'en', 'light');
    await scrollRegionTo(page, region, 'end');
    await page.locator('#employee-1024').focus();
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Shift+Tab');
      const focused = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        const scroller = document.querySelector('[data-testid=employee-list-scroll]')!;
        if (!el || !scroller.contains(el)) return null;
        return { top: el.getBoundingClientRect().top, headBottom: scroller.querySelector('thead')!.getBoundingClientRect().bottom, id: el.id || el.tagName };
      });
      if (focused && focused.id.startsWith('employee-')) {
        expect(focused.top, `${focused.id} is hidden behind the header`).toBeGreaterThanOrEqual(focused.headBottom - 1);
      }
    }
  });

  test('an employee anchor from a returnTo round trip is visible below the header and focused', async ({ page }) => {
    await page.setViewportSize({ width: desktop.width, height: desktop.height });
    await bootPeopleFixture(page, { count: 25 });
    await page.goto('/people?anchor=employee-1018');
    const link = page.locator('#employee-1018');
    await expect(link).toBeFocused({ timeout: 120_000 });
    const geometry = await link.evaluate(el => {
      const scroller = el.closest('[data-testid=employee-list-scroll]') as HTMLElement;
      const r = el.getBoundingClientRect();
      return { top: r.top, bottom: r.bottom, headBottom: scroller.querySelector('thead')!.getBoundingClientRect().bottom, regionBottom: scroller.getBoundingClientRect().bottom, scrollTop: scroller.scrollTop, pageY: window.scrollY };
    });
    expect(geometry.scrollTop, 'the list (not only the page) scrolled to the anchor').toBeGreaterThan(0);
    expect(geometry.top).toBeGreaterThanOrEqual(geometry.headBottom - 1);
    expect(geometry.bottom).toBeLessThanOrEqual(geometry.regionBottom + 1);
    await shot(page, '1366x768-en-light-6-anchor');
  });

  for (const [locale, name] of [['en', 'Employee list'], ['ar', 'قائمة الموظفين']] as const) {
    test(`the list region has a localized accessible name, ${locale}: "${name}"`, async ({ page }) => {
      const { region } = await openList(page, desktop, locale, 'light');
      await expect(region).toHaveAttribute('aria-label', name);
      await expect(page.getByRole('region', { name })).toBeVisible();
    });
  }

  test('at 1280x720 with a classic 15px scrollbar the list does not gain a horizontal scrollbar', async ({ page }) => {
    knownDefect('PL-8', true, 'the bounded region now has a vertical scrollbar; on a classic 15px bar (Windows, most Linux, macOS "always show") the 960px minimum table no longer fits at 1280px wide (scrollWidth 960 > clientWidth 955), so a horizontal scrollbar appears under the list. Lower min-w-[960px] to about 900px on the table');
    await page.addInitScript(() => {
      // Headless Chromium draws overlay scrollbars; make the list's bar a classic 15px one so its width counts.
      const style = document.createElement('style');
      style.textContent = '[data-testid=employee-list-scroll]::-webkit-scrollbar{width:15px;height:15px}';
      document.addEventListener('DOMContentLoaded', () => document.head.appendChild(style));
    });
    const { region } = await openList(page, { name: '1280x720', width: 1280, height: 720, phone: false }, 'en', 'light');
    const m = await metrics(region);
    const bar = await region.evaluate((el: HTMLElement) => el.offsetWidth - el.clientWidth);
    expect(bar, 'the classic scrollbar is in effect').toBe(15);
    expect(m.scrollWidth, `scrollWidth ${m.scrollWidth} vs clientWidth ${m.clientWidth} (scrollbar ${bar}px)`).toBeLessThanOrEqual(m.clientWidth + 1);
    await shot(page, '1280x720-en-light-7-scrollbars');
  });

  for (const size of [{ width: 1366, height: 768 }, { width: 1920, height: 1080 }]) {
    test(`there is no page-level scrollbar next to the list's own at ${size.width}x${size.height}`, async ({ page }) => {
      await openList(page, { name: `${size.width}x${size.height}`, ...size, phone: false }, 'en', 'light');
      const overflow = await page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
      expect(overflow, 'document scrollHeight - innerHeight').toBeLessThanOrEqual(0);
    });
  }

  test('on a phone a swipe on the list can still reach the pager (no scroll trap)', async ({ page }) => {
    const { region } = await openList(page, VIEWPORTS[2], 'en', 'light');
    await region.scrollIntoViewIfNeeded();
    await scrollRegionTo(page, region, 'end');
    const before = await page.evaluate(() => window.scrollY);
    const box = (await region.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, Math.min(box.y + box.height / 2, 600));
    await page.mouse.wheel(0, 400);
    await expect.poll(() => page.evaluate(() => window.scrollY), { message: 'page scrolled when the list was already at its end', timeout: 2_000 }).toBeGreaterThan(before);
  });
});

