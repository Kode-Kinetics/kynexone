import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import * as ts from 'typescript';
import { FRONTEND_ROOT, HOLE, REPO_ROOT, lineOf, literalsOf, parseSource, rel, walkSource } from './source-scan';
import { SETUP_LEGACY_PATHS } from '../src/lib/setupLegacyRoutes';
import { PEOPLE_DIRECTORY_QUERY_KEYS } from '../src/lib/peopleRoutes';
import { resolveComplianceRoute } from '../src/lib/complianceRoutes';

/**
 * Route wiring (Screen Standard section 8a). Every internal href, router.push/replace, redirect and
 * route-table literal in app/ and src/ must resolve to a page under app/ (route groups stripped,
 * [param] a wildcard) or to a middleware redirect. A link to /people may only carry query keys the
 * directory parser understands: an ignored key lands the user on the full unfiltered list while the
 * card says "complete the missing fields for each blocked employee" (register S1).
 *
 * Links the SERVER hands the UI (Saudi Compliance action centre, setup readiness, module catalog)
 * are scanned in the backend source and must resolve after the same resolver the UI applies.
 *
 * Existing misses are pinned in e2e/route-wiring-baseline.json and may only shrink.
 */

const BASELINE_PATH = path.join(FRONTEND_ROOT, 'e2e/route-wiring-baseline.json');

// ── The route table ────────────────────────────────────────────────────────

function pagePatterns(): string[][] {
  const out: string[][] = [];
  const visit = (dir: string, segs: string[]) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        const group = /^\(.+\)$/.test(entry.name);
        visit(path.join(dir, entry.name), group ? segs : [...segs, entry.name]);
      } else if (entry.name === 'page.tsx' || entry.name === 'route.ts') out.push(segs);
    }
  };
  visit(path.join(FRONTEND_ROOT, 'app'), []);
  return out;
}
const PATTERNS = pagePatterns();
const LEGACY = new Set(SETUP_LEGACY_PATHS);

function segmentsOf(pathname: string): string[] {
  return pathname.split('/').filter(Boolean);
}

function matches(pattern: string[], segs: string[]): boolean {
  for (let i = 0; i < pattern.length; i++) {
    const p = pattern[i];
    if (/^\[\[\.\.\..+\]\]$/.test(p)) return true;
    if (/^\[\.\.\..+\]$/.test(p)) return segs.length > i;
    if (i >= segs.length) return false;
    if (/^\[.+\]$/.test(p) || segs[i].includes(HOLE)) continue;
    if (p !== segs[i]) return false;
  }
  return pattern.length === segs.length;
}

function resolves(pathname: string): boolean {
  if (pathname === '/') return PATTERNS.some(p => p.length === 0);
  const clean = pathname.replace(/\/+$/, '');
  if (LEGACY.has(clean)) return true;
  const segs = segmentsOf(clean);
  return PATTERNS.some(p => matches(p, segs));
}

/** `/people?x=1#h` -> { pathname, query }. Holes survive. */
function split(href: string): { pathname: string; keys: string[] } {
  const noHash = href.split('#')[0];
  const [pathname, query = ''] = noHash.split('?');
  const keys = query.split('&').map(kv => kv.split('=')[0]).filter(k => k && !k.includes(HOLE));
  return { pathname, keys };
}

const isInternalRoute = (v: string) => /^\/(?!\/)/.test(v) && !v.startsWith('/api/') && !/\s/.test(v.split('?')[0]) && !v.startsWith('/_next');

// ── Frontend literals ──────────────────────────────────────────────────────

interface Link { file: string; line: number; href: string }

const ROUTE_PROPS = new Set(['href', 'to', 'route', 'tabHref', 'areaHref', 'redirectTo']);
const NAV_CALLEE = /^(router|navigation|nav)\.(push|replace|prefetch)$|^(redirect|permanentRedirect)$|^(window\.)?location\.(assign|replace)$/;

function frontendLinks(): Link[] {
  const links: Link[] = [];
  for (const file of walkSource()) {
    const sf = parseSource(file);
    const add = (node: ts.Node, expr: ts.Expression | undefined) => {
      for (const href of literalsOf(expr)) if (isInternalRoute(href)) links.push({ file: rel(file), line: lineOf(sf, node), href });
    };
    const visit = (node: ts.Node) => {
      if (ts.isJsxAttribute(node) && ts.isIdentifier(node.name) && ROUTE_PROPS.has(node.name.text) && node.initializer) {
        add(node, ts.isJsxExpression(node.initializer) ? node.initializer.expression : (node.initializer as ts.Expression));
      } else if (ts.isPropertyAssignment(node) && (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name)) && ROUTE_PROPS.has(node.name.text)) {
        add(node, node.initializer);
      } else if (ts.isCallExpression(node) && NAV_CALLEE.test(node.expression.getText(sf))) {
        add(node, node.arguments[0]);
      }
      ts.forEachChild(node, visit);
    };
    visit(sf);
  }
  return links;
}

// ── Links the server supplies ───────────────────────────────────────────────

