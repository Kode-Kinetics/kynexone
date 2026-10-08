import { test, expect } from '@playwright/test';
import { releaseA, releaseASlices } from '../src/i18n/releaseA';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { navigationHints, navigationItems } from '../src/routes/navigation';
import { RELEASE_A_FLAG } from '../src/lib/releaseA';
import { essNavItems, essPages } from '../src/routes/essSections';

// Release A R0 contract: one string file per slice, every English key translated into Arabic, spread into the
// dictionaries once, and every Release A screen reachable only behind the release_a flag.

const ARABIC = /[؀-ۿ]/;

test('every Release A slice translates every key into Arabic', () => {
  for (const [name, slice] of Object.entries(releaseASlices)) {
    expect(Object.keys(slice.ar).sort(), name).toEqual(Object.keys(slice.en).sort());
    for (const [key, value] of Object.entries(slice.en)) expect(value, `${name}: ${key}`).toBe(key);
    for (const [key, value] of Object.entries(slice.ar)) expect(ARABIC.test(value), `${name}: ${key}`).toBe(true);
  }
});

test('no key is claimed by two slices, so no slice can silently overwrite another', () => {
  const seen = new Map<string, string>();
  for (const [name, slice] of Object.entries(releaseASlices)) {
    for (const key of Object.keys(slice.en)) {
      expect(seen.get(key), `"${key}" is in ${seen.get(key)} and ${name}`).toBeUndefined();
      seen.set(key, name);
    }
  }
});

test('the dictionaries carry the Release A strings', () => {
  for (const key of Object.keys(releaseA.en)) {
    expect(LOCALE_DICTS.en[key]).toBe(key);
    expect(LOCALE_DICTS.ar[key]).toBe(releaseA.ar[key]);
  }
  expect(translate('ar', 'Contract renewals')).toBe('تجديد العقود');
  expect(translate('ar', 'My package')).toBe('باقتي');
});

test('Release A navigation is flag-gated, permission-gated, explained and translated', () => {
  // Admin screens are sidebar entries; the employee's own two are tabs inside Self-Service.
  const menuPaths = ['/benefits/by-grade', '/contract-renewals'];
  for (const path of menuPaths) {
    const item = navigationItems.find((i) => i.path === path);
    expect(item, path).toBeDefined();
    expect(item!.requiredFeatureKey, path).toBe(RELEASE_A_FLAG);
    expect(item!.requiredPermissions?.length, path).toBeGreaterThan(0);
    expect(navigationHints[path], path).toBeTruthy();
    expect(ARABIC.test(translate('ar', item!.label)), path).toBe(true);
  }
  expect(navigationItems.filter((i) => i.requiredFeatureKey === RELEASE_A_FLAG).map((i) => i.path).sort()).toEqual([...menuPaths].sort());

  const essPaths = ['/ess/package', '/ess/deductions'];
  for (const path of essPaths) {
    const page = essPages.find((p) => p.path === path);
    expect(page, path).toBeDefined();
    expect(page!.requiredFeatureKey, path).toBe(RELEASE_A_FLAG);
    expect(essNavItems.find((i) => i.path === path)?.requiredPermissions, path).toEqual(['ess.read']);
    expect(ARABIC.test(translate('ar', page!.hint)), path).toBe(true);
    expect(ARABIC.test(translate('ar', page!.label)), path).toBe(true);
    expect(ARABIC.test(translate('ar', page!.tab)), path).toBe(true);
  }
  expect(essPages.filter((p) => p.requiredFeatureKey === RELEASE_A_FLAG).map((p) => p.path).sort()).toEqual([...essPaths].sort());
});
