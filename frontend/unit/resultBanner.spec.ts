import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { renderComponent } from './support/ssr';

/** ResultBanner (Screen Standard section 3d): what changed, where you are, one next action, in a live region. */
const render = (props: Record<string, unknown>) => renderComponent('src/components/ui/ResultBanner.tsx', 'ResultBanner', props);

test('it is a polite live region marked for the screen contract, with the three parts', () => {
  const html = render({
    changed: '3 changes saved: Grade, Housing, Probation end',
    where: 'You are on the employee record.',
    next: { label: 'Review who is affected', href: '/people/12' },
  });
  expect(html).toContain('role="status"');
  expect(html).toContain('data-result');
  expect(html).toContain('tabindex="-1"');
  expect(html).toContain('3 changes saved: Grade, Housing, Probation end');
  expect(html).toContain('You are on the employee record.');
  expect(html).toContain('Review who is affected');
});

test('exactly one next action: a link when it has an href, a button when it has a handler', () => {
  const link = render({ changed: 'Saved', next: { label: 'Open the record', href: '/people/12' } });
  expect(link.match(/<a /g)).toHaveLength(1);
  expect(link).toContain('href="/people/12"');
  expect(link).not.toContain('<button');
  const button = render({ changed: 'Saved', next: { label: 'Review who is affected', onClick: () => undefined } });
  expect(button.match(/<button/g)).toHaveLength(1);
  expect(button).toContain('data-result-next');
});

test('a pending approval is stated, and no next action is drawn unless the caller gives one', () => {
  const pending = render({ changed: 'Salary change submitted', pending: 'It goes to Finance and applies on 1 Dec after approval.' });
  expect(pending).toContain('data-result-pending');
  expect(pending).toContain('applies on 1 Dec after approval.');
  expect(pending).not.toContain('data-result-next');
});

test('it is kept until dismissed: a dismiss button exists only when the caller can dismiss it', () => {
  expect(render({ changed: 'Saved' })).not.toContain('aria-label="Dismiss"');
  expect(render({ changed: 'Saved', onDismiss: () => undefined })).toContain('aria-label="Dismiss"');
});

test('focus moves to it when it appears', () => {
  const src = readFileSync(join(__dirname, '..', 'src', 'components', 'ui', 'ResultBanner.tsx'), 'utf8');
  expect(src).toMatch(/useEffect\(\(\) => \{ ref\.current\?\.focus\(\); \}, \[\]\)/);
});
