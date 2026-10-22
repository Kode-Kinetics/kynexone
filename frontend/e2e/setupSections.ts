import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Setup & Administration's section navigation. From 1280px it is a grouped column with every section visible; below
 * that it is a section picker whose list opens on demand. These helpers reach a section the same way at every
 * width, so a spec states which section it opens, not how the current viewport lays the navigation out.
 */
export function setupSections(page: Page, name = 'Setup sections'): Locator {
  return page.getByRole('navigation', { name, exact: true });
}

/** The section's entry in the navigation, with the picker opened first when the entry is folded away. */
export async function setupSection(page: Page, label: string, navName?: string): Promise<Locator> {
  const nav = setupSections(page, navName);
  const entry = nav.getByRole('button', { name: label, exact: true });
  // The picker is the navigation's only disclosure button, whatever the interface language.
  const picker = nav.locator('button[aria-expanded]');
  // Retried: a company switch or route change can re-render the page between opening the picker and reading it.
  await expect(async () => {
    if (await entry.isVisible()) return;
    if (await picker.isVisible() && await picker.getAttribute('aria-expanded') !== 'true') await picker.click();
    await expect(entry).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
  return entry;
}

export async function openSetupSection(page: Page, label: string, navName?: string): Promise<void> {
  await (await setupSection(page, label, navName)).click();
}
