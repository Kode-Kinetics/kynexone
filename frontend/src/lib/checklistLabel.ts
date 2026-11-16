import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

/**
 * One option in the onboarding-checklist picker: the checklist's name only. The internal code
 * ("CL-010B9CCB8BBA") means nothing to the person choosing. A built-in template name is translated;
 * a name the company typed is shown as typed.
 */
export function checklistOptionLabel(t: Translator, c: { name: string; isActive: boolean; code?: string }): string {
  const name = t(c.name);
  return c.isActive ? name : `${name} (${t('Off')})`;
}
