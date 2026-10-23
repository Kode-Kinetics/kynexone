import { test, expect } from '@playwright/test';
import { LOCALE_DICTS, translate, type LocaleCode } from '../src/i18n/translations';
import { PERMISSION_AREA_LABELS, permissionAccessDescription, permissionModule } from '../src/lib/permissionAreas';
import { commandNavigationItems, navigationGroups } from '../src/routes/navigation';

const tr = (locale: LocaleCode) => (key: string, params?: Parameters<typeof translate>[2]) => translate(locale, key, params);
const hasArabic = (key: string) => Boolean(LOCALE_DICTS.ar[key]) && LOCALE_DICTS.ar[key] !== LOCALE_DICTS.en[key];

// The ⌘K palette used to describe a screen as "Requires payroll.read or payroll.write".

test('a single module reads as plain words, in English and Arabic', () => {
  expect(permissionAccessDescription(['payroll.read'], 'en', tr('en'))).toBe('Needs payroll access');
  expect(permissionAccessDescription(['payroll.read'], 'ar', tr('ar'))).toBe('يتطلب صلاحية الرواتب');
});

test('several keys of one module are one area, and different modules are joined with "or"', () => {
  expect(permissionAccessDescription(['leave.read', 'leave.write'], 'en', tr('en'))).toBe('Needs leave access');
  expect(permissionAccessDescription(['employees.write', 'payroll.approve'], 'en', tr('en'))).toBe('Needs employee records or payroll access');
  const arabic = permissionAccessDescription(['employees.write', 'payroll.approve'], 'ar', tr('ar'));
  expect(arabic).toContain('سجلات الموظفين');
  expect(arabic).toContain('الرواتب');
  expect(arabic).toContain('أو');
});

test('proper names keep their capital; an unknown module is still words, never a raw key', () => {
  expect(permissionAccessDescription(['qiwa.read'], 'en', tr('en'))).toBe('Needs Qiwa access');
  expect(permissionAccessDescription(['time_bank.read'], 'en', tr('en'))).toBe('Needs time bank access');
  expect(permissionAccessDescription([], 'en', tr('en'))).toBe('Open module');
});

test('every module the menu and palette gate on has an area name in Arabic', () => {
  const modules = new Set(commandNavigationItems.flatMap((item) => item.requiredPermissions ?? []).map(permissionModule));
  for (const module of modules) {
    const key = PERMISSION_AREA_LABELS[module];
    expect(key, `area label for "${module}"`).toBeTruthy();
    if (key !== 'Kody') expect(hasArabic(key), `Arabic for area "${key}"`).toBe(true);
  }
  for (const item of commandNavigationItems) {
    const description = permissionAccessDescription(item.requiredPermissions ?? [], 'ar', tr('ar'));
    expect(description, item.label).not.toMatch(/[a-z]+\.[a-z_]+/);
  }
});

test('every sidebar group and entry has Arabic, including the ones the design review listed', () => {
  for (const group of navigationGroups) {
    expect(hasArabic(group.label), `group "${group.label}"`).toBe(true);
    for (const item of group.items) if (item.label !== 'Kody') expect(hasArabic(item.label), `entry "${item.label}"`).toBe(true);
  }
  expect(translate('ar', 'Group Overview')).toBe('نظرة على المجموعة');
  expect(translate('ar', 'Insights & Reports')).toBe('التحليلات والتقارير');
  expect(translate('ar', 'Tax Policies')).toBe('السياسات الضريبية');
  expect(translate('ar', 'GOSI Filing')).toBe('ملفات التأمينات الاجتماعية');
  expect(translate('ar', 'Parallel Run Variance')).toBe('فروقات التشغيل الموازي');
  expect(translate('ar', 'Home')).toBe('الرئيسية');
});

test('the CSV toolbar labels use the agreed Arabic, and the template key still reads "Template" in English', () => {
  expect(translate('ar', 'Import CSV')).toBe('استيراد CSV');
  expect(translate('ar', 'CSV import template')).toBe('القالب');
  expect(translate('en', 'CSV import template')).toBe('Template');
  expect(translate('ar', 'Export')).toBe('تصدير');
  expect(translate('ar', 'Importing…')).toBe('جارٍ الاستيراد…');
  expect(translate('ar', 'Checking…')).toBe('جارٍ التحقق…');
});
