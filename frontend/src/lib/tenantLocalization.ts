/**
 * Reading GET /api/tenant-admin/localization. Pure: no React, no axios — TenantSettingsContext
 * fetches, this decides what the answer means (unit/tenantLocalization.spec.ts).
 *
 * `stated` is the API's word on whether the answer is a real tenant's. Before sign-in (no tenant
 * resolvable) the endpoint returns a blank placeholder with `stated: false`; acting on it would
 * treat "no tenant" as "a tenant whose default language is English". Only `stated === true` counts
 * as loaded; an older API that does not send the field is treated as not stated.
 */

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

export const DEFAULTS: TenantSettings = {
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

/** The endpoint's JSON: the settings fields, plus `stated`. */
export type LocalizationAnswer = Partial<TenantSettings> & { stated?: boolean };

/** The settings to show (blanks filled from DEFAULTS) and whether they are the tenant's own. */
export function readLocalization(data: LocalizationAnswer | null | undefined): { settings: TenantSettings; stated: boolean } | null {
  if (!data) return null;
  return {
    stated: data.stated === true,
    settings: {
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
    },
  };
}
