// ============================================================
// useFormat() — the one way screens format dates, times, money and numbers.
//
//   const f = useFormat();
//   f.date(payslip.payDate, 'long')   // 10 October 2026 / ١٠ أكتوبر ٢٠٢٦
//   f.time(record.clockIn)            // company time zone (Asia/Riyadh), not the device's
//   f.money(net)                      // SAR 12,500.00 / 12,500.00 ر.س.
//   f.hijri(day)                      // secondary line; '' when unavailable — hide the line
//
// Pure logic lives in formatCore.ts (unit-tested).
// ============================================================

import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { formatDistanceToNowStrict } from 'date-fns';
import { ar as arLocale, enUS } from 'date-fns/locale';
import { useAuthStore } from '@/auth/authStore';
import {
  createFormatter,
  parseDateInput,
  EMPTY_VALUE,
  type DateInput,
  type FormatLanguage,
} from './formatCore';

export * from './formatCore';

/** The company zone if the session carries one (future API field), else Asia/Riyadh. */
function useCompanyTimeZone(): string | undefined {
  return useAuthStore((state) => {
    const user = state.user as unknown as { timeZone?: string; companyTimeZone?: string } | null;
    return user?.companyTimeZone ?? user?.timeZone ?? undefined;
  });
}

export function useFormat() {
  const { i18n } = useTranslation();
  const language: FormatLanguage = i18n.language?.startsWith('ar') ? 'ar' : 'en';
  const timeZone = useCompanyTimeZone();

  return useMemo(() => {
    const core = createFormatter({ language, timeZone });
    /** "3 hours ago" / "منذ ٣ ساعات". Instants only. */
    const relative = (value: DateInput): string => {
      const p = parseDateInput(value);
      if (!p || p.kind !== 'instant') return EMPTY_VALUE;
      return formatDistanceToNowStrict(p.date, {
        addSuffix: true,
        locale: language === 'ar' ? arLocale : enUS,
      });
    };
    return { ...core, relative };
  }, [language, timeZone]);
}

export type UseFormat = ReturnType<typeof useFormat>;
