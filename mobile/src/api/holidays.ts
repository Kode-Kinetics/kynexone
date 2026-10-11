// Public holidays for the signed-in tenant. Owned by lane A1.
//
// GET /api/leave/holidays/range?fromDate&toDate (HolidayCalendarController.Range) returns the
// tenant's PublicHoliday rows in date order: { id, nameEn, nameAr, date, hijriDate, isOptional,
// holidayType }. An empty list means HR has not loaded a holiday calendar — the Home card hides.

import { apiGet } from './client';
import { mapHolidays, type PublicHolidayItem } from './homeMappers';

export type { PublicHolidayItem } from './homeMappers';

export const holidaysApi = {
  async getRange(fromDate: string, toDate: string): Promise<PublicHolidayItem[]> {
    const params = new URLSearchParams({ fromDate, toDate });
    return mapHolidays(await apiGet<unknown>(`/leave/holidays/range?${params.toString()}`));
  },
};
