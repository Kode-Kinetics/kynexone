import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { offerLetterArabic, translateOfferLetter } from '../src/i18n/offerLetter';

test('Arabic designer copy retains token placeholders and interpolates versions', () => {
  expect(translateOfferLetter('ar', 'Saved version {version}', { version: 3 })).toBe('النسخة المحفوظة 3');
  expect(translateOfferLetter('ar', 'Incomplete merge field. Use {{field_name}}.')).toContain('{{field_name}}');
  expect(translateOfferLetter('ar', 'Unknown merge field: annual_bonus')).toBe('حقل دمج غير معروف: annual_bonus');
  expect(translateOfferLetter('ar', 'Text exceeds 250 characters.')).toBe('يتجاوز النص 250 حرفًا.');
});

test('user-authored names and English copy are not changed', () => {
  expect(translateOfferLetter('ar', 'QA · Executive offer')).toBe('QA · Executive offer');
  expect(translateOfferLetter('en', 'Saved version {version}', { version: 2 })).toBe('Saved version 2');
  expect(translateOfferLetter('ar', 'Use {token} in any text or table field.', { token: '{{company_name}}' })).toContain('{{company_name}}');
});

test('literal designer interface labels and help have Arabic translations', () => {
  const missing = new Set<string>();
  for (const name of ['OfferLetterDesigner', 'OfferTemplateSelect']) {
    const source = readFileSync(`src/components/${name}.tsx`, 'utf8');
    const tree = ts.createSourceFile(name + '.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const inspect = (node: ts.Node) => {
      let value: string | undefined;
      if (ts.isCallExpression(node) && node.expression.getText(tree) === 't' && node.arguments[0] && ts.isStringLiteral(node.arguments[0])) value = node.arguments[0].text;
      if (ts.isJsxAttribute(node) && ['label', 'help'].includes(node.name.getText(tree)) && node.initializer && ts.isStringLiteral(node.initializer)) value = node.initializer.text;
      if (value && /[A-Za-z]/.test(value) && !offerLetterArabic[value]) missing.add(value);
      ts.forEachChild(node, inspect);
    };
    inspect(tree);
  }
  expect([...missing]).toEqual([]);
});

test('Arabic translations preserve all interpolation parameters', () => {
  const params = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();
  for (const [key, value] of Object.entries(offerLetterArabic)) expect(params(value), key).toEqual(params(key));
});
