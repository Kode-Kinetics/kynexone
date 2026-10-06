'use client';

import { useMemo } from 'react';
import { useLocale } from '../contexts/LocaleContext';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { createFormatter, type Formatter } from '../lib/format';

/**
 * The formatter for the current viewer and tenant: UI language from LocaleContext; zone, date
 * pattern, leading calendar and dual-date preference from the tenant's localization settings.
 *
 * Money has NO implicit currency here: TenantSettingsContext falls back to 'USD' before the
 * settings load, and a guessed label is worse than none. Pass the currency you resolved (the
 * run's company currency, the loan's currency) to `money()`; bare amounts are shown otherwise.
 */
export function useFormat(): Formatter {
  const { locale } = useLocale();
  const { defaultTimezone, dateFormat, calendarSystem, hijriDatesEnabled } = useTenantSettings();
  return useMemo(
    () => createFormatter({ locale, timeZone: defaultTimezone, dateFormat, calendarSystem, hijriDatesEnabled, currency: null }),
    [locale, defaultTimezone, dateFormat, calendarSystem, hijriDatesEnabled],
  );
}
