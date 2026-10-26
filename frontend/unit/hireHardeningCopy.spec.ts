import { expect, test } from '@playwright/test';
import { hireWizard } from '../src/i18n/hireWizard';
import { LOCALE_DICTS } from '../src/i18n/translations';

test.describe('CD34 Arabic copy', () => {
  test('the domain hint reads «يُضاف نطاق», keeps KynexOne in Latin and never says كاينكس ون', () => {
    const ar = LOCALE_DICTS.ar['The company domain @{domain} is added. KynexOne never creates the mailbox.'];
    expect(ar).toContain('يُضاف نطاق');
    expect(ar).toContain('KynexOne');
    expect(ar).not.toContain('تُضاف');
    expect(ar).not.toContain('كاينكس');
  });
  test('no wizard sentence transliterates the brand', () => {
    for (const [k, v] of Object.entries(hireWizard.ar)) expect(v, k).not.toContain('كاينكس');
  });
  test('employee code is «الرقم الوظيفي»; the MOL ID is the establishment number at the ministry', () => {
    expect(LOCALE_DICTS.ar['Employee code']).toBe('الرقم الوظيفي');
    expect(LOCALE_DICTS.ar['MOL ID']).toBe('رقم المنشأة في وزارة الموارد البشرية');
  });
  test('rehire success copy is its own sentence', () => {
    expect(LOCALE_DICTS.en['{name} has been rehired.']).toBe('{name} has been rehired.');
    expect(LOCALE_DICTS.ar['{name} has been rehired.']).toContain('{name}');
    expect(LOCALE_DICTS.ar['{name} has been rehired.']).not.toBe('{name} has been rehired.');
  });
  test('override hint and the name-only duplicate line exist in both languages', () => {
    for (const k of ['An HR Manager can approve this exception.', 'Same name — check before continuing.', 'Border Number']) {
      expect(LOCALE_DICTS.en[k], k).toBe(k);
      expect(LOCALE_DICTS.ar[k], k).toBeTruthy();
      expect(LOCALE_DICTS.ar[k], k).not.toBe(k);
    }
  });
});
