import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

/**
 * The `i` coverage ratchet (Screen Standard 8d): every <Field> has `info`, or `noInfo="reason"`, or is counted as
 * undecided; undecided may only go down per file and a new file starts at 0. Wired into `npm run lint`.
 */
type Counts = { withInfo: number; noInfo: number; withoutInfo: number };
type Mod = { count: () => Record<string, Counts>; countFile: (text: string) => Counts; regressions: (now: Record<string, Counts>, baseline: Record<string, Counts>) => string[] };
const root = join(__dirname, '..');
const load = () => (new Function('u', 'return import(u)') as (u: string) => Promise<Mod>)(pathToFileURL(join(root, 'scripts', 'info-coverage-ratchet.mjs')).href);

test('a Field is counted as with info, exempt with a reason, or undecided', async () => {
  const { countFile } = await load();
  const source = `
    export const X = () => (<>
      <Field label="Grade" info="Sets the limits." infoKey="employees.grade"><input /></Field>
      <Field label="Phone" noInfo="plain text"><input /></Field>
      <Field label="Joining date"><input /></Field>
      <Field label="Notes" />
      <Other label="not a Field" />
    </>);`;
  expect(countFile(source)).toEqual({ withInfo: 1, noInfo: 1, withoutInfo: 2 });
});

test('the baseline is today: the repo passes, and undecided cannot rise in a file or appear in a new file', async () => {
  const { count, regressions } = await load();
  const baseline = JSON.parse(readFileSync(join(root, 'scripts', 'info-coverage-baseline.json'), 'utf8')).files as Record<string, Counts>;
  expect(regressions(count(), baseline)).toEqual([]);
  const rose = { ...baseline, 'src/views/LoansPage.tsx': { ...(baseline['src/views/LoansPage.tsx'] ?? { withInfo: 0, noInfo: 0, withoutInfo: 0 }), withoutInfo: (baseline['src/views/LoansPage.tsx']?.withoutInfo ?? 0) + 1 } };
  expect(regressions(rose, baseline)).toHaveLength(1);
  expect(regressions({ 'src/views/BrandNewPage.tsx': { withInfo: 0, noInfo: 0, withoutInfo: 1 } }, baseline)).toEqual(['src/views/BrandNewPage.tsx: withoutInfo 0 -> 1']);
  expect(regressions({ 'src/views/BrandNewPage.tsx': { withInfo: 3, noInfo: 2, withoutInfo: 0 } }, baseline)).toEqual([]);
});

test('lint runs it', () => {
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as { scripts: Record<string, string> };
  expect(pkg.scripts.lint).toContain('info-coverage-ratchet.mjs');
});