const TOP_LEVEL = new Set(PATTERNS.map(p => p[0]).filter(Boolean));
// Outbound API clients (the backend calling a bank's HTTP API): their "/payroll/v2" literals are the bank's URL paths, never links
// the server hands the UI. Only this folder is skipped; the ANB client folder is the one outbound client folder that exists.
const OUTBOUND_API_DIRS = ['Infrastructure/Payroll/SaudiBankExports/Api'];
function serverLinks(root = path.join(REPO_ROOT, 'backend-dotnet/Zayra.Api')): Link[] {
  const links: Link[] = [];
  const visit = (dir: string) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (['bin', 'obj', 'Migrations'].includes(entry.name)) continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory() && OUTBOUND_API_DIRS.includes(path.relative(root, full).replaceAll('\\', '/'))) continue;
      if (entry.isDirectory()) visit(full);
      else if (entry.name.endsWith('.cs')) {
        const lines = fs.readFileSync(full, 'utf8').split('\n');
        lines.forEach((line, i) => {
          for (const m of line.matchAll(/"(\/[a-z][a-z0-9-]*(?:\/[^"\s?#]*)?(?:[?#][^"\s]*)?)"/g)) {
            const href = m[1].replace(/\{[^}]*\}/g, HOLE);
            if (href.startsWith('/api/') || !TOP_LEVEL.has(segmentsOf(href.split(/[?#]/)[0])[0])) continue;
            links.push({ file: path.relative(REPO_ROOT, full).replaceAll('\\', '/'), line: i + 1, href });
          }
        });
      }
    }
  };
  visit(root);
  return links;
}

// ── Checks ─────────────────────────────────────────────────────────────────

const show = (l: Link) => `${l.file}:${l.line} ${l.href.replaceAll(HOLE, '${}')}`;
const baseline: { misses: string[] } = JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));

test('the route table is read from app/ (guards the scan itself)', () => {
  expect(PATTERNS.length).toBeGreaterThanOrEqual(100);
  expect(resolves('/people/12/edit')).toBe(true);
  expect(resolves('/setup')).toBe(true);
  expect(resolves('/definitely-not-a-page')).toBe(false);
  expect(resolves('/people/12/nope')).toBe(false);
});

test('every internal link literal in app/ and src/ resolves to a page', () => {
  const links = frontendLinks();
  expect(links.length, 'the scan found the links').toBeGreaterThan(150);
  const misses = links.filter(l => !resolves(split(l.href).pathname)).map(show);
  const fresh = misses.filter(m => !baseline.misses.includes(m));
  expect(fresh, 'links that resolve to no page (fix the link; do not add to the baseline)').toEqual([]);
  const stale = baseline.misses.filter(m => !misses.includes(m));
  expect(stale, 'baseline entries that no longer miss: remove them').toEqual([]);
});

test('a link to /people only uses query keys the directory understands', () => {
  const allowed = new Set([...PEOPLE_DIRECTORY_QUERY_KEYS, 'employeeId', 'tab', 'returnTo', 'section', 'panel', 'mode']);
  const bad: string[] = [];
  for (const l of [...frontendLinks()]) {
    const { pathname, keys } = split(l.href);
    if (pathname === '/people') for (const k of keys) if (!allowed.has(k)) bad.push(`${show(l)} (key "${k}")`);
  }
  expect(bad).toEqual([]);
});

test('server-supplied links resolve to a page, and to a People filter the page understands', () => {
  const links = serverLinks();
  expect(links.length, 'the scan found the server links').toBeGreaterThan(20);
  const allowed = new Set([...PEOPLE_DIRECTORY_QUERY_KEYS, 'employeeId', 'tab']);
  const bad: string[] = [];
  for (const l of links) {
    const resolved = resolveComplianceRoute(l.href);
    const { pathname, keys } = split(resolved);
    if (!resolves(pathname)) bad.push(`${show(l)} resolves to no page`);
    else if (pathname === '/people') for (const k of keys) if (!allowed.has(k)) bad.push(`${show(l)} -> ${resolved}: the People list ignores "${k}"`);
  }
  expect(bad).toEqual([]);
});

test('an outbound bank-API client is not a server-supplied UI link, but a UI link in any other file still is', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'route-wiring-'));
  try {
    const api = path.join(tmp, 'Infrastructure/Payroll/SaudiBankExports/Api');
    const ctl = path.join(tmp, 'Controllers');
    fs.mkdirSync(api, { recursive: true });
    fs.mkdirSync(ctl, { recursive: true });
    fs.writeFileSync(path.join(api, 'Client.cs'), 'var u = endpoint.Url("/payroll/v2");\nvar w = $"/payroll/{id}/WPS";\n');
    fs.writeFileSync(path.join(ctl, 'Thing.cs'), 'var link = "/payroll/v2";\n');
    const found = serverLinks(tmp).map(l => `${l.file.split('/').pop()}:${l.href}`);
    expect(found, 'the outbound client is skipped').not.toContain('Client.cs:/payroll/v2');
    expect(found.some(f => f.startsWith('Client.cs')), 'nothing in the outbound client folder is scanned').toBe(false);
    expect(found, 'a link in any other file is still scanned').toContain('Thing.cs:/payroll/v2');
    // The exclusion is exactly the outbound API client folder: the real scan reads no path from it.
    expect(serverLinks().some(l => l.file.includes('SaudiBankExports/Api/'))).toBe(false);
    expect(OUTBOUND_API_DIRS).toEqual(['Infrastructure/Payroll/SaudiBankExports/Api']);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('server-supplied People links carry NO readiness or gap filter until the backend exposes ?gap=', () => {
  // `readiness=Blocked` is the activation gate and `NotReady` a superset, so neither selects the population the card counts.
  // Backend ask: GET /api/employees?gap=qiwa_blocked|missing_iban|missing_gosi_ref, then map the three links onto it.
  const people = serverLinks().map(l => resolveComplianceRoute(l.href)).filter(h => split(h).pathname === '/people');
  expect(people.length).toBeGreaterThanOrEqual(3);
  for (const href of people) {
    expect(href, href).not.toMatch(/[?&](readiness|gapType|filter|gap)=/);
  }
});

test('the Saudi Compliance action centre applies that resolver to every server link', () => {
  const src = fs.readFileSync(path.join(FRONTEND_ROOT, 'src/views/SaudiComplianceDashboard.tsx'), 'utf8');
  expect(src).toContain('resolveComplianceRoute(item.route)');
});
