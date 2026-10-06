import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import * as ts from 'typescript';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { messageArgs } from '../src/i18n/message';

/**
 * i18n ratchet — Arabic coverage may only go UP.
 *
 * WHY THIS EXISTS. The 2026-10-05 audit found the tenant UI ~95% English under Arabic: the
 * dashboard called t() with 174 keys that were not in the dictionary (so Arabic users saw
 * English), 71 keys were sentence fragments ('due in', 'of') that Arabic cannot reorder, and
 * ~5,800 user-visible strings were hard-coded in JSX. Wave 0 fixed the dashboard and built the
 * foundation (lib/format.ts, EnumLabel, lib/apiError.ts); this file stops the rest regressing
 * while Waves 1–6 translate it, the same way rtl-logical-properties.spec.ts holds the RTL work.
 *
 * HARD CHECKS (fail on any violation):
 *   1. every literal t('…') / msg('…') key exists in BOTH en and ar;
 *   2. every English entry has an Arabic entry, and the Arabic is not a copy of the English
 *      (except AR_SAME_ALLOWED: brand names);
 *   3. every translation keeps the English entry's {placeholders} exactly;
 *   4. no NEW fragment keys: a key that starts lower-case, ends in ':' or ',', or is a bare
 *      connective ('of', 'for', 'in', …). The existing ones are pinned in i18n-baseline.json;
 *   5. every literal t('…') call whose English or Arabic text has a {placeholder} passes params,
 *      and an inline params object names every placeholder — otherwise the screen shows "{name}".
 *   app/platform (the operator console stays English, CTO decision) and the legal pages are
 *   excluded from check 1 — they are still counted by the ratchets below.
 *
 * RATCHETS (per file, pinned in e2e/i18n-baseline.json; counts may not go UP):
 *   hardcoded  user-visible JSX text and label/title/placeholder/aria-label/… literals
 *   rawStatus  `{x.status}` rendered as-is instead of through <EnumLabel>
 *   locale     'en-US' / 'en-GB' literals and argument-less toLocale*() — use useFormat()
 *   icons      directional Chevron/Arrow icons that neither the RTL stylesheet nor the call site mirrors
 *
 * IF A HARD CHECK FAILS: add the key to src/i18n/translations.ts in en AND ar, as a whole
 *   sentence with {placeholders} (see i18n/message.ts for plurals).
 * IF A RATCHET FAILS because a count went UP: translate the string (t / EnumLabel / useFormat /
 *   a mirrored icon) instead of adding a literal.
 * IF A COUNT WENT DOWN: nothing fails (a lagging baseline is accepted so parallel PRs do not
 *   conflict on it); the run prints a warning. Each Wave PR repins (`npm run i18n:repin`) and
 *   commits the baseline, so the ratchet keeps its teeth.
 *
 * REPIN (`npm run i18n:repin`) writes the current counts. It REFUSES any per-file increase and
 * any new fragment key, unless I18N_ALLOW_INCREASE=1 is set AND the baseline's
 * "allowIncreaseReason" says why. The reason and the increases are then moved into the
 * baseline's "increaseLog", so every accepted regression is on record in the diff.
 */

const FRONTEND_ROOT = process.cwd();
const BASELINE_PATH = path.join(FRONTEND_ROOT, 'e2e/i18n-baseline.json');
const SCANNED_DIRS = ['app', 'src'];
const REPIN = process.env.I18N_REPIN === '1';
const ALLOW_INCREASE = process.env.I18N_ALLOW_INCREASE === '1';

