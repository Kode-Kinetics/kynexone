import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootSetup, STARTER_TASKS } from './onboarding/p3Mocks';
import { bootHire, hireTemplateFixture } from './hire/hireMocks';

/** WP-P3 accessibility: axe (WCAG 2.2 AA tags) on the template screens, the worklist and the wizard picker, EN 1440 and AR 360. Serious and critical fail. */
async function clean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(700);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 3).map(n => `${n.target.join(' ')} ${JSON.stringify(n.any[0]?.data ?? {})}`) }));
  expect(bad, label).toEqual([]);
}

const seed = [{ id: 'cl-a', companyId: 'co-1', name: 'Branch hires', nameAr: 'تعيينات الفروع', isDefault: true, isActive: true, isDeleted: false, tasks: STARTER_TASKS }];
const hireSeed = [{ id: 'ht-1', companyId: 'co-1', name: 'Branch cashier', nameAr: 'أمين صندوق', matchDesignationId: 'ds1', essCompletionSections: ['id'], defaults: { probationDays: 90 }, isActive: true, isDeleted: false, checklistTaskCount: 3 }];

for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe ${locale} ${viewport.width}`, () => {
    test.use({ viewport });

    test('checklist templates: list, editor with an error state', async ({ page }) => {
      await bootSetup(page, '/setup?tab=onboardingChecklistTemplates', { locale, seedChecklists: seed });
      await expect(page.getByTestId('checklist-list')).toBeVisible({ timeout: 30_000 });
      await clean(page, 'list');
      await page.getByRole('button', { name: locale === 'ar' ? 'تعديل' : 'Edit', exact: true }).first().click();
      await expect(page.getByTestId('checklist-editor')).toBeVisible();
      await clean(page, 'editor');
      await page.getByTestId('task-row-0').locator('input').first().fill('');
      await page.getByRole('button', { name: locale === 'ar' ? 'حفظ القائمة' : 'Save checklist' }).click();
      await clean(page, 'editor with errors');
    });

    test('hire templates: list and editor', async ({ page }) => {
      await bootSetup(page, '/setup?tab=hireTemplates', { locale, seedHire: hireSeed });
      await expect(page.getByTestId('hire-template-list')).toBeVisible({ timeout: 30_000 });
      await clean(page, 'hire list');
      await page.getByRole('button', { name: locale === 'ar' ? 'تعديل' : 'Edit', exact: true }).first().click();
      await expect(page.getByTestId('hire-template-editor')).toBeVisible();
      await clean(page, 'hire editor');
    });

    test('wizard picker: before and after applying', async ({ page }) => {
      await bootHire(page, { locale, hireTemplates: [hireTemplateFixture], suggestedTemplateId: 'tpl-cashier' });
      await expect(page.getByTestId('hire-template-picker')).toBeVisible();
      await clean(page, 'picker suggested');
      await page.getByTestId('hire-template-picker').locator('select').selectOption('tpl-cashier');
      await expect(page.getByTestId('hire-template-status')).not.toBeEmpty();
      await clean(page, 'picker applied');
    });
  });
}
