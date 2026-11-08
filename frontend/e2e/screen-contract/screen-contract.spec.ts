import { test } from '@playwright/test';
import { CHECKS, LOCALES, VIEWPORTS } from './contract';
import { SCREENS } from './screens';

/**
 * Every screen x language x viewport x contract assertion is its own test, so the title names the exact assertion that
 * is red. An assertion a screen is known to fail (screens.ts `failing`) is test.fixme with the cause in the title until
 * Wave 1 builds the screen from the primitives; everything else runs. SCREEN_CONTRACT_STRICT=1 runs the fixme ones too.
 */
const strict = process.env.SCREEN_CONTRACT_STRICT === '1';

for (const screen of SCREENS) {
  test.describe(screen.name, () => {
    for (const locale of LOCALES) {
      for (const viewport of VIEWPORTS) {
        for (const check of CHECKS) {
          if (check.applies && !check.applies(screen, locale)) continue;
          if (check.only && !check.only.includes(viewport.name)) continue;
          const cause = screen.ready ? undefined : screen.failing[check.id];
          const title = `${locale} ${viewport.name} ${check.title}${cause ? ` [FAILS today: ${cause}]` : ''}`;
          const define = cause && !strict ? test.fixme : test;
          define(title, async ({ page }) => {
            await page.setViewportSize({ width: viewport.width, height: viewport.height });
            await screen.open(page, locale);
            await check.run(page, screen, locale, viewport);
          });
        }
      }
    }
  });
}
