import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import tailwind from '../tailwind.config';

/**
 * Motion tokens (Screen Standard section 4). Three durations, two easings, defined once in
 * styles/index.css and mapped into Tailwind, so `duration-fast|base|slow` is the only vocabulary.
 * Reduced motion is handled once, at the root. The ratchet metrics (scripts/design-token-ratchet.mjs)
 * may only go down; 3D tilt is gone.
 */

const root = join(__dirname, '..');
const css = readFileSync(join(root, 'src', 'styles', 'index.css'), 'utf8');

function rootBlock(): string {
  // The first plain `:root {` block that defines the wg-dur tokens.
  const m = css.match(/:root\s*\{[^}]*--wg-dur-fast:[^}]*\}/);
  expect(m, 'a :root block that defines --wg-dur-fast').not.toBeNull();
  return m![0];
}

test('three duration tokens and two easings are defined once', () => {
  const block = rootBlock();
  expect(block).toMatch(/--wg-dur-fast:\s*120ms;/);
  expect(block).toMatch(/--wg-dur-base:\s*200ms;/);
  expect(block).toMatch(/--wg-dur-slow:\s*280ms;/);
  expect(block).toMatch(/--wg-ease:\s*cubic-bezier\(0\.2,\s*0,\s*0,\s*1\);/);
  expect(block).toMatch(/--wg-ease-drawer:\s*cubic-bezier\(0\.32,\s*0\.72,\s*0,\s*1\);/);
  // No UI motion exceeds 300 ms.
  for (const m of block.matchAll(/--wg-dur-[a-z]+:\s*(\d+)ms/g)) expect(Number(m[1])).toBeLessThanOrEqual(300);
});

test('the six legacy durations are folded onto the three tokens', () => {
  const all = [...css.matchAll(/--wg-dur-([a-z]+):\s*([^;]+);/g)].map(m => [m[1], m[2].trim()] as const);
  const literal = all.filter(([, v]) => /^\d+ms$/.test(v)).map(([k]) => k).sort();
  expect(literal, 'only fast, base and slow may hold a literal time').toEqual(['base', 'fast', 'slow']);
  expect(css).not.toMatch(/--wg-dur-pulse/);
});

test('reduced motion zeroes the tokens, runs animations once and keeps essential motion', () => {
  const m = css.match(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*:root\s*\{([^}]*)\}([\s\S]*?)\n\}/);
  expect(m, 'a root-level reduced-motion block that overrides :root').not.toBeNull();
  const [, vars, rest] = m!;
  for (const token of ['fast', 'base', 'slow']) expect(vars).toMatch(new RegExp(`--wg-dur-${token}:\\s*0\\.01ms;`));
  expect(rest).toMatch(/animation-iteration-count:\s*1\s*!important/);
  expect(rest).toMatch(/scroll-behavior:\s*auto/);
  expect(rest).toContain('data-essential-motion');
});

test('tailwind maps duration-fast|base|slow and ease-standard|drawer onto the tokens', () => {
  const ext = (tailwind.theme as unknown as { extend: Record<string, Record<string, string>> }).extend;
  expect(ext.transitionDuration).toEqual({ fast: 'var(--wg-dur-fast)', base: 'var(--wg-dur-base)', slow: 'var(--wg-dur-slow)' });
  expect(ext.transitionTimingFunction).toEqual({ standard: 'var(--wg-ease)', drawer: 'var(--wg-ease-drawer)' });
});

test('3D is banned: no wg-tile3d rule and no use', () => {
  expect(css).not.toContain('wg-tile3d');
  const activity = readFileSync(join(root, 'src', 'components', 'dashboard', 'ActivityPanel.tsx'), 'utf8');
  expect(activity).not.toContain('wg-tile3d');
  expect(activity).toMatch(/hover:border-line-strong/);
});

test('the ratchet counts motion metrics and has baselines for them', async () => {
  const nativeImport = new Function('u', 'return import(u)') as (u: string) => Promise<{ count: () => Record<string, number>; METRICS: Record<string, RegExp> }>;
  const { count, METRICS } = await nativeImport(pathToFileURL(join(root, 'scripts', 'design-token-ratchet.mjs')).href);
  const baseline = JSON.parse(readFileSync(join(root, 'scripts', 'design-token-baseline.json'), 'utf8')) as Record<string, number>;
  const keys = ['rawDurationClass', 'animatePulse', 'animatePing', 'tile3d'];
  for (const k of keys) {
    expect(METRICS[k], `${k} is a ratchet metric`).toBeInstanceOf(RegExp);
    expect(baseline[k], `${k} has a pinned baseline`).toBeGreaterThanOrEqual(0);
  }
  expect('duration-150'.match(METRICS.rawDurationClass)).toHaveLength(1);
  expect('duration-fast duration-base duration-slow'.match(METRICS.rawDurationClass)).toBeNull();
  expect('animate-pulse animate-pulse-slow'.match(METRICS.animatePulse)).toHaveLength(1);
  expect(count().tile3d).toBe(0);
});
