// The Home screen's supporting reads, beside the ESS dashboard. Owned by lane A1.
//
// Each source loads independently: one failing list never blanks the screen, and a list that did
// not load is reported as unknown (null) — never as zero.
//
//   GET /leave/requests?employeeId          pending leave + approved leave covering today
//   GET /overtime/requests?employeeId       pending overtime (not counted by /ess/dashboard)
//   GET /ess/hr-requests/my                 open HR tickets
//   GET /attendance/daily?from&to&employeeId  recent processed days (missing punches, rest days)
//   GET /leave/holidays/range               public holidays (today + the next one)
//   GET /ess/payslips                       the latest final payslip (for "your payslip is ready")
//   GET /mobile/dashboard/{id}                     unread count (a server COUNT; the notification lists cap at 5 / 30)

import { apiGet } from './client';
import { fetchAllPages, type PageOf } from './paging';
import { holidaysApi } from './holidays';
import { unreadCount } from './homeMappers';
import {
  approvedLeaveOn,
  isPendingStatus,
  mapDayRecord,
  type DayRecord,
  type PublicHolidayItem,
} from './homeMappers';
import { appStorage, loadCredentialEnvelope, userStorage } from '@/storage';
import { riyadhBusinessDate } from '@/utils/businessDate';

export interface EmployeeHomeSignals {
  today: string;
  pending: { leave: number | null; overtime: number | null; hr: number | null };
  approvedLeaveToday: { leaveTypeName?: string } | null;
  /** Processed attendance days, recent first; null when the list could not be loaded. */
  dayRecords: DayRecord[] | null;
  /** Holidays from today for a year; null when the list could not be loaded. */
  holidays: PublicHolidayItem[] | null;
  latestPayslip: { id: string; year: number; month: number; periodLabel: string } | null;
  seenPayslipId: string | null;
  unreadNotifications: number | null;
}

async function employeeContext(): Promise<{ employeeId: number | null; key: string }> {
  const [user, credentials] = await Promise.all([userStorage.getUser(), loadCredentialEnvelope()]);
  const id = Number(user?.employeeId);
  const employeeId = Number.isFinite(id) && id > 0 ? id : null;
  return { employeeId, key: `${credentials?.tenantId ?? ''}:${employeeId ?? ''}` };
}

function addDays(day: string, delta: number): string {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d + delta)).toISOString().slice(0, 10);
}

const SEEN_PAYSLIP_KEY = 'kx_home_seen_payslip';

function settle<T>(promise: Promise<T>): Promise<T | null> {
  return promise.catch((error) => {
    console.warn('[Home] A supporting list could not load:', error?.message ?? error);
    return null;
  });
}

export const employeeHomeApi = {
  async getSignals(): Promise<EmployeeHomeSignals> {
    const today = riyadhBusinessDate();
    const { employeeId, key } = await employeeContext();
    if (!employeeId) {
      return {
        today,
        pending: { leave: null, overtime: null, hr: null },
        approvedLeaveToday: null,
        dayRecords: null,
        holidays: null,
        latestPayslip: null,
        seenPayslipId: null,
        unreadNotifications: null,
      };
    }

    const [leaveRows, overtimeRows, hrRows, dayRows, holidays, payslips, unread, seen] = await Promise.all([
      settle(fetchAllPages<any>((page, pageSize) =>
        apiGet<PageOf<any>>(`/leave/requests?employeeId=${employeeId}&page=${page}&pageSize=${pageSize}`))),
      settle(fetchAllPages<any>((page, pageSize) =>
        apiGet<PageOf<any>>(`/overtime/requests?employeeId=${employeeId}&page=${page}&pageSize=${pageSize}`))),
      settle(apiGet<any[]>('/ess/hr-requests/my')),
      settle(apiGet<PageOf<any>>(
        `/attendance/daily?from=${addDays(today, -35)}&to=${today}&employeeId=${employeeId}&page=1&pageSize=62`)),
      settle(holidaysApi.getRange(today, addDays(today, 365))),
      settle(apiGet<any[]>('/ess/payslips')),
      settle(apiGet<any>(`/mobile/dashboard/${employeeId}`)),
      appStorage.get<string>(`${SEEN_PAYSLIP_KEY}:${key}`),
    ]);

    const count = (rows: unknown, source: 'leave' | 'overtime' | 'hr') =>
      Array.isArray(rows) ? rows.filter((r: any) => isPendingStatus(source, r?.status)).length : null;

    const dayList = dayRows ? (dayRows.items ?? dayRows.data ?? []) : null;
    const latest = Array.isArray(payslips)
      ? [...payslips]
        .filter((p: any) => p?.id && Number(p?.year) > 0 && Number(p?.month) > 0)
        .sort((a: any, b: any) => (Number(b.year) * 12 + Number(b.month)) - (Number(a.year) * 12 + Number(a.month)))[0]
      : null;

    return {
      today,
      pending: {
        leave: count(leaveRows, 'leave'),
        overtime: count(overtimeRows, 'overtime'),
        hr: count(hrRows, 'hr'),
      },
      approvedLeaveToday: approvedLeaveOn(leaveRows, today),
      dayRecords: dayList
        ? dayList.map(mapDayRecord).filter((r): r is DayRecord => r !== null).sort((a, b) => b.date.localeCompare(a.date))
        : null,
      holidays,
      latestPayslip: latest
        ? { id: String(latest.id), year: Number(latest.year), month: Number(latest.month), periodLabel: String(latest.periodLabel ?? '') }
        : null,
      seenPayslipId: seen ?? null,
      unreadNotifications: unreadCount(unread),
    };
  },

  /** Remember that this payslip was opened from Home, so "your payslip is ready" stops showing. */
  async markPayslipSeen(payslipId: string): Promise<void> {
    const { key } = await employeeContext();
    await appStorage.set(`${SEEN_PAYSLIP_KEY}:${key}`, payslipId);
  },
};
