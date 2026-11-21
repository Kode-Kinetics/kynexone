import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import ts from 'typescript';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { mergeFieldKey, offerLetterArabic } from '../src/i18n/offerLetter';

/**
 * Two screens used to show English under Arabic (live crawl, HR Manager): /reports (section headings, chart titles, tab labels,
 * subtitle, filters, tables) and /recruitment/offer-templates (the merge-field names the designer lists: "probation period",
 * "work location", "contract term clause" …). These tests keep every user-visible string of both going through the translator
 * and keep each key's Arabic in the dictionary.
 */
const root = path.resolve(__dirname, '..');
const REPORT_FILES = [
  'src/views/ReportsPage.tsx',
  'src/components/charts/reports/ReportsAttendanceTrendChart.tsx',
  'src/components/charts/reports/ReportsHeadcountTrendChart.tsx',
  'src/components/charts/reports/ReportsLeaveTrendChart.tsx',
  'src/components/charts/reports/ReportsPayrollTrendChart.tsx',
];

// Values the API receives, never shown as written (the screen renders them through t()).
const API_VALUES = new Set(['Daily', 'Email']);
const CHECKED_ATTRS = new Set(['label', 'title', 'placeholder', 'aria-label', 'alt', 'what', 'confirmLabel']);
const CHART_TAGS = new Set(['Line', 'Bar', 'Area']);

const parse = (file: string) => ts.createSourceFile(file, fs.readFileSync(path.join(root, file), 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const isT = (n: ts.Node): n is ts.CallExpression =>
  ts.isCallExpression(n) && ts.isIdentifier(n.expression) && (n.expression.text === 't' || n.expression.text === 'msg');
const insideT = (n: ts.Node) => { for (let p: ts.Node | undefined = n.parent; p; p = p.parent) if (isT(p)) return true; return false; };
const insideImport = (n: ts.Node) => { for (let p: ts.Node | undefined = n.parent; p; p = p.parent) if (ts.isImportDeclaration(p) || ts.isExportDeclaration(p)) return true; return false; };
const withoutAcronyms = (s: string) => s.replace(/\b[A-Z]{2,6}\b/g, ' ');

function literalKeys(node: ts.Node, out: Set<string>) {
  if (ts.isStringLiteralLike(node)) out.add(node.text);
  else if (ts.isConditionalExpression(node)) { literalKeys(node.whenTrue, out); literalKeys(node.whenFalse, out); }
  else if (ts.isParenthesizedExpression(node)) literalKeys(node.expression, out);
}

test('every user-visible string on /reports goes through t()', () => {
  const hardcoded: string[] = [];
  for (const file of REPORT_FILES) {
    const sf = parse(file);
    const flag = (text: string, node: ts.Node) => hardcoded.push(`${file}:${sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1} «${text.trim()}»`);
    const visit = (n: ts.Node) => {
      if (ts.isJsxText(n)) {
        const text = n.text.replace(/\s+/g, ' ').trim();
        if (/[A-Za-z]{3,}/.test(withoutAcronyms(text))) flag(text, n);
      } else if (ts.isJsxAttribute(n) && n.initializer && ts.isStringLiteral(n.initializer)) {
        const name = n.name.getText(sf);
        const tag = ts.isJsxOpeningLikeElement(n.parent.parent) ? n.parent.parent.tagName.getText(sf) : '';
        const checked = CHECKED_ATTRS.has(name) || (name === 'name' && CHART_TAGS.has(tag));
        const text = n.initializer.text;
        if (checked && /[A-Za-z]{3,}/.test(withoutAcronyms(text)) && !text.includes('@')) flag(text, n);
      } else if ((ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) && !ts.isJsxAttribute(n.parent) && !insideT(n) && !insideImport(n)) {
        if (/^[A-Z][a-z]/.test(n.text) && !API_VALUES.has(n.text)) flag(n.text, n);
      } else if (ts.isTemplateExpression(n) && !insideT(n) && /^[A-Z][a-z]/.test(n.head.text)) flag(n.head.text, n);
      ts.forEachChild(n, visit);
    };
    visit(sf);
  }
  expect(hardcoded, hardcoded.join('\n')).toEqual([]);
});

test('every t()/msg() key on /reports has Arabic that is not a copy of the English', () => {
  const missing: string[] = [];
  let seen = 0;
  for (const file of REPORT_FILES) {
    const sf = parse(file);
    const visit = (n: ts.Node) => {
      if (isT(n) && n.arguments[0]) {
        const keys = new Set<string>();
        literalKeys(n.arguments[0], keys);
        for (const k of keys) {
          seen++;
          const ar = LOCALE_DICTS.ar[k];
          if (!ar || ar === k) missing.push(`${file}: «${k}»`);
        }
      }
      ts.forEachChild(n, visit);
    };
    visit(sf);
  }
  expect(seen).toBeGreaterThan(80);
  expect(missing, missing.join('\n')).toEqual([]);
});

test('the report tab strip, section headings and chart titles have Arabic', () => {
  for (const key of ['Analytics Dashboard', 'Report Library', 'Saved Reports', 'Scheduled Reports', 'Execution History', 'Reports sections',
    'Enterprise reporting, live analytics, and scheduled delivery', 'Workforce', 'Today', 'Finance & Compliance', 'Last Payroll Run',
    'Headcount Trend', 'Payroll Trend (Net Salary)', 'Attendance (Last 30 Days)', 'Leave Taken (Days)']) {
    expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
    expect(LOCALE_DICTS.ar[key], key).not.toBe(key);
  }
});

test('every merge field the offer-letter designer lists has an Arabic name', () => {
  const cs = fs.readFileSync(path.resolve(root, '../backend-dotnet/Zayra.Api/Infrastructure/Documents/Letters/OfferLetterDesign.cs'), 'utf8');
  const block = /Tokens\s*=\s*\[([\s\S]*?)\];/.exec(cs)?.[1] ?? '';
  const tokens = [...block.matchAll(/"([a-z_]+)"/g)].map(m => m[1]);
  expect(tokens.length).toBeGreaterThan(20);
  // The designer shows t(mergeFieldKey(token)) through useOfferLetterT, which falls back to the app dictionary.
  const missing = tokens.map(mergeFieldKey).filter(label => !(offerLetterArabic[label] ?? LOCALE_DICTS.ar[label]));
  expect(missing, missing.join(', ')).toEqual([]);
});
