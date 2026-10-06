/**
 * Message templates for translated copy: `{name}` placeholders and ICU-style plurals.
 *
 * WHY. Arabic does not put words in English order, so a sentence assembled from fragments
 * (`t('due in') + ' ' + age`) can never be translated correctly. Every user-facing sentence is
 * one key with named placeholders, and the translation decides where each value goes:
 *
 *   'Due in {time}'                       → 'مستحق خلال {time}'
 *   '{count, plural, one {# employee} other {# employees}}'
 *
 * Plural branches follow Intl.PluralRules for the locale (Arabic has six forms: zero, one, two,
 * few, many, other; English has two), plus exact `=N` matches. `#` inside a branch is the count,
 * formatted for the locale. Only `plural` is supported; anything else is left verbatim so a
 * malformed template shows up on screen rather than throwing.
 *
 * Pure: no React, no DOM. The CI i18n ratchet (e2e/i18n-coverage.spec.ts) uses `messageArgs` to
 * prove every translation keeps the same placeholders as English.
 */

export type MessageParams = Record<string, string | number | null | undefined>;

type Node =
  | { kind: 'text'; value: string }
  | { kind: 'arg'; name: string }
  | { kind: 'plural'; name: string; branches: Record<string, Node[]> }
  | { kind: 'hash' };

class Parser {
  i = 0;
  constructor(private readonly s: string) {}

  parse(inPlural: boolean, stopAtBrace: boolean): Node[] {
    const out: Node[] = [];
    let text = '';
    const flush = () => { if (text) { out.push({ kind: 'text', value: text }); text = ''; } };
    while (this.i < this.s.length) {
      const c = this.s[this.i];
      if (c === '}' && stopAtBrace) break;
      if (c === '#' && inPlural) { flush(); out.push({ kind: 'hash' }); this.i++; continue; }
      if (c === '{') {
        const node = this.tryArg();
        if (node) { flush(); out.push(node); continue; }
      }
      text += c;
      this.i++;
    }
    flush();
    return out;
  }

  /** At a `{`. Returns null (and leaves the cursor) when this is not a well-formed argument. */
  private tryArg(): Node | null {
    const start = this.i;
    const m = /^\{\s*([A-Za-z_][\w]*)\s*(\}|,)/.exec(this.s.slice(this.i));
    if (!m) return null;
    const name = m[1];
    this.i += m[0].length;
    if (m[2] === '}') return { kind: 'arg', name };
    const typeMatch = /^\s*plural\s*,/.exec(this.s.slice(this.i));
    if (!typeMatch) { this.i = start; return null; }
    this.i += typeMatch[0].length;
    const branches: Record<string, Node[]> = {};
    for (;;) {
      const sel = /^\s*(=\d+|zero|one|two|few|many|other)\s*\{/.exec(this.s.slice(this.i));
      if (!sel) break;
      this.i += sel[0].length;
      branches[sel[1]] = this.parse(true, true);
      if (this.s[this.i] !== '}') { this.i = start; return null; }
      this.i++;
    }
    const close = /^\s*\}/.exec(this.s.slice(this.i));
    if (!close || !branches.other) { this.i = start; return null; }
    this.i += close[0].length;
    return { kind: 'plural', name, branches };
  }
}

const cache = new Map<string, Node[]>();
function parse(template: string): Node[] {
  let nodes = cache.get(template);
  if (!nodes) {
    nodes = new Parser(template).parse(false, false);
    if (cache.size > 2000) cache.clear();
    cache.set(template, nodes);
  }
  return nodes;
}

function pluralCategory(n: number, locale: string): string {
  try { return new Intl.PluralRules(locale).select(n); } catch { return n === 1 ? 'one' : 'other'; }
}

/** Plural forms a locale asks for that an English-shaped translation may not supply. */
const FALLBACK: Record<string, string[]> = { zero: ['other'], one: ['other'], two: ['few', 'many', 'other'], few: ['many', 'other'], many: ['few', 'other'], other: [] };

function render(nodes: Node[], params: MessageParams, locale: string, fmt: (n: number) => string, hash: string | null): string {
  let out = '';
  for (const n of nodes) {
    if (n.kind === 'text') out += n.value;
    else if (n.kind === 'hash') out += hash ?? '#';
    else if (n.kind === 'arg') {
      const v = params[n.name];
      out += v == null ? `{${n.name}}` : typeof v === 'number' ? fmt(v) : v;
    } else {
      const raw = params[n.name];
      const count = typeof raw === 'number' ? raw : Number(raw);
      if (raw == null || Number.isNaN(count)) { out += `{${n.name}}`; continue; }
      const exact = n.branches[`=${count}`];
      const cat = pluralCategory(count, locale);
      const branch = exact ?? [cat, ...(FALLBACK[cat] ?? [])].map((c) => n.branches[c]).find(Boolean) ?? n.branches.other;
      out += render(branch, params, locale, fmt, fmt(count));
    }
  }
  return out;
}

/**
 * Fill a template. `locale` is a BCP-47 tag used for plural rules; `formatNumber` formats numeric
 * values (Latin digits — see lib/format.ts). A placeholder with no value is left as `{name}` so a
 * missing argument is visible in review instead of silently disappearing.
 */
export function formatMessage(template: string, params: MessageParams, locale: string, formatNumber: (n: number) => string): string {
  return render(parse(template), params, locale, formatNumber, null);
}

/** Every argument a template references, including those inside plural branches. */
export function messageArgs(template: string): Set<string> {
  const out = new Set<string>();
  const walk = (nodes: Node[]) => {
    for (const n of nodes) {
      if (n.kind === 'arg') out.add(n.name);
      if (n.kind === 'plural') { out.add(n.name); Object.values(n.branches).forEach(walk); }
    }
  };
  walk(parse(template));
  return out;
}
