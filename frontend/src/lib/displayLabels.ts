import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

/** "3y" → "3 yrs" / "3 سنوات": years of experience as a pluralised, translated phrase. */
export function yearsLabel(t: Translator, years: number | null | undefined): string {
  return t('{years, plural, one {# yr} other {# yrs}}', { years: years ?? 0 });
}

/** A payslip or document language code ("en", "AR") as its name in the viewer's language. */
export function languageLabel(t: Translator, code: string | null | undefined): string {
  const c = (code ?? '').trim().toLowerCase();
  if (c === 'en') return t('English');
  if (c === 'ar') return t('Arabic');
  if (c === 'fr') return t('French');
  return (code ?? '').trim().toUpperCase();
}
