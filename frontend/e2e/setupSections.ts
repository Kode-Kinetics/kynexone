import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Setup & Administration's section navigation. From 1280px it is a grouped column with every section visible; below
 * that the list folds away and the section header's "All sections" button opens it (a bottom sheet on a phone).
 * These helpers reach a section the same way at every width, so a spec states which section it opens, not how the
 * current viewport lays the navigation out. Entries are links (a Cmd/Ctrl-click opens a new tab).
 */
export function setupSections(page: Page, name = 'Setup sections'): Locator {
  return page.getByRole('navigation', { name, exact: true });
}

/**
 * Where the entries are: the navigation, or on a phone the bottom sheet it opens (a dialog portalled to <body>, named
 * like the navigation). Use this to find an entry; use setupSections for the landmark itself.
 */
export function setupSectionEntries(page: Page, name = 'Setup sections'): Locator {
  return setupSections(page, name).or(page.getByRole('dialog', { name, exact: true }));
}

/** The "All sections" toggle in the section header, whatever the interface language. */
export function setupSectionsToggle(page: Page): Locator {
  return page.locator('[data-setup-sections-toggle]');
}

/** The section's entry in the navigation, with the folded list opened first when the entry is hidden. */
export async function setupSection(page: Page, label: string, navName?: string): Promise<Locator> {
  const entry = setupSectionEntries(page, navName).getByRole('link', { name: label, exact: true });
  const toggle = setupSectionsToggle(page);
  // Retried: a company switch or route change can re-render the page between opening the list and reading it.
  await expect(async () => {
    if (await entry.isVisible()) return;
    if (await toggle.isVisible() && await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
    await expect(entry).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
  return entry;
}

export async function openSetupSection(page: Page, label: string, navName?: string): Promise<void> {
  await (await setupSection(page, label, navName)).click();
}
