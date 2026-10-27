import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootEss, INBOX_ITEMS } from './ess/essMocks';

/** P2 Browser Pilot (mocked API): HR's profile-completion review inbox, English at 1440 and Arabic at 360, with axe. */
const PERMS = ['employees.read', 'employees.write', 'employees.sensitive', 'employees.documents', 'employees.override_validation'];
const DOCS = [
  { id: 'd-clean', kind: 'bank_letter', scanStatus: 'clean', fileName: 'clean.pdf' },
  { id: 'd-trusted', kind: 'bank_letter', scanStatus: 'not_scanned_trusted_source', fileName: 'trusted.pdf' },
  { id: 'd-quar', kind: 'bank_letter', scanStatus: 'quarantined', fileName: 'quarantined.pdf' },
  { id: 'd-pend', kind: 'bank_letter', scanStatus: 'quarantined_pending', fileName: 'pending.pdf' },
];
const bankWithDocs = { ...INBOX_ITEMS[1], documents: DOCS };
const noScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);

async function axeClean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, nodes: v.nodes.slice(0, 3).map(n => n.target.join(' ')) }));
  expect(bad, label).toEqual([]);
}

test.describe('English at 1440', () => {
  test.use({ viewport: { width: 1440, height: 1000 } });

  test('list, open the diff, accept a section with a note, send another back with a note', async ({ page }) => {
    const log = await bootEss(page, { persona: 'hr', hrPermissions: PERMS });
    const items = page.getByTestId('inbox-item');
    await expect(items).toHaveCount(2);
    await expect(items.nth(0)).toContainText('Sara Al-Qahtani');
    await items.nth(0).getByRole('button', { name: 'Review' }).click();
    await expect(items.nth(0).getByRole('row', { name: /Mobile number/ })).toContainText('Changed');
    await items.nth(0).getByRole('button', { name: 'Accept', exact: true }).first().click();
    await items.nth(0).getByLabel('Review note').fill('Checked against the ID');
    await items.nth(0).getByRole('button', { name: 'Accept', exact: true }).last().click();
    await expect(page.getByText('Accepted and applied to the employee record.')).toBeVisible();
    expect(log.decisions[0]).toEqual({ id: 'req-person', verb: 'approve', body: { notes: 'Checked against the ID' } });
  });

  test('sending back needs a note', async ({ page }) => {
    const log = await bootEss(page, { persona: 'hr', hrItems: [INBOX_ITEMS[0]], hrPermissions: PERMS });
    const item = page.getByTestId('inbox-item');
    await item.getByRole('button', { name: 'Review' }).click();
    await item.getByRole('button', { name: 'Request changes' }).click();
    await expect(item.getByRole('button', { name: 'Send back to employee' })).toBeDisabled();
    await item.getByLabel('Note to the employee (required)').fill('Use your own number.');
    await item.getByRole('button', { name: 'Send back to employee' }).click();
    await expect(page.getByText('Sent back to the employee.')).toBeVisible();
    expect(log.decisions).toEqual([{ id: 'req-person', verb: 'reject', body: { notes: 'Use your own number.' } }]);
  });

  test('the bank section goes to Finance and the new IBAN is not applied by HR', async ({ page }) => {
    const log = await bootEss(page, { persona: 'hr', hrItems: [INBOX_ITEMS[1]], hrPermissions: PERMS });
    const item = page.getByTestId('inbox-item');
    await expect(item).toContainText('Goes to Finance');
    await item.getByRole('button', { name: 'Review' }).click();
    await item.getByRole('button', { name: 'Accept', exact: true }).first().click();
    await item.getByRole('button', { name: 'Accept', exact: true }).last().click();
    await expect(page.getByText(/A Finance approver must now approve the new bank details/)).toBeVisible();
    await expect(page.getByText('Accepted and applied to the employee record.')).toHaveCount(0);
    expect(JSON.stringify(log.decisions)).not.toMatch(/iban|SA\d/i);
  });

  test('without employees.sensitive: values are masked and files are listed but not downloadable', async ({ page }) => {
    await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs], hideDocIds: true, hrPermissions: ['employees.read', 'employees.write'] });
    await expect(page.getByText('Values are masked. You need the sensitive-data permission to see them in full.')).toBeVisible();
    const item = page.getByTestId('inbox-item');
    await item.getByRole('button', { name: 'Review' }).click();
    await expect(item.getByRole('link', { name: 'Download' })).toHaveCount(0);
    await expect(item.getByTestId('attachment-hidden')).toHaveCount(2);   // clean and trusted: allowed to download, but this caller may not
    await expect(item.getByText('letter.pdf')).toHaveCount(0);
    await expect(item.getByRole('button', { name: 'Scan again' })).toHaveCount(0);
  });

  test('scan states are named, and only a file waiting for its scan offers Scan again', async ({ page }) => {
    await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs], hrPermissions: PERMS });
    const card = page.getByTestId('inbox-item');
    await card.getByRole('button', { name: 'Review' }).click();
    const row = (n: string) => card.locator('li', { hasText: n });
    await expect(row('clean.pdf')).toContainText('Checked');
    await expect(row('trusted.pdf')).toContainText('Not virus-scanned (trusted source)');
    await expect(row('quarantined.pdf')).toContainText('Blocked');
    await expect(row('pending.pdf')).toContainText('Being checked');
    await expect(card.getByRole('button', { name: 'Scan again' })).toHaveCount(1);
    await expect(row('pending.pdf').getByRole('button', { name: 'Scan again' })).toBeVisible();
    await expect(row('quarantined.pdf').getByRole('button', { name: 'Scan again' })).toHaveCount(0);
  });

  test('Scan again: the status changes to what the server answered and the file becomes downloadable', async ({ page }) => {
    const log = await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs], hrPermissions: PERMS, rescan: { status: 200, scanStatus: 'clean' } });
    const card = page.getByTestId('inbox-item');
    await card.getByRole('button', { name: 'Review' }).click();
    await card.locator('li', { hasText: 'pending.pdf' }).getByRole('button', { name: 'Scan again' }).click();
    await expect(card.locator('li', { hasText: 'pending.pdf' })).toContainText('Checked');
    await expect(card.locator('li', { hasText: 'pending.pdf' }).getByRole('link', { name: 'Download' })).toBeVisible();
    await expect(card.getByRole('button', { name: 'Scan again' })).toHaveCount(0);
    expect(log.rescans).toEqual(['d-pend']);
  });

  test('Scan again answered 409 says the file is no longer waiting and refreshes the list', async ({ page }) => {
    await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs], hrPermissions: PERMS, rescan: { status: 409 } });
    const card = page.getByTestId('inbox-item');
    await card.getByRole('button', { name: 'Review' }).click();
    await card.locator('li', { hasText: 'pending.pdf' }).getByRole('button', { name: 'Scan again' }).click();
    await expect(page.getByText('This file is no longer waiting for a scan. The list was refreshed.')).toBeVisible();
  });

  test('Scan again without the document permissions is not offered', async ({ page }) => {
    await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs], hrPermissions: ['employees.read', 'employees.write', 'employees.sensitive'] });
    const card = page.getByTestId('inbox-item');
    await card.getByRole('button', { name: 'Review' }).click();
    await expect(card.getByRole('button', { name: 'Scan again' })).toHaveCount(0);
  });

  test('axe: list, open review with decision form', async ({ page }) => {
    await bootEss(page, { persona: 'hr', hrItems: [bankWithDocs, INBOX_ITEMS[0]], hrPermissions: PERMS });
    await axeClean(page, 'list');
    const card = page.getByTestId('inbox-item').first();
    await card.getByRole('button', { name: 'Review' }).click();
    await card.getByRole('button', { name: 'Request changes' }).click();
    await axeClean(page, 'open with form');
  });
});

test.describe('Arabic at 360', () => {
  test.use({ viewport: { width: 360, height: 780 } });

  test('right-to-left, no sideways scroll, Finance chip and Scan again in Arabic, numbers stay left-to-right', async ({ page }) => {
    await bootEss(page, { persona: 'hr', locale: 'ar', hrItems: [bankWithDocs], hrPermissions: PERMS });
    expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
    const card = page.getByTestId('inbox-item');
    await expect(card).toContainText('تذهب إلى المالية');
    await card.getByRole('button', { name: 'مراجعة' }).click();
    await expect(card.getByRole('button', { name: 'إعادة الفحص' })).toHaveCount(1);
    await expect(card.getByTestId('scan-label').first()).toBeVisible();
    expect(await noScroll(page)).toBe(true);
    await expect(card.locator('[dir="ltr"]').first()).toBeVisible();
  });

  test('axe in Arabic: list and open review', async ({ page }) => {
    await bootEss(page, { persona: 'hr', locale: 'ar', hrItems: [bankWithDocs], hrPermissions: PERMS });
    await axeClean(page, 'ar list');
    await page.getByTestId('inbox-item').getByRole('button', { name: 'مراجعة' }).click();
    await axeClean(page, 'ar open');
  });
});
