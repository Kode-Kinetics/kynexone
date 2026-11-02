import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import tailwind from '../tailwind.config';

/**
 * Design-token contract. Parses src/styles/tokens.css (the single source of truth for colour
 * roles) and asserts WCAG 2.2 AA for every text/surface and control/surface pair, in both themes,
 * so a token edit that makes the product less readable fails here, not in an audit. Also runs the
 * token ratchet: counts of off-system styling may only go down.
 */

const css = readFileSync(join(__dirname, '..', 'src', 'styles', 'tokens.css'), 'utf8');

function block(selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  expect(start, `${selector} block`).toBeGreaterThanOrEqual(0);
  const body = css.slice(start, css.indexOf('\n}', start));
  const out: Record<string, string> = {};
  for (const m of body.matchAll(/--kx-([a-z0-9-]+):\s*(\d+) (\d+) (\d+);/g)) {
    out[m[1]] = '#' + [m[2], m[3], m[4]].map(n => Number(n).toString(16).padStart(2, '0')).join('').toUpperCase();
  }
  return out;
}

const light = block(':root');
const dark = { ...light, ...block(':root.dark') };

function luminance(hex: string): number {
  const c = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}
export function ratio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const SURFACES = ['canvas', 'surface', 'surface-sunken', 'overlay'];
const STATUS = ['primary', 'success', 'warning', 'danger', 'info'];

const themes = { light, dark } as const;
const report: string[] = [];

for (const [name, t] of Object.entries(themes)) {
  test.describe(`${name} theme contrast`, () => {
    for (const surface of SURFACES) {
      for (const role of ['text', 'text-muted', 'text-subtle']) {
        test(`${role} on ${surface} >= 4.5`, () => {
          const r = ratio(t[role], t[surface]);
          report.push(`${name} ${role} on ${surface}: ${r.toFixed(2)}`);
          expect(r).toBeGreaterThanOrEqual(4.5);
        });
      }
      test(`border-strong (input boundary) on ${surface} >= 3`, () => {
        expect(ratio(t['border-strong'], t[surface])).toBeGreaterThanOrEqual(3);
      });
      test(`focus ring on ${surface} >= 3`, () => {
        expect(ratio(t.focus, t[surface])).toBeGreaterThanOrEqual(3);
      });
      for (const role of STATUS) {
        test(`${role}-fg on ${surface} >= 4.5`, () => {
          expect(ratio(t[`${role === 'primary' ? 'primary' : role}-fg`], t[surface])).toBeGreaterThanOrEqual(4.5);
        });
      }
    }
    for (const role of STATUS) {
      test(`${role}-fg on ${role}-soft >= 4.5`, () => {
        const r = ratio(t[`${role}-fg`], t[`${role}-soft`]);
        report.push(`${name} ${role}-fg on ${role}-soft: ${r.toFixed(2)}`);
        expect(r).toBeGreaterThanOrEqual(4.5);
      });
      test(`on-${role} text on ${role} fill >= 4.5`, () => {
        const r = ratio(t[`on-${role}`], t[role]);
        report.push(`${name} on-${role} on ${role}: ${r.toFixed(2)}`);
        expect(r).toBeGreaterThanOrEqual(4.5);
      });
    }
    test('primary-hover keeps white text >= 4.5', () => {
      expect(ratio(t['on-primary'], t['primary-hover'])).toBeGreaterThanOrEqual(4.5);
    });
    for (const key of ['t-slate-400', 't-slate-500', 't-sapphire', 't-emerald-600', 't-amber-600', 't-rose-500', 't-rose-600']) {
      // Text remaps are used as text on the page surfaces and on the soft tints.
      test(`palette text remap ${key} >= 4.5 on surface and canvas`, () => {
        expect(ratio(t[key], t.surface)).toBeGreaterThanOrEqual(4.5);
        expect(ratio(t[key], t.canvas)).toBeGreaterThanOrEqual(4.5);
      });
    }
  });
}

test('the permanently-dark scopes (platform shell, login) read dark-theme text remaps', () => {
  const scoped = block('.platform-shell,\n.tenant-login-shell');
  for (const key of ['t-slate-400', 't-slate-500']) {
    expect(ratio(scoped[key], '#0A0F1A')).toBeGreaterThanOrEqual(4.5);
  }
});

test('control heights are 28 / 36 / 40, and 44 on coarse pointers', () => {
  expect(css).toMatch(/--kx-control-sm: 1\.75rem/);
  expect(css).toMatch(/--kx-control-md: 2\.25rem/);
  expect(css).toMatch(/--kx-control-lg: 2\.5rem/);
  expect(css).toMatch(/@media \(pointer: coarse\)[\s\S]*--kx-control-md: 2\.75rem/);
});

test('type scale has no size under 11px and weights stop at semibold', () => {
  const theme = tailwind.theme as { fontWeight: Record<string, string>; extend: { fontSize: Record<string, [string, unknown]> } };
  for (const [name, [size]] of Object.entries(theme.extend.fontSize)) {
    const px = parseFloat(size) * (size.endsWith('rem') ? 16 : 1);
    expect(px, `${name}`).toBeGreaterThanOrEqual(11);
  }
  expect(Math.max(...Object.values(theme.fontWeight).map(Number))).toBeLessThanOrEqual(600);
});

test('radii are the 4 / 8 / 12 / 16 scale plus full', () => {
  const radius = (tailwind.theme as { borderRadius: Record<string, string> }).borderRadius;
  expect(new Set(Object.values(radius))).toEqual(new Set(['0px', '4px', '8px', '12px', '16px', '9999px']));
});

test('elevation never uses a coloured shadow', () => {
  for (const level of ['--kx-e1', '--kx-e2', '--kx-e3']) {
    const m = css.match(new RegExp(`${level}: ([^;]+);`));
    expect(m?.[1], level).toMatch(/rgb\((15 23 42|0 0 0) \//);
  }
});

test('index.css no longer carries !important colour overrides', () => {
  const index = readFileSync(join(__dirname, '..', 'src', 'styles', 'index.css'), 'utf8');
  const colourImportant = index.split('\n').filter(l => /!important/.test(l) && /(^|\s)(color|background(-color)?)\s*:/.test(l) && !/CanvasText|LinkText|background-image/.test(l));
  expect(colourImportant).toEqual([]);
});

test('design-token ratchet: off-system counts may only go down', async () => {
  const baseline = JSON.parse(readFileSync(join(__dirname, '..', 'scripts', 'design-token-baseline.json'), 'utf8')) as Record<string, number>;
  // Native ESM import (the test runner would otherwise transpile the .mjs into CJS).
  const nativeImport = new Function('u', 'return import(u)') as (u: string) => Promise<{ count: () => Record<string, number> }>;
  const { count } = await nativeImport(pathToFileURL(join(__dirname, '..', 'scripts', 'design-token-ratchet.mjs')).href);
  const now = count();
  for (const key of Object.keys(baseline)) {
    expect(now[key], `${key} (baseline ${baseline[key]}); run scripts/design-token-ratchet.mjs --update after lowering`).toBeLessThanOrEqual(baseline[key]);
  }
});

test.afterAll(() => {
  // Recorded for the review: node unit run prints these with --reporter=list via console.
  console.log(report.sort().join('\n'));
});