/** Excluded from the key-existence check only. Still counted by every ratchet. */
const HARD_CHECK_EXCLUDED = [/^app\/platform\//, /^src\/components\/platform\//, /^app\/privacy\//, /^app\/terms\//, /^app\/security\//];

/** Arabic may equal English only for these keys — names that are never translated. */
const AR_SAME_ALLOWED: Record<string, string> = {
  Kody: 'the assistant’s product name',
};

/** Not a sentence fragment even though it matches the shape: namespaced machine keys. */
const NAMESPACED = /^(enum|error)\./;

/** Formatting is the formatter's job; its own locale literals are the point of it. */
const LOCALE_RATCHET_EXCLUDED = new Set(['src/lib/format.ts']);

// ── Source scanning ──────────────────────────────────────────────────────────

const USER_VISIBLE_ATTRS = new Set(['label', 'placeholder', 'title', 'aria-label', 'alt', 'description', 'helperText', 'hint',
  'emptyMessage', 'emptyText', 'subtitle', 'heading', 'message', 'tooltip', 'confirmLabel', 'cancelLabel', 'buttonLabel', 'text',
  'caption', 'accessibilityLabel', 'info', 'eyebrow', 'sub', 'body', 'headline']);
const hasWords = (s: string) => /[A-Za-z]{2,}/.test(s) && !/^[A-Z0-9_]+$/.test(s.trim());

type Metric = 'hardcoded' | 'rawStatus' | 'locale' | 'icons';
const METRICS: Metric[] = ['hardcoded', 'rawStatus', 'locale', 'icons'];
type Counts = Record<Metric, number>;

interface KeyUse { key: string; file: string; line: number }
/**
 * A literal t('…') call and what it passes as params: `none` (no second argument), the names in an
 * inline object literal, or `opaque` (a variable, a spread — not checkable statically).
 */
interface TCall { keys: string[]; file: string; line: number; params: 'none' | 'opaque' | string[] }
interface Scan { counts: Map<string, Counts>; keys: KeyUse[]; tCalls: TCall[]; files: string[]; dynamicKeyCalls: number }

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name === '.next' || entry.name === 'dist') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (/\.tsx?$/.test(entry.name) && !/\.(spec|test)\.tsx?$/.test(entry.name) && !entry.name.endsWith('.d.ts')) out.push(full);
  }
  return out;
}

/** lucide-react export name → canonical icon name (its CSS class is `lucide-<canonical>`). */
function lucideCanonicalNames(): Map<string, string> {
  const out = new Map<string, string>();
  const index = path.join(FRONTEND_ROOT, 'node_modules/lucide-react/dist/esm/lucide-react.mjs');
  if (!fs.existsSync(index)) return out;
  for (const m of fs.readFileSync(index, 'utf8').matchAll(/export \{([^}]*)\} from '\.\/icons\/([a-z0-9-]+)\.mjs'/g)) {
    for (const name of m[1].matchAll(/default as (\w+)/g)) out.set(name[1], m[2]);
  }
  return out;
}

/** The `.lucide-*` classes src/styles/index.css flips under [dir='rtl']. */
function mirroredIconClasses(): Set<string> {
  const css = fs.readFileSync(path.join(FRONTEND_ROOT, 'src/styles/index.css'), 'utf8');
  return new Set([...css.matchAll(/\[dir='rtl'\]\s+\.lucide-([a-z0-9-]+)/g)].map((m) => m[1]));
}

function kebab(name: string): string {
  return name.replace(/^Lucide/, '').replace(/Icon$/, '').replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}

function isTranslatorCall(n: ts.Node): n is ts.CallExpression {
  return ts.isCallExpression(n) && ts.isIdentifier(n.expression) && (n.expression.text === 't' || n.expression.text === 'msg');
}

/** Local helpers that fill a t() template with their second argument. */
const FILL_HELPERS = new Set(['fmt', 'fill']);

function paramsOf(call: ts.CallExpression): TCall['params'] {
  let arg = call.arguments[1];
  // `fmt(t('… {n} …'), { n })` / `fill(t('… {date} …'), { date: <bdi>…</bdi> })`: the template is
  // filled by a named local helper (EstablishmentPanel's fmt for strings, StatutoryLeaveHistory's
  // fill for React nodes, which t() cannot take). Only these names count; any other wrapper does not.
  const outer = call.parent;
  if (!arg && outer && ts.isCallExpression(outer) && ts.isIdentifier(outer.expression) && FILL_HELPERS.has(outer.expression.text)
    && outer.arguments[0] === call && outer.arguments[1]) arg = outer.arguments[1];
  if (!arg) return 'none';
  const obj = ts.isParenthesizedExpression(arg) ? arg.expression : arg;
  if (!ts.isObjectLiteralExpression(obj)) return 'opaque';
  const names: string[] = [];
  for (const p of obj.properties) {
    if (ts.isShorthandPropertyAssignment(p)) names.push(p.name.text);
    else if (ts.isPropertyAssignment(p) && (ts.isIdentifier(p.name) || ts.isStringLiteral(p.name))) names.push(p.name.text);
    else return 'opaque'; // spread, computed name, method
  }
  return names;
}

