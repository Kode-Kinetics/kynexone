import { expect, test } from '@playwright/test';
import { createElement } from 'react';
import { renderComponent } from './support/ssr';

/**
 * The one shared Field (Screen Standard 3b, 8d): label bound to its control, required mark, the `i`, the visible
 * "no `i`" decision, hint, description, error, and an inline layout.
 */
const field = (props: Record<string, unknown>, control: ReturnType<typeof createElement> = createElement('input', { type: 'text', name: 'x' })) =>
  renderComponent('src/components/ui/Field.tsx', 'Field', { ...props, children: control });

test('the label is bound to the control it labels', () => {
  const html = field({ label: 'Joining date' });
  const id = /<input[^>]*id="([^"]+)"/.exec(html)?.[1];
  expect(id).toBeTruthy();
  expect(html).toContain(`<label for="${id}">Joining date</label>`);
});

test('it finds the control through wrappers and keeps an id the control already has', () => {
  const wrapped = field({ label: 'Grade' }, createElement('div', null, createElement('select', { id: 'grade-select' })));
  expect(wrapped).toContain('<label for="grade-select">Grade</label>');
});

test('required draws the mark and sets aria-required on the control', () => {
  const html = field({ label: 'Name', required: true });
  expect(html).toContain('aria-required="true"');
  expect(html).toMatch(/<label for="[^"]+">Name <span[^>]*>\*<\/span><\/label>/);
});

test('info puts the `i` next to the label, named after the label; none without it', () => {
  const withInfo = field({ label: 'Grade', info: 'Sets the benefits this employee is entitled to.', infoKey: 'employees.grade' });
  expect(withInfo).toContain('aria-label="About Grade"');
  expect(withInfo).toContain('Sets the benefits this employee is entitled to.');
  expect(field({ label: 'Name' })).not.toContain('role="tooltip"');
});

test('noInfo is a visible, recorded decision and draws no `i`', () => {
  const html = field({ label: 'Phone', noInfo: 'plain text' });
  expect(html).toContain('data-no-info="plain text"');
  expect(html).not.toContain('role="tooltip"');
  // An `i` wins over an exemption: the field is not recorded as exempt when it has help.
  expect(field({ label: 'Grade', info: 'Text.', noInfo: 'x' })).not.toContain('data-no-info');
});

test('hint and description are tied to the control and visible', () => {
  const html = field({ label: 'IBAN', hint: 'SA followed by 22 digits.', description: 'Where salary is paid.' });
  expect(html).toContain('SA followed by 22 digits.');
  expect(html).toContain('Where salary is paid.');
  const describedBy = /<input[^>]*aria-describedby="([^"]+)"/.exec(html)?.[1] ?? '';
  expect(describedBy.split(' ')).toHaveLength(2);
  for (const id of describedBy.split(' ')) expect(html).toContain(`id="${id}"`);
});

test('an error sits under the control, is announced, and marks the control invalid', () => {
  const html = field({ label: 'IBAN', error: 'Enter a Saudi IBAN: SA followed by 22 digits.' });
  expect(html).toContain('aria-invalid="true"');
  expect(html).toMatch(/role="alert"[^>]*>Enter a Saudi IBAN/);
  expect(html.indexOf('<input')).toBeLessThan(html.indexOf('role="alert"'));
  expect(field({ label: 'IBAN' })).not.toContain('aria-invalid');
});

test('layout and size options', () => {
  expect(field({ label: 'Active', layout: 'inline' })).toContain('justify-between');
  expect(field({ label: 'Name', size: 'md' })).toContain('text-sm font-medium');
  expect(field({ label: 'Name' })).toContain('text-xs font-medium');
  // Spacing follows the standard: label to control is 6 px (mb-1.5).
  expect(field({ label: 'Name' })).toContain('mb-1.5');
});

test('a single custom control that is not a native input still gets the id the label points at', () => {
  const custom = createElement('div', { 'data-picker': true });
  const html = field({ label: 'Employee' }, custom);
  const id = /<div[^>]*data-picker[^>]*id="([^"]+)"|<div[^>]*id="([^"]+)"[^>]*data-picker/.exec(html);
  expect(id).toBeTruthy();
  expect(html).toContain(`<label for="${id?.[1] ?? id?.[2]}">Employee</label>`);
});
