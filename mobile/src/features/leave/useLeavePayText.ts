import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useFormat } from '@/utils/format';
import { splitMinutes } from '@/features/overtime/overtimeLogic';
import { classifyLeavePayError, errorShape, pluralCategory } from './leavePayText';

type PluralGroup = 'days' | 'calendarDays' | 'daysLeft' | 'instalmentsLeft';

/** Counts, durations, names and error messages for the Leave / Overtime / Payslip screens. */
export function useLeavePayText() {
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const language: 'en' | 'ar' = i18n.language?.startsWith('ar') ? 'ar' : 'en';

  return useMemo(() => {
    const count = (group: PluralGroup, n: number) =>
      t(`leavePay.plural.${group}.${pluralCategory(language, n)}`, { n: f.number(n) });

    /** Day counts: 0.5 reads as "Half a day", never "0.5 days". */
    const days = (n: number) => (n === 0.5 ? t('leavePay.halfDay') : count('days', n));

    const duration = (minutes: number) => {
      const { h, m } = splitMinutes(minutes);
      if (h && m) return t('leavePay.duration.hm', { h: f.number(h), m: f.number(m) });
      if (h) return t('leavePay.duration.h', { h: f.number(h) });
      return t('leavePay.duration.m', { m: f.number(m) });
    };

    /** The server's own English/Arabic pair: Arabic when the UI is Arabic and one exists. */
    const name = (en: string | null | undefined, ar: string | null | undefined) =>
      (language === 'ar' && ar ? ar : en) ?? '';

    const error = (err: unknown) => t(`leavePay.errors.${classifyLeavePayError(errorShape(err))}`);

    const dateRange = (start: string, end: string, style: 'medium' | 'dayMonth' = 'medium') =>
      start === end || !end
        ? f.date(start, style)
        : t('leavePay.common.dateRange', { start: f.date(start, style), end: f.date(end, style) });

    return { language, count, days, duration, name, error, dateRange };
  }, [f, language, t]);
}
