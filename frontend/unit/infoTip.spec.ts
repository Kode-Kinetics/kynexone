import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { translate } from '../src/i18n/translations';
import { FIELD_KEY_PERMISSION, INFO_TIP_MAX_WIDTH, canSeeFieldKey, flipsAtViewportEdge, resolveInfoText } from '../src/lib/infoTip';
import { renderComponent } from './support/ssr';

/**
 * The `i` affordance (Screen Standard 3b): a real button, described by its popover, English and Arabic through t(),
 * 24 px (44 on coarse pointers), dismissible with Esc, hoverable, and no developer `key:` line for ordinary users.
 */
const render = (props: Record<string, unknown>) => renderComponent('src/components/InfoTip.tsx', 'InfoTip', props);
const source = readFileSync(join(__dirname, '..', 'src', 'components', 'InfoTip.tsx'), 'utf8');

test('it is a button whose accessible name says what it is about, described by its popover', () => {
  const html = render({ text: 'Sets the benefits and loan limits this employee is entitled to.', label: 'Grade', fieldKey: 'employees.grade' });
  expect(html).toContain('<button type="button"');
  expect(html).toContain('aria-label="About Grade"');
  const describedBy = /aria-describedby="([^"]+)"/.exec(html)?.[1];
  expect(describedBy).toBeTruthy();
  expect(html).toContain(`id="${describedBy}"`);
  expect(html).toContain('role="tooltip"');
  expect(html).toContain('Sets the benefits and loan limits this employee is entitled to.');
});

test('without a label it still has a name', () => {
  expect(render({ text: 'Something useful.' })).toContain('aria-label="More information"');
});

test('the developer `key:` line is never printed to ordinary users', () => {
  const html = render({ text: 'Text.', fieldKey: 'employees.grade' });
  expect(html).not.toContain('employees.grade');
  expect(html).not.toMatch(/key:/i);
});

test('only the Setup permission sees the field key (the one that edits per-company help text)', () => {
  expect(FIELD_KEY_PERMISSION).toBe('security.manage');
  expect(canSeeFieldKey(() => true, 'employees.grade')).toBe(true);
  expect(canSeeFieldKey(permission => permission === 'employees.write', 'employees.grade')).toBe(false);
  expect(canSeeFieldKey(() => true, undefined)).toBe(false);
  expect(canSeeFieldKey(null, 'employees.grade')).toBe(false);
});

test('a per-company override replaces the built-in text and is shown as the company wrote it; the built-in goes through t()', () => {
  const t = (key: string) => (key === 'Built in.' ? 'مدمج.' : key);
  expect(resolveInfoText('Built in.', 'Company wording.', t)).toBe('Company wording.');
  expect(resolveInfoText('Built in.', undefined, t)).toBe('مدمج.');
  expect(resolveInfoText('Built in.', '   ', t)).toBe('مدمج.');
});

test('English and Arabic exist for the labels it prints', () => {
  for (const key of ['About {field}', 'More information', 'Field key']) {
    expect(translate('ar', key, { field: 'x' }), key).not.toBe(key);
  }
  expect(translate('ar', 'About {field}', { field: 'الدرجة' })).toContain('الدرجة');
  expect(source).toMatch(/t\('About \{field\}'/);
  expect(source).toMatch(/resolveInfoText\(text,/);
});

test('the popover is at most 280 px, anchored to the inline-start edge, and flips at the viewport edge', () => {
  expect(INFO_TIP_MAX_WIDTH).toBe(280);
  expect(source).toContain('max-w-[280px]');
  expect(source).toMatch(/start-0/);
  // LTR: the popover grows to the right; it flips when its right edge passes the viewport.
  expect(flipsAtViewportEdge({ left: 900, right: 1220 }, 1200, 'ltr')).toBe(true);
  expect(flipsAtViewportEdge({ left: 100, right: 380 }, 1200, 'ltr')).toBe(false);
  // RTL grows to the left; it flips when its left edge passes 0.
  expect(flipsAtViewportEdge({ left: -12, right: 268 }, 1200, 'rtl')).toBe(true);
  expect(flipsAtViewportEdge({ left: 400, right: 680 }, 1200, 'rtl')).toBe(false);
});

test('hit area is 24 px, 44 on coarse pointers; it opens on hover, focus and tap and Esc dismisses it', () => {
  expect(source).toContain('h-6 w-6');
  expect(source).toContain('[@media(pointer:coarse)]:h-11');
  expect(source).toContain('[@media(pointer:coarse)]:w-11');
  for (const handler of ['onMouseEnter', 'onMouseLeave', 'onFocus', 'onBlur', 'onClick']) expect(source, handler).toContain(handler);
  expect(source).toContain("'Escape'");
  // WCAG 1.4.13: the popover must stay open while hovered, so it is a child of the hover target and not pointer-events-none.
  expect(source).not.toContain('pointer-events-none');
});
