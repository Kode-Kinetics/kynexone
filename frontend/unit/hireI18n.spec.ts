import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { SERVER_CODE_KEYS } from '../src/lib/hire/problem';

/** Every sentence the wizard can show exists in English and Arabic, and the Arabic is not the English. */
const ROOTS = [path.resolve(__dirname, '../src/views/employees/hire'), path.resolve(__dirname, '../src/lib/hire')];
const DYNAMIC = ['Hire basics', 'Person and contact', 'Job, pay and documents', 'Review and create', 'Male', 'Female', 'Single', 'Married', 'Divorced', 'Widowed',
  'Full-time', 'Part-time', 'Contract', 'Permanent', 'Fixed term', ...Object.values(SERVER_CODE_KEYS)];

function files(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(path.join(dir, e.name)) : /\.tsx?$/.test(e.name) ? [path.join(dir, e.name)] : []);
}
function keys(): string[] {
  const out = new Set<string>(DYNAMIC);
  for (const f of ROOTS.flatMap(files)) {
    const src = fs.readFileSync(f, 'utf8');
    for (const m of src.matchAll(/\bt\(\s*'((?:[^'\\]|\\.)*)'/g)) out.add(m[1].replace(/\\'/g, '\''));
    for (const m of src.matchAll(/\b(?:add|key:)\(?\s*(?:'[a-zA-Z]+',\s*)?'((?:[^'\\]|\\.)*)'/g)) if (/[A-Za-z]{3}.* /.test(m[1])) out.add(m[1].replace(/\\'/g, '\''));
  }
  return [...out];
}

test('every wizard sentence has an English and a different Arabic entry', () => {
  const missing: string[] = [];
  for (const k of keys()) {
    if (!LOCALE_DICTS.en[k]) missing.push(`en: ${k}`);
    else if (!LOCALE_DICTS.ar[k] || LOCALE_DICTS.ar[k] === k) missing.push(`ar: ${k}`);
  }
  expect(missing).toEqual([]);
});

test('no hard-coded user-visible literals in the wizard views (JSX text, placeholder, title, aria-label, confirm, alert)', () => {
  const offenders: string[] = [];
  for (const f of files(ROOTS[0])) {
    const src = fs.readFileSync(f, 'utf8');
    if (/\bconfirm\(|\balert\(/.test(src)) offenders.push(`${f}: native dialog`);
    if (/\b(?:placeholder|title|aria-label|alt)="[^"{]*[A-Za-z]{2}/.test(src)) offenders.push(`${f}: attribute literal`);
    for (const m of src.matchAll(/>([^<>{}\n]*[A-Za-z]{3}[^<>{}\n]*)</g)) if (!/^\s*[A-Z0-9_]+\s*$/.test(m[1]) && !/=>|&&|\?|\bas\b/.test(m[1])) offenders.push(`${f}: JSX text "${m[1].trim()}"`);
  }
  expect(offenders).toEqual([]);
});
