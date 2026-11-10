import type { Page } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type ApiMocks, type Persona } from '../setup-consolidation/fixture';
import type { Locale } from './contract';

/**
 * Intercepted API replies for the Attendance Live Dashboard. Nothing here reads or writes a real organization: the
 * shapes are the ones src/api/attendance.ts declares, with invented people. Shared by the screen contract, the
 * dashboard-specific contracts and the before/after screenshots.
 */
export const DAY = '2026-11-05';

const PEOPLE: Array<[string, string, string]> = [
  ['E-1001', 'Amal Al-Harbi', 'Finance'], ['E-1002', 'Omar Khalid', 'Operations'], ['E-1003', 'Layla Hassan', 'People'],
  ['E-1004', 'Yusuf Nasser', 'Sales'], ['E-1005', 'Noura Al-Qahtani', 'Finance'], ['E-1006', 'Faisal Mansour', 'Operations'],
  ['E-1007', 'Huda Saleh', 'Sales'], ['E-1008', 'Tariq Aziz', 'Engineering'], ['E-1009', 'Mariam Said', 'People'],
  ['E-1010', 'Khalid Rahman', 'Engineering'], ['E-1011', 'Sara Ibrahim', 'Finance'], ['E-1012', 'Hamad Al-Otaibi', 'Operations'],
  ['E-1013', 'Dana Youssef', 'Sales'], ['E-1014', 'Rashid Al-Dosari', 'Engineering'],
];

export function dailyRows(count = PEOPLE.length) {
  return PEOPLE.slice(0, count).map(([code, name, department], i) => {
    const late = i % 5 === 2 ? 17 : 0;
    const absent = i % 7 === 6;
    const missing = i % 6 === 4;
    return {
      id: `d-${code}`, employeeId: 1000 + i, employeeName: name, department, branch: 'Riyadh HQ', workDate: DAY,
      firstInUtc: absent ? undefined : `${DAY}T05:${String(late ? 17 : 2 + (i % 4)).padStart(2, '0')}:00Z`,
      lastOutUtc: absent || missing ? undefined : `${DAY}T14:0${i % 9}:00Z`,
      totalWorkedMinutes: absent ? 0 : 480 - late, lateMinutes: late, earlyExitMinutes: 0, overtimeMinutes: 0, undertimeMinutes: 0,
      missingPunch: missing, status: absent ? 'Absent' : late ? 'Late' : 'Present', manualCorrectionStatus: 'None', isPayrollLocked: false,
    };
  });
}

const employees = PEOPLE.map(([code, name, department], i) => ({
  id: 1000 + i, publicId: `p-${code}`, employeeCode: code, fullName: name, arabicName: name, department, designation: 'Specialist', branch: 'Riyadh HQ',
  status: 'Active', profileCompletenessScore: 100, iqamaNumber: '', readinessState: 'Ready', activationBlockersCount: 0,
}));

const TILE_RULES: Record<string, (r: ReturnType<typeof dailyRows>[number]) => boolean> = {
  present: r => ['Present', 'Late', 'Half day'].includes(r.status),
  absent: r => r.status === 'Absent',
  late: r => r.lateMinutes > 0,
  missingPunch: r => r.missingPunch,
};

const page = <T,>(items: T[]) => ({ items, total: items.length, page: 1, pageSize: 50 });

export interface AttendanceMockOptions {
  /** Daily rows returned for the day (default: 14 people). A function sees the request's `status` filter. */
  rows?: ReturnType<typeof dailyRows> | ((status: string | null) => ReturnType<typeof dailyRows>);
  devices?: Array<{ deviceId: string; deviceName: string; vendor: string; status: string; lastSyncAtUtc?: string; errorLog: string }>;
  insights?: Array<{ id: string; insightType: string; severity: string; title: string; summary: string; createdAtUtc: string }>;
  extra?: ApiMocks;
}

export function attendanceMocks(options: AttendanceMockOptions = {}): ApiMocks {
  const rows = options.rows ?? dailyRows();
  const devices = options.devices ?? [
    { deviceId: 'dev-1', deviceName: 'Main entrance', vendor: 'ZKTeco', status: 'Success', lastSyncAtUtc: `${DAY}T04:55:00Z`, errorLog: '' },
    { deviceId: 'dev-2', deviceName: 'Warehouse gate', vendor: 'Hikvision', status: 'Failed', lastSyncAtUtc: `${DAY}T01:10:00Z`, errorLog: 'timeout' },
  ];
  const insights = options.insights ?? [
    { id: 'i-1', insightType: 'late-trend', severity: 'Medium', title: 'Late arrivals up in Operations', summary: 'Four late arrivals this week against one last week.', createdAtUtc: `${DAY}T06:00:00Z` },
  ];
  return {
    '/api/attendance/daily': (url: URL) => {
      const status = url.searchParams.get('status');
      const tile = url.searchParams.get('tile');
      const list = typeof rows === 'function' ? rows(status) : rows;
      if (typeof rows === 'function') return page(list);
      // `status` is an exact match; `tile` applies the server's tile rule (the same predicate the KPI count uses).
      if (tile) return page(list.filter(r => TILE_RULES[tile]?.(r) ?? false));
      return page(status ? list.filter(r => r.status === status) : list);
    },
    '/api/attendance/dashboard': { date: DAY, activeEmployees: 14, present: 11, absent: 2, late: 3, missingPunch: 2, overtimeEmployees: 1, deviceErrors: 1, pendingRegularizations: 0 },
    '/api/attendance/devices': page([]),
    '/api/attendance/events/raw': page([]),
    '/api/employees': page(employees),
    '/api/attendance/regularization/my': page([]),
    '/api/attendance/regularization/pending-approval': page([]),
    '/api/attendance/reports/payroll-summary': [],
    '/api/attendance/reports/device-sync': devices,
    '/api/attendance/ai/insights': insights,
    ...options.extra,
  };
}

export const HR_PERSONA: Persona = { roles: ['HR Manager'], permissions: [...ALL_PERMISSIONS, 'attendance.read', 'attendance.write'] };
/** Someone who can punch at a kiosk but cannot enter attendance for a selection: sees the plain punch form. */
export const KIOSK_PERSONA: Persona = { roles: ['Supervisor'], permissions: ['dashboard.read', 'attendance.read', 'attendance.kiosk'] };

export async function openAttendance(pageHandle: Page, locale: Locale, options: AttendanceMockOptions & { persona?: Persona } = {}) {
  const booted = await boot(pageHandle, { locale, url: '/attendance', persona: options.persona ?? HR_PERSONA, mocks: attendanceMocks(options) });
  await pageHandle.locator('main').first().waitFor({ state: 'visible', timeout: 60_000 });
  return booted;
}
