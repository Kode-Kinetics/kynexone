import test from 'node:test';
import assert from 'node:assert/strict';
import { shellStrings } from '../src/config/strings/shellStrings.ts';
import { homeStrings } from '../src/config/strings/homeStrings.ts';
import { leavePayStrings } from '../src/config/strings/leavePayStrings.ts';
import { managerStrings } from '../src/config/strings/managerStrings.ts';
import { accountStrings } from '../src/config/strings/accountStrings.ts';
import { safetyStrings } from '../src/config/strings/safetyStrings.ts';

type Tree = { [key: string]: string | Tree };

function leaves(tree: Tree, prefix = ''): Map<string, string> {
  const out = new Map<string, string>();
  for (const [key, value] of Object.entries(tree)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof value === 'string') out.set(path, value);
    else for (const [k, v] of leaves(value, path)) out.set(k, v);
  }
  return out;
}

const modules = { shellStrings, homeStrings, leavePayStrings, managerStrings, accountStrings, safetyStrings } as Record<string, { en: Tree; ar: Tree }>;

for (const [name, mod] of Object.entries(modules)) {
  test(`${name}: English and Arabic have the same keys, none empty`, () => {
    const en = leaves(mod.en);
    const ar = leaves(mod.ar);
    assert.deepEqual([...ar.keys()].sort(), [...en.keys()].sort());
    for (const [key, value] of ar) assert.ok(value.trim().length > 0, `${name} ar.${key} is empty`);
  });

  test(`${name}: interpolation placeholders match`, () => {
    const en = leaves(mod.en);
    const ar = leaves(mod.ar);
    const vars = (s: string) => (s.match(/\{\{\s*\w+\s*\}\}/g) ?? []).map((v) => v.replace(/\s/g, '')).sort();
    for (const [key, value] of en) assert.deepEqual(vars(ar.get(key) ?? ''), vars(value), `${name}.${key}`);
  });
}
