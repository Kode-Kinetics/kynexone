import { expect, type Page } from '@playwright/test';

/**
 * The per-screen contract (Screen Standard 8g). Each check is one named assertion so a screen can be red on exactly one
 * of them and the test title says which. Hooks come from the shared primitives: `data-purpose` (ui/Page),
 * `data-primary-action` (ui/Page header slot or ui/ActionBar), `data-page-header`, `data-action-bar`,
 * `data-step-strip` (ui/StepStrip), `data-result` (ui/ResultBanner), `data-empty-state` (ui/EmptyState).
 */
export type ScreenType = 'list' | 'record' | 'form' | 'dashboard' | 'setup';
export type Locale = 'en' | 'ar';
export interface Viewport { name: string; width: number; height: number }

export const VIEWPORTS: readonly Viewport[] = [
  { name: '1366x768', width: 1366, height: 768 },
  { name: '1920x1080', width: 1920, height: 1080 },
  { name: '320x640', width: 320, height: 640 },
];
export const LOCALES: readonly Locale[] = ['en', 'ar'];

/** The primary action lives in the page header (list, record, dashboard) or the sticky footer (form, setup). */
export const primaryZone = (type: ScreenType): 'header' | 'footer' => (type === 'form' || type === 'setup' ? 'footer' : 'header');

export interface Check {
  id: 'purpose' | 'primary-action' | 'step-strip' | 'locale' | 'reflow' | 'occupied';
  /** What the assertion proves, as it appears in the test title. */
  title: string;
  /** Viewports it applies to (default all). */
  only?: readonly string[];
  /** Whether it applies to this screen. */
  applies?: (screen: { type: ScreenType; multiStep?: boolean }, locale: Locale) => boolean;
  run: (page: Page, screen: { type: ScreenType; multiStep?: boolean }, locale: Locale, viewport: Viewport) => Promise<void>;
}

export const CHECKS: readonly Check[] = [
  {
    id: 'purpose',
    title: 'opens with one purpose line, 20-140 characters (35% more in Arabic): [data-purpose]',
    async run(page, _screen, locale) {
      const purpose = page.locator('[data-purpose]');
      await expect(purpose, 'one [data-purpose] sentence under the title').toHaveCount(1);
      const text = ((await purpose.textContent()) ?? '').trim();
      expect(text.length).toBeGreaterThanOrEqual(20);
      expect(text.length).toBeLessThanOrEqual(locale === 'ar' ? 190 : 140);
    },
  },
  {
    id: 'primary-action',
    title: 'has exactly one primary action, in its zone, at the inline-end: [data-primary-action]',
    async run(page, screen, locale, viewport) {
      const primary = page.locator('[data-primary-action]');
      await expect(primary, 'exactly one [data-primary-action]').toHaveCount(1);
      const zone = primaryZone(screen.type) === 'header' ? '[data-page-header]' : '[data-action-bar]';
      await expect(page.locator(`${zone} [data-primary-action]`), `inside ${zone}`).toHaveCount(1);
      const box = await primary.boundingBox();
      expect(box, 'the primary action is visible').not.toBeNull();
      const centre = box!.x + box!.width / 2;
      // inline-end is the right half in English and the left half in Arabic (320 px: the footer bar spans, so skip the half test).
      if (viewport.width >= 640) expect(locale === 'ar' ? centre < viewport.width / 2 : centre > viewport.width / 2).toBe(true);
    },
  },
  {
    id: 'step-strip',
    title: 'shows the step strip with one current step: [data-step-strip] [aria-current=step]',
    applies: screen => !!screen.multiStep,
    async run(page) {
      await expect(page.locator('[data-step-strip] [aria-current="step"]'), 'one current step in the strip').toHaveCount(1);
    },
  },
  {
    id: 'locale',
    title: 'is right-to-left with lang="ar" in Arabic',
    applies: (_screen, locale) => locale === 'ar',
    async run(page) {
      await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
      await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
    },
  },
  {
    id: 'reflow',
    title: 'does not scroll sideways (scrollWidth <= clientWidth)',
    async run(page) {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'horizontal overflow in px').toBeLessThanOrEqual(0);
    },
  },
  {
    id: 'occupied',
    title: 'uses at least 80% of the main column at 1920 (no blank band wider than 25%)',
    only: ['1920x1080'],
    async run(page) {
      const share = await page.evaluate(() => {
        const main = document.querySelector('main');
        if (!main) return 0;
        const area = main.getBoundingClientRect();
        const content = [...main.querySelectorAll('*')].map(el => el.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0);
        if (content.length === 0) return 0;
        const left = Math.min(...content.map(r => r.left));
        const right = Math.max(...content.map(r => r.right));
        return (right - left) / area.width;
      });
      expect(share, 'occupied share of main').toBeGreaterThanOrEqual(0.8);
    },
  },
];
