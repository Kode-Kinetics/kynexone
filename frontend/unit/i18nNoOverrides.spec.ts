import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import ts from 'typescript';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * A feature catalog may add keys, never redefine one that already exists with different wording: the later spread in
 * translations.ts would silently re-word another screen (P2 once turned "All companies" into a different Arabic phrase on the GL screens).
 * Give a screen its own namespaced key, or reuse the existing text.
 */
const dir = path.resolve(__dirname, '../src/i18n');
type Pair = { en?: Record<string, string>; ar?: Record<string, string> };

const isDict = (v: unknown): v is Record<string, string> =>
  !!v && typeof v === 'object' && Object.keys(v).length > 0 && Object.values(v as object).every(x => typeof x === 'string');

async function catalogs(): Promise<Record<string, Pair>> {
  const out: Record<string, Pair> = {};
  for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.ts') && !['translations.ts', 'message.ts'].includes(f))) {
    let mod: Record<string, unknown>;
    try { mod = require(path.join(dir, file)); } catch { continue; }
    for (const [name, value] of Object.entries(mod)) {
      const v = value as Pair;
      if (v && typeof v === 'object' && (isDict(v.en) || isDict(v.ar))) out[`${file}:${name}`] = { en: isDict(v.en) ? v.en : undefined, ar: isDict(v.ar) ? v.ar : undefined };
    }
  }
  return out;
}

/** The literal entries written directly inside the `en` and `ar` objects of translations.ts (not the spreads). */
function inlineEntries(): Record<'en' | 'ar', Record<string, string>> {
  const file = path.join(dir, 'translations.ts');
  const sf = ts.createSourceFile(file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true);
  const result = { en: {}, ar: {} } as Record<'en' | 'ar', Record<string, string>>;
  sf.forEachChild(node => {
    if (!ts.isVariableStatement(node)) return;
    for (const d of node.declarationList.declarations) {
      const name = d.name.getText(sf);
      if ((name !== 'en' && name !== 'ar') || !d.initializer || !ts.isObjectLiteralExpression(d.initializer)) continue;
      for (const p of d.initializer.properties) {
        if (!ts.isPropertyAssignment(p)) continue;
        const key = ts.isStringLiteralLike(p.name) || ts.isIdentifier(p.name) ? p.name.text : null;
        if (key !== null && ts.isStringLiteralLike(p.initializer)) result[name][key] = p.initializer.text;
      }
    }
  });
  return result;
}

// Older catalogs already disagree with each other in places; this guard is for the catalogs below and any added to the list.
const OWNED = ['onboardingP2.ts:', 'onboardingP3.ts:', 'onboardingP4.ts:'];

test('an onboarding catalog never redefines a key another catalog or translations.ts already defines with different wording', async () => {
  const cats = await catalogs();
  const inline = inlineEntries();
  expect(Object.keys(cats).filter(k => OWNED.some(o => k.startsWith(o)) && (cats[k].ar || cats[k].en)).length).toBeGreaterThanOrEqual(3);
  expect(Object.keys(inline.ar).length).toBeGreaterThan(500);
  const collisions: string[] = [];
  for (const loc of ['en', 'ar'] as const) {
    const others = new Map<string, { value: string; source: string }>();
    for (const [k, v] of Object.entries(inline[loc])) others.set(k, { value: v, source: 'translations.ts' });
    for (const [source, pair] of Object.entries(cats)) {
      if (OWNED.some(o => source.startsWith(o))) continue;
      for (const [k, v] of Object.entries(pair[loc] ?? {})) if (!others.has(k)) others.set(k, { value: v, source });
    }
    for (const [source, pair] of Object.entries(cats)) {
      if (!OWNED.some(o => source.startsWith(o))) continue;
      for (const [k, v] of Object.entries(pair[loc] ?? {})) {
        const prior = others.get(k);
        if (prior && prior.value !== v) collisions.push(`${loc} "${k}": ${prior.source} = «${prior.value}» but ${source} = «${v}»`);
      }
    }
  }
  expect(collisions, collisions.join('\n')).toEqual([]);
});
