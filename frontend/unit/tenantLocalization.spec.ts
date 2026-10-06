import { expect, test } from '@playwright/test';
import { DEFAULTS, readLocalization } from '../src/lib/tenantLocalization';

/**
 * GET /api/tenant-admin/localization says `stated: true` only for a real tenant's answer. Only that
 * counts as loaded, so the anonymous pre-sign-in placeholder can never apply a default language.
 */
test.describe('tenant localization answer', () => {
  test('a stated answer is loaded and its values are used', () => {
    const a = readLocalization({ stated: true, defaultLanguage: 'ar', defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR' });
    expect(a?.stated).toBe(true);
    expect(a?.settings.defaultLanguage).toBe('ar');
    expect(a?.settings.defaultTimezone).toBe('Asia/Riyadh');
    expect(a?.settings.currencyCode).toBe('SAR');
  });

  test('the anonymous placeholder (stated: false) is not loaded', () => {
    const a = readLocalization({ stated: false, defaultLanguage: 'en', defaultTimezone: '', currencyCode: '' });
    expect(a?.stated).toBe(false);
    expect(a?.settings.defaultTimezone).toBe(DEFAULTS.defaultTimezone);
  });

  test('an answer without the field (an older API) is not loaded', () => {
    expect(readLocalization({ defaultLanguage: 'ar' })?.stated).toBe(false);
  });

  test('no body is no answer', () => {
    expect(readLocalization(null)).toBeNull();
    expect(readLocalization(undefined)).toBeNull();
  });
});
