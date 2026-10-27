import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { reviewItemLabel } from '../src/lib/hire/reviewLabels';

const tIn = (loc: 'en' | 'ar') => (k: string, p?: Record<string, string | number>) =>
  (LOCALE_DICTS[loc][k] ?? k).replace(/\{(\w+)\}/g, (_, n) => String(p?.[n] ?? `{${n}}`));
const ARABIC = /[؀-ۿ]/;

const backend = (...p: string[]) => fs.readFileSync(path.resolve(process.cwd(), '..', 'backend-dotnet', 'Zayra.Api', ...p), 'utf8');

/** Every key the readiness evaluator and the hire-review route can emit, read from the backend source of truth. */
function emittedKeys(): { key: string; label: string }[] {
  const registry = [...backend('Infrastructure', 'Employees', 'EmployeeFieldRegistry.cs').matchAll(/^\s+D\("([^"]+)", "([^"]+)"/gm)].map(m => ({ key: m[1], label: m[2] }));
  const evaluator = backend('Infrastructure', 'Employees', 'EmployeeReadinessEvaluator.cs');
  const block = evaluator.slice(evaluator.indexOf('ImportGapLabels ='), evaluator.indexOf('};', evaluator.indexOf('ImportGapLabels =')));
  const gaps = [...block.matchAll(/\["([^"]+)"\]\s*=\s*"([^"]+)"/g)].map(m => ({ key: m[1], label: m[2] }));
  const noPolicy = evaluator.match(/NoPolicyKey\s*=\s*"([^"]+)"/)![1];
  return [
    ...registry, ...gaps,
    { key: noPolicy, label: 'No readiness policy configured' },
    { key: 'pay:compensationUnapproved', label: 'Pay package awaiting Finance approval' },
    { key: 'invalid:payrollProfile.iban', label: 'Invalid value: payrollProfile.iban' },
    { key: 'data:id_checksum_invalid', label: 'whatever the server said' },
    { key: 'unknown', label: '' },
  ];
}

test('CD47: the backend emits a healthy number of readiness keys (guards the parser itself)', () => {
  const keys = emittedKeys();
  expect(keys.length).toBeGreaterThan(100);
  expect(keys.map(k => k.key)).toContain('IqamaNumber');
  expect(keys.map(k => k.key)).toContain('dup:strong');
});

test('CD47: every readiness / hire-review key has an English and an Arabic label in the frontend catalog', () => {
  const problems: string[] = [];
  for (const { key, label } of emittedKeys()) {
    const en = reviewItemLabel({ key, label }, tIn('en'));
    const ar = reviewItemLabel({ key, label }, tIn('ar'));
    if (!en || en === key) problems.push(`${key}: no English label (${en})`);
    if (!ARABIC.test(ar)) problems.push(`${key}: no Arabic label (${ar})`);
    if (ar === en) problems.push(`${key}: Arabic equals English`);
  }
  expect(problems).toEqual([]);
});

test('CD47: an identity item never reaches the Arabic screen as an English server sentence', () => {
  const ar = reviewItemLabel({ key: 'IqamaNumber', label: 'Identity number missing' }, tIn('ar'));
  expect(ar).not.toMatch(/identity number missing/i);
  expect(ARABIC.test(ar)).toBe(true);
});

test('CD47: the hire-review route always sends a key for every item', () => {
  const src = backend('Controllers', 'Employees', 'EmployeeHireController.cs');
  expect(src).toContain('ReviewKey(');
});

// ── CD48 ──────────────────────────────────────────────────────────────────────────────────────
test('CD48: the corrected Arabic copy', () => {
  const ar = LOCALE_DICTS.ar;
  expect(ar['Terminated']).toBe('منتهي الخدمة');
  expect(ar['Joined in {year}']).toBe('انضم في {year}');
  expect(ar['This is a different person']).toBe('هذا شخص آخر');
  expect(ar['Marked as a different person']).toBe('تم تحديده كشخص آخر');
  expect(ar['Confirm different person']).toBe('تأكيد أنه شخص آخر');
  expect(ar['Same name — check before continuing.']).toBe('يوجد موظف بالاسم نفسه. تحقق قبل المتابعة.');
  expect(ar['You can leave this for now and add it later from the employee record.']).toContain('لاحقًا');
  expect(ar['You can leave this for now and add it later from the employee record.']).not.toContain('لاحقاً');
});

test('CD48: the duplicate panel isolates its separator so it cannot be drawn as an Arabic-Indic zero', () => {
  const panel = fs.readFileSync(path.resolve(process.cwd(), 'src', 'views', 'employees', 'hire', 'fields', 'DuplicatePanel.tsx'), 'utf8');
  expect(panel).not.toContain('٠');
  expect(panel).not.toMatch(/`\s*·\s*\$\{/);                       // no bare "·" glued into a template string
  expect(panel).toMatch(/dir="ltr"[^>]*>·<\/span>|>·<\/span>/);       // rendered as its own isolated span
});