/** Literal keys in a t()/msg() first argument, through `a ? 'x' : 'y'` branches. */
function literalKeys(arg: ts.Expression): string[] | null {
  if (ts.isStringLiteral(arg) || ts.isNoSubstitutionTemplateLiteral(arg)) return [arg.text];
  if (ts.isParenthesizedExpression(arg)) return literalKeys(arg.expression);
  if (ts.isConditionalExpression(arg)) {
    const a = literalKeys(arg.whenTrue), b = literalKeys(arg.whenFalse);
    return a && b ? [...a, ...b] : null;
  }
  return null;
}

function scanSources(): Scan {
  const canonical = lucideCanonicalNames();
  const mirrored = mirroredIconClasses();
  const counts = new Map<string, Counts>();
  const keys: KeyUse[] = [];
  const tCalls: TCall[] = [];
  let dynamicKeyCalls = 0;
  const files: string[] = [];
  for (const d of SCANNED_DIRS) files.push(...walk(path.join(FRONTEND_ROOT, d)));

  for (const full of files) {
    const rel = path.relative(FRONTEND_ROOT, full).split(path.sep).join('/');
    const src = fs.readFileSync(full, 'utf8');
    const sf = ts.createSourceFile(full, src, ts.ScriptTarget.Latest, true, full.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
    const c: Counts = { hardcoded: 0, rawStatus: 0, locale: 0, icons: 0 };
    const lineOf = (n: ts.Node) => sf.getLineAndCharacterOfPosition(n.getStart()).line + 1;

    // Icons imported from lucide-react, by local name.
    const icons = new Map<string, string>();
    for (const st of sf.statements) {
      if (!ts.isImportDeclaration(st) || !ts.isStringLiteral(st.moduleSpecifier) || st.moduleSpecifier.text !== 'lucide-react') continue;
      const named = st.importClause?.namedBindings;
      if (named && ts.isNamedImports(named)) {
        for (const el of named.elements) {
          const imported = (el.propertyName ?? el.name).text;
          icons.set(el.name.text, canonical.get(imported) ?? kebab(imported));
        }
      }
    }

    const insideTranslator = (n: ts.Node) => {
      for (let p = n.parent; p; p = p.parent) {
        if (isTranslatorCall(p)) return true;
        if (ts.isJsxElement(p) || ts.isSourceFile(p)) return false;
      }
      return false;
    };

    const visit = (n: ts.Node) => {
      // Keys
      if (isTranslatorCall(n) && n.arguments.length) {
        const lits = literalKeys(n.arguments[0]);
        if (lits) {
          for (const key of lits) keys.push({ key, file: rel, line: lineOf(n) });
          if ((n.expression as ts.Identifier).text === 't') tCalls.push({ keys: lits, file: rel, line: lineOf(n), params: paramsOf(n) });
        } else dynamicKeyCalls++;
      }
      // hardcoded JSX
      if (ts.isJsxText(n)) {
        const s = n.text.replace(/\s+/g, ' ').trim();
        if (s && hasWords(s)) c.hardcoded++;
      } else if (ts.isJsxAttribute(n) && n.initializer && USER_VISIBLE_ATTRS.has(n.name.getText())) {
        const init = n.initializer;
        const lit = ts.isStringLiteral(init) ? init
          : ts.isJsxExpression(init) && init.expression && (ts.isStringLiteral(init.expression) || ts.isNoSubstitutionTemplateLiteral(init.expression)) ? init.expression
          : null;
        if (lit && hasWords(lit.text)) c.hardcoded++;
      } else if (ts.isJsxExpression(n) && n.expression && n.parent && ts.isJsxElement(n.parent)) {
        const e = n.expression;
        if ((ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) && hasWords(e.text) && !insideTranslator(n)) c.hardcoded++;
        // raw status
        if (ts.isPropertyAccessExpression(e) && /status$/i.test(e.name.text)) c.rawStatus++;
      }
      // locale
      if (!LOCALE_RATCHET_EXCLUDED.has(rel)) {
        if (ts.isStringLiteral(n) && /^en-(US|GB)\b/.test(n.text)) c.locale++;
        if (ts.isCallExpression(n) && ts.isPropertyAccessExpression(n.expression)
          && /^toLocale(String|DateString|TimeString)$/.test(n.expression.name.text)
          && (n.arguments.length === 0 || (ts.isIdentifier(n.arguments[0]) && n.arguments[0].text === 'undefined'))) c.locale++;
      }
      // icons
      if ((ts.isJsxSelfClosingElement(n) || ts.isJsxOpeningElement(n)) && ts.isIdentifier(n.tagName) && icons.has(n.tagName.text)) {
        const name = icons.get(n.tagName.text)!;
        const directional = /(^|-)(chevrons?|arrow)(-|$)/.test(name) && /(^|-)(left|right)(-|$)/.test(name);
        const cls = n.attributes.properties.find((p) => ts.isJsxAttribute(p) && p.name.getText() === 'className');
        const selfMirrored = !!cls && /rtl-mirror|rtl:rotate-180|rtl:-scale-x-100|rtl:scale-x-\[-1\]/.test(cls.getText());
        if (directional && !mirrored.has(name) && !selfMirrored) c.icons++;
      }
      ts.forEachChild(n, visit);
    };
    visit(sf);
    if (METRICS.some((m) => c[m] > 0)) counts.set(rel, c);
  }
  return { counts, keys, tCalls, files: files.map((f) => path.relative(FRONTEND_ROOT, f).split(path.sep).join('/')), dynamicKeyCalls };
}

// ── Dictionaries ─────────────────────────────────────────────────────────────

const en = LOCALE_DICTS.en;
const ar = LOCALE_DICTS.ar;

function isFragmentKey(key: string): boolean {
  if (NAMESPACED.test(key)) return false;
  return /^[a-z]/.test(key) || /^\s|\s$/.test(key) || /[:,]$/.test(key) || /^(of|for|in|vs|and|or|to|by|at|on|from|with|d)$/i.test(key);
}

/** Latin words left once placeholders and plural syntax are removed. */
function latinWords(value: string): boolean {
  let s = value;
  for (let i = 0; i < 4; i++) s = s.replace(/\{[^{}]*\}/g, ' ');
  return /[A-Za-z]{2,}/.test(s);
}

const sameSet = (a: Set<string>, b: Set<string>) => a.size === b.size && [...a].every((x) => b.has(x));

// ── Baseline ─────────────────────────────────────────────────────────────────

interface Baseline {
  _readme: string;
  pinnedAt: string;
  /** Set by hand, with I18N_ALLOW_INCREASE=1, to let one repin accept increases. Cleared by the repin. */
  allowIncreaseReason?: string;
  /** Every repin that accepted an increase: when, why, and what went up. */
  increaseLog?: Array<{ pinnedAt: string; reason: string; increases: string[] }>;
  totals: Counts & { files: number };
  fragmentKeys: string[];
  files: Record<string, Partial<Counts>>;
}

function readBaseline(): Baseline {
  return JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8')) as Baseline;
}

function buildBaseline(scan: Scan): Baseline {
  const files: Record<string, Partial<Counts>> = {};
  const totals = { hardcoded: 0, rawStatus: 0, locale: 0, icons: 0, files: 0 };
  for (const rel of [...scan.counts.keys()].sort()) {
    const c = scan.counts.get(rel)!;
    const entry: Partial<Counts> = {};
    for (const m of METRICS) if (c[m] > 0) { entry[m] = c[m]; totals[m] += c[m]; }
    files[rel] = entry;
    totals.files++;
  }
  return {
    _readme: 'Pinned by e2e/i18n-coverage.spec.ts. Counts may not go up. Each Wave PR repins (`npm run i18n:repin`) and commits this file, so the baseline never lags more than one PR. Repin refuses increases unless I18N_ALLOW_INCREASE=1 and allowIncreaseReason is set here. Never hand-edit a number upward.',
    pinnedAt: new Date().toISOString().slice(0, 10),
    totals,
    fragmentKeys: Object.keys(en).filter(isFragmentKey).sort(),
    files,
  };
}

// ── Tests ────────────────────────────────────────────────────────────────────

test.describe('i18n coverage ratchet', () => {
  const scan = scanSources();

  test('the scan actually sees the frontend', () => {
    // A silent zero (wrong cwd, renamed directory) would make every assertion below vacuous.
    expect(scan.files.length).toBeGreaterThan(150);
    expect(scan.files).toContain('src/views/DashboardPage.tsx');
    expect(scan.keys.length).toBeGreaterThan(300);
    expect(Object.keys(en).length).toBeGreaterThan(500);
  });

  test('every literal t() / msg() key exists in English and Arabic', () => {
    const missing = scan.keys
      .filter((u) => !HARD_CHECK_EXCLUDED.some((re) => re.test(u.file)))
      .filter((u) => !(u.key in en) || !(u.key in ar))
      .map((u) => `  ${u.file}:${u.line}  ${JSON.stringify(u.key)}  (${!(u.key in en) ? 'no en' : ''}${!(u.key in en) && !(u.key in ar) ? ', ' : ''}${!(u.key in ar) ? 'no ar' : ''})`);
    expect(missing, `Keys used in code but missing from src/i18n/translations.ts:\n${missing.join('\n')}\n`).toEqual([]);
  });

  test('every English entry has a real Arabic translation', () => {
    const missing = Object.keys(en).filter((k) => !(k in ar));
    expect(missing, `English entries with no Arabic entry:\n${missing.join('\n')}`).toEqual([]);
    const copied = Object.keys(en).filter((k) => k in ar && ar[k] === en[k] && latinWords(en[k]) && !(k in AR_SAME_ALLOWED));
    expect(copied, `Arabic entries that are just the English text (translate them, or add a brand name to AR_SAME_ALLOWED with a reason):\n${copied.join('\n')}`).toEqual([]);
    const dead = Object.keys(AR_SAME_ALLOWED).filter((k) => !(k in en));
    expect(dead, 'AR_SAME_ALLOWED lists keys that no longer exist').toEqual([]);
  });

  test('translations keep exactly the English placeholders', () => {
    const bad: string[] = [];
    for (const [code, dict] of Object.entries(LOCALE_DICTS)) {
      if (code === 'en') continue;
      for (const [k, v] of Object.entries(dict)) {
        if (!(k in en)) continue;
        const want = messageArgs(en[k]);
        const got = messageArgs(v);
        if (!sameSet(want, got)) bad.push(`  ${code}  ${JSON.stringify(k)}: expected {${[...want].join(', ')}} got {${[...got].join(', ')}}`);
      }
    }
    // The key documents its arguments: a call site reading the key must see what the value needs.
    for (const [k, v] of Object.entries(en)) {
      if (NAMESPACED.test(k)) continue;
      if (!sameSet(messageArgs(k), messageArgs(v))) bad.push(`  en  ${JSON.stringify(k)}: key and value name different placeholders`);
    }
    expect(bad, `Placeholder mismatches:\n${bad.join('\n')}`).toEqual([]);
  });

  /** Per-file increases and decreases of `current` against `pinned`, as readable lines. */
  function compare(pinned: Baseline, current: Baseline) {
    const up: string[] = [];
    const down: string[] = [];
    const filesSeen = new Set([...Object.keys(pinned.files), ...Object.keys(current.files)]);
    for (const rel of [...filesSeen].sort()) {
      for (const m of METRICS) {
        const was = pinned.files[rel]?.[m] ?? 0;
        const is = current.files[rel]?.[m] ?? 0;
        if (is > was) up.push(`  ${rel}  ${m}: ${was} → ${is}`);
        else if (is < was) down.push(`  ${rel}  ${m}: ${was} → ${is}`);
      }
    }
    const pinnedFragments = new Set(pinned.fragmentKeys);
    const newFragments = current.fragmentKeys.filter((k) => !pinnedFragments.has(k));
    const goneFragments = pinned.fragmentKeys.filter((k) => !current.fragmentKeys.includes(k));
    return { up, down, newFragments, goneFragments };
  }

  /** A stale (too high) baseline is not a failure: it is reported so someone repins. */
  function warn(lines: string[]) {
    const text = lines.join('\n');
    console.warn(text);
    test.info().annotations.push({ type: 'warning', description: text });
  }

  const pinned = readBaseline();

  test('every t() call passes the params its message needs', () => {
    const bad: string[] = [];
    for (const call of scan.tCalls) {
      for (const key of call.keys) {
        const needed = new Set<string>();
        for (const dict of [en, ar]) if (key in dict) messageArgs(dict[key]).forEach((a) => needed.add(a));
        if (needed.size === 0) continue;
        if (call.params === 'none') bad.push(`  ${call.file}:${call.line}  ${JSON.stringify(key)}  needs {${[...needed].join(', ')}} but is called without params`);
        else if (call.params !== 'opaque') {
          const missing = [...needed].filter((a) => !(call.params as string[]).includes(a));
          if (missing.length) bad.push(`  ${call.file}:${call.line}  ${JSON.stringify(key)}  params do not name {${missing.join(', ')}}`);
        }
      }
    }
    expect(bad, `t() calls that would show a raw {placeholder}:\n${bad.join('\n')}\n`).toEqual([]);
  });

  test('no new sentence-fragment keys', () => {
    test.skip(REPIN, 'the repin test checks fragments itself');
    const now = Object.keys(en).filter(isFragmentKey);
    const { newFragments, goneFragments } = compare(pinned, { ...pinned, fragmentKeys: now });
    expect(newFragments, `New fragment keys. Arabic cannot reorder a sentence glued from fragments — write one key for the whole sentence with {placeholders}:\n${newFragments.join('\n')}`).toEqual([]);
    if (goneFragments.length) warn([`Pinned fragment keys that no longer exist — thank you. Run \`npm run i18n:repin\` and commit e2e/i18n-baseline.json:`, ...goneFragments.map((k) => `  ${k}`)]);
  });

  test('per-file counts may not go up', () => {
    test.skip(REPIN, 'the repin test writes the baseline instead');
    const { up, down } = compare(pinned, buildBaseline(scan));
    expect(up, `i18n regressions (counts went UP). Use t()/msg(), <EnumLabel>, useFormat() or a mirrored icon:\n${up.join('\n')}\n`).toEqual([]);
    if (down.length) warn([`i18n baseline is stale: counts went DOWN — thank you. Lock it in with \`npm run i18n:repin\`, then commit e2e/i18n-baseline.json:`, ...down]);
  });

  test('repin: write the baseline, refusing increases without a recorded reason', () => {
    test.skip(!REPIN, 'only under `npm run i18n:repin`');
    const current = buildBaseline(scan);
    const { up, newFragments } = compare(pinned, current);
    const increases = [...up, ...newFragments.map((k) => `  new fragment key: ${JSON.stringify(k)}`)];
    const reason = pinned.allowIncreaseReason?.trim() ?? '';
    if (increases.length) {
      expect(ALLOW_INCREASE && reason.length > 0,
        `Refusing to repin: these would go UP.\n${increases.join('\n')}\n\n`
        + 'Translate them instead. If an increase is genuinely intended, write why in "allowIncreaseReason" in '
        + 'e2e/i18n-baseline.json and rerun with I18N_ALLOW_INCREASE=1.\n').toBe(true);
    }
    const log = [...(pinned.increaseLog ?? [])];
    if (increases.length) log.push({ pinnedAt: current.pinnedAt, reason, increases: increases.map((l) => l.trim()) });
    const out: Baseline = { _readme: current._readme, pinnedAt: current.pinnedAt, ...(log.length ? { increaseLog: log } : {}), totals: current.totals, fragmentKeys: current.fragmentKeys, files: current.files };
    fs.writeFileSync(BASELINE_PATH, `${JSON.stringify(out, null, 2)}\n`);
    console.log(`i18n baseline re-pinned: ${JSON.stringify(current.totals)}${increases.length ? ` (accepted ${increases.length} increase(s): ${reason})` : ''}`);
  });

  test('the shared foundations stay wired', () => {
    const read = (rel: string) => fs.readFileSync(path.join(FRONTEND_ROOT, rel), 'utf8');
    // Raw JSON must never be an error message again.
    expect(read('src/hooks/useApiCall.ts')).not.toContain('JSON.stringify');
    expect(read('src/views/LeavePage.tsx')).not.toMatch(/`\$\{[^}]*status\}: \$\{JSON\.stringify/);
    // fr/es stay in the dictionaries but out of the switcher.
    const tr = read('src/i18n/translations.ts');
    expect(tr).toMatch(/fr: \{[^}]*selectable: false/);
    expect(tr).toMatch(/es: \{[^}]*selectable: false/);
    expect(read('src/contexts/LocaleContext.tsx')).toContain('.filter(([, meta]) => meta.selectable)');
    // The tenant's default language is honoured when the user has not chosen one.
    expect(read('src/contexts/LocaleContext.tsx')).toContain('defaultLanguage');
    // Latin digits are a product decision, not a browser default.
    expect(read('src/lib/format.ts')).toContain('-u-nu-latn');
  });
});
