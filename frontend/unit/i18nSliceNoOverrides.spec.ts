import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import ts from 'typescript';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { releaseASlices } from '../src/i18n/releaseA';

/**
 * A Release A slice is spread AFTER the base dictionary, so a key it repeats with different Arabic re-words every other screen
 * that uses the key (the Loan payments slice once turned the app-wide "Employee" = موظف into الموظف, which broke the cost-centre
 * readiness link names). A slice may add keys; for a key the base file already defines it must be identical, or leave it out.
 */
const file = path.resolve(__dirname, '../src/i18n/translations.ts');

function baseEntries(lang: 'en' | 'ar'): Record<string, string> {
  const sf = ts.createSourceFile(file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true);
  const out: Record<string, string> = {};
  sf.forEachChild(node => {
    if (!ts.isVariableStatement(node)) return;
    for (const d of node.declarationList.declarations) {
      if (d.name.getText(sf) !== lang || !d.initializer || !ts.isObjectLiteralExpression(d.initializer)) continue;
      for (const p of d.initializer.properties) {
        if (!ts.isPropertyAssignment(p)) continue;
        const key = ts.isStringLiteralLike(p.name) || ts.isIdentifier(p.name) ? p.name.text : null;
        if (key !== null && ts.isStringLiteralLike(p.initializer)) out[key] = p.initializer.text;
      }
    }
  });
  return out;
}

test('the established Arabic for "Employee" is not re-worded by a later slice', () => {
  expect(LOCALE_DICTS.ar['Employee']).toBe('موظف');
});

test('a Release A slice never redefines a key translations.ts already defines with different Arabic', () => {
  const base = baseEntries('ar');
  expect(Object.keys(base).length).toBeGreaterThan(500);
  const collisions: string[] = [];
  for (const name of ['loanPayments', 'benefitUsage']) {
    for (const [k, v] of Object.entries(releaseASlices[name].ar)) {
      if (k in base && base[k] !== v) collisions.push(`"${k}": translations.ts = «${base[k]}» but releaseA/${name} = «${v}»`);
    }
  }
  expect(collisions, collisions.join('\n')).toEqual([]);
});
