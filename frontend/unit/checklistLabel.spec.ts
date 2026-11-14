import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { checklistOptionLabel } from '../src/lib/checklistLabel';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);

test.describe('onboarding checklist option', () => {
  const starter = { code: 'CL-010B9CCB8BBA', name: 'Saudi Arabia starter checklist', isActive: true };

  test('shows the name only, never the internal code', () => {
    expect(checklistOptionLabel(en, starter)).toBe('Saudi Arabia starter checklist');
    expect(checklistOptionLabel(ar, starter)).not.toContain('CL-');
  });

  test('translates the known starter template name', () => {
    expect(checklistOptionLabel(ar, starter)).toMatch(/[؀-ۿ]/);
  });

  test("a tenant's own checklist name is kept as typed; an inactive one is marked", () => {
    expect(checklistOptionLabel(ar, { code: 'CL-1', name: 'Sales hires', isActive: false })).toBe(`Sales hires (${ar('Off')})`);
  });
});
