import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  ACCESSIBLE_LAYOUT,
  columnsFor,
  resolveAccessibleLayout,
} from '../src/theme/accessibleLayout.ts';

test('iPhone 14 tabs fit at default text and become scrollable at accessibility sizes', () => {
  assert.equal(columnsFor(390, 1, 156), 2);
  assert.equal(resolveAccessibleLayout(390, 3.1, false).shouldStack, true);
});

test('RTL retains logical route ordering and accessible touch targets', () => {
  const layout = resolveAccessibleLayout(390, 1, true);

  assert.equal(layout.rowDirection, 'row');
  assert.equal(layout.textAlign, 'right');
  assert.equal(layout.minimumTouchTarget, 44);
  assert.equal(ACCESSIBLE_LAYOUT.minimumTouchTarget, 44);
});

test('bottom navigation exposes every route through an adaptive horizontal rail', () => {
  const source = readFileSync(
    new URL('../src/navigation/LiquidTabBar.tsx', import.meta.url),
    'utf8',
  );

  assert.match(source, /scrollEnabled=\{shouldStack\}/);
  assert.match(source, /x:\s*state\.index\s*\*\s*itemWidth/);
  assert.match(source, /testID=\{`tab-\$\{route\.name\}`\}/);
});

test('login uses a fifty-percent translucent glass form surface', () => {
  const source = readFileSync(
    new URL('../src/features/auth/LoginScreen.tsx', import.meta.url),
    'utf8',
  );

  assert.match(source, /glass/);
  assert.match(source, /translucentOpacity=\{0\.5\}/);
  assert.match(source, /accessibilityRole="header"/);
});
