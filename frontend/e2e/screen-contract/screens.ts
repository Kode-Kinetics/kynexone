import fs from 'node:fs';
import path from 'node:path';
import type { Page } from '@playwright/test';
import { ALL_PERMISSIONS, boot } from '../setup-consolidation/fixture';
import { bootEmployeeWorkspaceFixture } from '../employee-workspace.fixture';
import type { Check, Locale, ScreenType } from './contract';

/**
 * The screens held to the contract. Wave 1 builds the first three (Screen Standard examples A, B and C). Adding a screen
 * is adding an entry: how to open it with every API call intercepted, its type, whether it is a multi-step flow, and which
 * assertions fail today.
 *
 * `failing` records, per assertion, WHY it fails on main today. While `ready` is false an assertion listed there is
 * test.fixme with that cause in its title, so the suite is green and the debt is named. Wave 1 builds the screen from the
 * primitives (ui/Page, ActionBar, StepStrip, ResultBanner, EmptyState), empties `failing` and sets `ready: true`.
 * SCREEN_CONTRACT_STRICT=1 runs every assertion regardless, to see the current red.
 */
export interface ScreenContract {
  name: string;
  type: ScreenType;
  multiStep?: boolean;
  open: (page: Page, locale: Locale) => Promise<void>;
  ready: boolean;
  failing: Partial<Record<Check['id'], string>>;
}

const fixtures = path.join(__dirname, '..', 'fixtures', 'benefits-by-grade');
const load = (name: string) => JSON.parse(fs.readFileSync(path.join(fixtures, `${name}.json`), 'utf8'));

export const SCREENS: readonly ScreenContract[] = [
  {
    name: 'employee-edit',
    type: 'form',
    multiStep: true,
    async open(page, locale) {
      await bootEmployeeWorkspaceFixture(page, { locale });
      await page.goto('/people/901/edit?mode=guided&section=personal');
      await page.locator('main').first().waitFor({ state: 'visible', timeout: 60_000 });
    },
    ready: false,
    failing: {
      purpose: 'no [data-purpose] line under the title (Page primitive not adopted)',
      'primary-action': 'no [data-primary-action] in a sticky [data-action-bar] (two btn-primary today, no ActionBar)',
      'step-strip': 'the progress strip is not [data-step-strip] with aria-current=step (EmployeeCreateProgress not extracted to StepStrip)',
    },
  },
  {
    name: 'attendance-dashboard',
    type: 'dashboard',
    async open(page, locale) {
      await boot(page, {
        locale, url: '/attendance',
        persona: { roles: ['HR Manager'], permissions: [...ALL_PERMISSIONS, 'attendance.read', 'attendance.write'] },
        mocks: {},
      });
      await page.locator('main').first().waitFor({ state: 'visible', timeout: 60_000 });
    },
    ready: true,
    failing: {},
  },
  {
    name: 'benefits-by-grade',
    type: 'setup',
    multiStep: true,
    async open(page, locale) {
      await boot(page, {
        locale, url: '/benefits/by-grade',
        persona: { roles: ['HR Director'], permissions: [...ALL_PERMISSIONS, 'entitlements.read', 'entitlements.manage'] },
        mocks: { '/api/entitlements/matrix': load('matrix-group'), '/api/companies': { items: load('companies'), total: 1, page: 1, pageSize: 100 } },
      });
      await page.locator('main').first().waitFor({ state: 'visible', timeout: 60_000 });
    },
    ready: false,
    failing: {
      purpose: 'no [data-purpose] line under the title (Page primitive not adopted)',
      'primary-action': 'no [data-primary-action] in the sticky [data-action-bar] (Publish sits at the bottom of the page)',
      'step-strip': 'the guided flow is not a [data-step-strip] (StepStrip not adopted)',
    },
  },
];
