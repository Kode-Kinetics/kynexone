'use client';

import { createContext, useContext, useEffect, useState, useCallback } from 'react';
import client from '../api/client';

export interface TenantSettings {
  currencyCode: string;
  countryCode: string;
  defaultTimezone: string;
  dateFormat: string;
  workWeek: string;
  weekStartDay: string;
  defaultLanguage: string;
  rtlEnabled: boolean;
  calendarSystem: string;
  hijriDatesEnabled: boolean;
}

const DEFAULTS: TenantSettings = {
  currencyCode: 'USD',
  countryCode: 'US',
  // EMPTY, never a zone. This used to be 'America/New_York', which meant a tenant whose zone was
  // unknown — the API still loading, the request failing, or the tenant having no localization row
  // — silently rendered its clocks in US Eastern. For a GCC customer that is 7-11 hours out, and on
  // the HR Command Center header it showed the wrong DAY. Empty means "unknown", and every consumer
  // passes it to Intl as `undefined`, which renders in the VIEWER's own browser zone: still not the
  // tenant's stated zone, but never a foreign one, and never silently wrong by a day.
  defaultTimezone: '',
  dateFormat: 'MM/DD/YYYY',
  workWeek: 'Mon-Fri',
  weekStartDay: 'Monday',
  defaultLanguage: 'en',
  rtlEnabled: false,
  calendarSystem: 'Gregorian',
  hijriDatesEnabled: false,
};

interface TenantSettingsContextValue {
  settings: TenantSettings;
  reload: () => Promise<void>;
}

const TenantSettingsContext = createContext<TenantSettingsContextValue>({
  settings: DEFAULTS,
  reload: async () => {},
});

export function TenantSettingsProvider({ children }: { children: React.ReactNode }) {
  const [settings, setSettings] = useState<TenantSettings>(DEFAULTS);

  const load = useCallback(async () => {
    try {
      const { data } = await client.get<Partial<TenantSettings>>('/api/tenant-admin/localization');
      if (data) {
        setSettings({
          currencyCode: data.currencyCode || DEFAULTS.currencyCode,
          countryCode: data.countryCode || DEFAULTS.countryCode,
          defaultTimezone: data.defaultTimezone || DEFAULTS.defaultTimezone,
          dateFormat: data.dateFormat || DEFAULTS.dateFormat,
          workWeek: data.workWeek || DEFAULTS.workWeek,
          weekStartDay: data.weekStartDay || DEFAULTS.weekStartDay,
          defaultLanguage: data.defaultLanguage || DEFAULTS.defaultLanguage,
          rtlEnabled: data.rtlEnabled ?? DEFAULTS.rtlEnabled,
          calendarSystem: data.calendarSystem || DEFAULTS.calendarSystem,
          hijriDatesEnabled: data.hijriDatesEnabled ?? DEFAULTS.hijriDatesEnabled,
        });
      }
    } catch {
      // Fail silently — use defaults. Happens on first load before auth token is set.
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <TenantSettingsContext.Provider value={{ settings, reload: load }}>
      {children}
    </TenantSettingsContext.Provider>
  );
}

export function useTenantSettings(): TenantSettings {
  return useContext(TenantSettingsContext).settings;
}

export function useTenantSettingsContext(): TenantSettingsContextValue {
  return useContext(TenantSettingsContext);
}
