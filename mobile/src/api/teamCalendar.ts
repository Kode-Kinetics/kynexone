// ============================================================
// Team leave calendar (lane B).
//
// GET /api/leave/calendar/team?fromDate&toDate (LeaveCalendarController.Team) — data-scoped
// server-side to the employees the caller may see. It lists APPROVED leave (and legacy
// "Submitted" rows, which the app marks as not yet approved). Requests still waiting for a
// decision are in the Approvals inbox, not here.
// ============================================================

import { apiGet } from './client';
import { flattenTeamCalendar, holidayOn, type OutEntry } from '@/features/approvals/teamModel';

export const teamCalendarApi = {
  async getTeamLeave(fromDate: string, toDate: string): Promise<OutEntry[]> {
    const groups = await apiGet<unknown>(
      `/leave/calendar/team?fromDate=${encodeURIComponent(fromDate)}&toDate=${encodeURIComponent(toDate)}`
    );
    return flattenTeamCalendar(groups);
  },

  /**
   * Is `isoDate` (a Riyadh business date) a public holiday in the tenant's holiday calendar?
   * GET /api/leave/holidays/range — used so a holiday is never counted as an absence.
   */
  async getPublicHoliday(isoDate: string): Promise<{ nameEn: string | null; nameAr: string | null } | null> {
    const rows = await apiGet<unknown>(`/leave/holidays/range?fromDate=${isoDate}&toDate=${isoDate}`);
    return holidayOn(rows, isoDate);
  },
};
