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

test.describe('lead review fixes', () => {
  test('a year is a plain year in the panel line, EN and AR (never "2,021")', async () => {
    const { plainYear } = await import('../src/lib/hire/duplicate');
    const { translate } = await import('../src/i18n/translations');
    expect(plainYear(2021)).toBe('2021');
    expect(translate('en', 'Joined in {year}', { year: plainYear(2021) })).toBe('Joined in 2021');
    expect(translate('ar', 'Joined in {year}', { year: plainYear(2021) })).toBe('انضم في 2021');
  });
  test('no wizard sentence promises Self-Service completion; Review copy is neutral', () => {
    for (const k of Object.keys(hireWizard.ar)) expect(k, k).not.toMatch(/self-service|pre-?boarding/i);
    for (const v of Object.values(hireWizard.ar)) expect(v).not.toContain('الخدمة الذاتية');
    expect(LOCALE_DICTS.en['You can leave this for now and add it later from the employee record.']).toBeTruthy();
    expect(LOCALE_DICTS.ar['You can leave this for now and add it later from the employee record.']).toBe('يمكنك تركه الآن وإضافته لاحقًا من ملف الموظف.');
  });
});
