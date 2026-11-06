import { expect, test } from '@playwright/test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { pathToFileURL } from 'node:url';
import * as ts from 'typescript';
import { translate } from '../src/i18n/translations';
import { renderComponent } from './support/ssr';

/**
 * Empty states (Screen Standard 3c). Three kinds, three causes, and every one tells the user what to do next:
 *   first-use   nothing exists yet          exactly one action: the same verb as the header ("Add the first grade")
 *   filtered    filters hide everything     exactly one action: "Clear filters"
 *   restricted  you may not see this        no action; it names who can grant access
 * A failed load is not empty (LoadFailedRow). A hand-rolled border-dashed block is a defect and is ratcheted down.
 */
const root = join(__dirname, '..');
const render = (props: Record<string, unknown>) => renderComponent('src/components/ui/EmptyState.tsx', 'EmptyState', props);
const actions = (html: string) => (html.match(/<button\b|<a\b/g) ?? []).length;

test('first-use: title, why it is empty, and exactly one action', () => {
  const html = render({ kind: 'first-use', title: 'No grades yet', description: "Grades set each employee's benefits and loan limits.", action: { label: 'Add the first grade', onClick: () => undefined } });
  expect(html).toContain('data-empty-state="first-use"');
  expect(html).toContain('No grades yet');
  expect(html).toContain("Grades set each employee&#x27;s benefits and loan limits.");
  expect(actions(html)).toBe(1);
  expect(html).toContain('Add the first grade');
  expect(html).toContain('btn-primary');
});

test('filtered: the action defaults to "Clear filters" and is the only one', () => {
  const html = render({ kind: 'filtered', title: 'No employees match', description: 'Active filters: Department Finance, Status Active.', action: { onClick: () => undefined } });
  expect(html).toContain('data-empty-state="filtered"');
  expect(actions(html)).toBe(1);
  expect(html).toContain('Clear filters');
  expect(html).toContain('btn-secondary');
});

test('an action can be a link', () => {
  const html = render({ kind: 'first-use', title: 'No devices yet', action: { label: 'Add a device', href: '/setup?tab=attendanceDevices' } });
  expect(html).toMatch(/<a [^>]*href="\/setup\?tab=attendanceDevices"/);
  expect(actions(html)).toBe(1);
});

test('restricted: no action, and it says who can grant access', () => {
  const html = render({ kind: 'restricted', title: "You can't see payroll runs" });
  expect(html).toContain('data-empty-state="restricted"');
  expect(actions(html)).toBe(0);
  expect(html).toContain('HR administrator');
  const custom = render({ kind: 'restricted', title: "You can't see payroll runs", description: 'Payroll access is given by your HR administrator.' });
  expect(custom).toContain('Payroll access is given by your HR administrator.');
});

test('an empty state without its action is refused at render time, so none can ship without a next step', () => {
  expect(() => render({ kind: 'first-use', title: 'No grades yet' })).toThrow(/needs one action/);
  expect(() => render({ kind: 'filtered', title: 'No match' })).toThrow(/needs one action/);
});

test('English and Arabic exist for its defaults', () => {
  for (const key of ['Clear filters', 'Access is given by your HR administrator.']) expect(translate('ar', key), key).not.toBe(key);
});

function* tsx(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name.startsWith('.')) continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) yield* tsx(p);
    else if (/\.tsx$/.test(name)) yield p;
  }
}

test('every <EmptyState> in the product has a kind, and an action unless it is restricted', () => {
  const bad: string[] = [];
  let seen = 0;
  for (const dir of ['src', 'app']) {
    for (const file of tsx(join(root, dir))) {
      if (file.endsWith('components/ui/EmptyState.tsx')) continue;
      const source = readFileSync(file, 'utf8');
      const sf = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
      const visit = (node: ts.Node) => {
        if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && node.tagName.getText(sf) === 'EmptyState') {
          const attrs = new Map(node.attributes.properties.flatMap(a => (ts.isJsxAttribute(a) ? [[a.name.getText(sf), a] as const] : [])));
          const kindAttr = attrs.get('kind')?.initializer;
          const kind = kindAttr && ts.isStringLiteral(kindAttr) ? kindAttr.text : undefined;
          seen++;
          const where = `${relative(root, file)}:${sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1}`;
          if (kind !== 'first-use' && kind !== 'filtered' && kind !== 'restricted') bad.push(`${where} (no valid kind)`);
          else if (kind !== 'restricted' && !attrs.has('action')) bad.push(`${where} (kind ${kind} has no action)`);
        }
        ts.forEachChild(node, visit);
      };
      visit(sf);
    }
  }
  expect(seen, 'the scan sees the EmptyStates in the product').toBeGreaterThan(0);
  expect(bad).toEqual([]);
});

test('ratchet: hand-rolled border-dashed blocks and ad-hoc "No ... yet" texts may only go down', async () => {
  const nativeImport = new Function('u', 'return import(u)') as (u: string) => Promise<{ count: () => Record<string, number> }>;
  const { count } = await nativeImport(pathToFileURL(join(root, 'scripts', 'design-token-ratchet.mjs')).href);
  const baseline = JSON.parse(readFileSync(join(root, 'scripts', 'design-token-baseline.json'), 'utf8')) as Record<string, number>;
  const now = count();
  for (const key of ['borderDashed', 'adHocEmptyText']) {
    expect(baseline[key], `${key} is pinned in the baseline`).toBeGreaterThan(0);
    expect(now[key], `${key} (baseline ${baseline[key]}): use ui/EmptyState`).toBeLessThanOrEqual(baseline[key]);
  }
});
