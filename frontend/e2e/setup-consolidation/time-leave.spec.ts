import { movedSectionSpecs } from './fixture';

const paged = { items: [], total: 0, page: 1, pageSize: 50 };// The Shifts page loads the roster for the week; the rostering policy is one object, not a list.
const roster = { '/api/shifts/roster': { assignments: [], employees: [], definitions: [] } };
const rosteringPolicy = { genderRules: [], voluntaryShiftCodes: [], weekendDemand: [], holidayDemand: [], minRestHours: 11, maxConsecutiveDays: 6 };
// The Attendance workspace reads these as paged lists (the default reply is a plain array).
const attendancePages = Object.fromEntries(
  ['devices', 'daily', 'events/raw', 'regularization/my', 'regularization/pending-approval', 'exceptions'].map(path => [`/api/attendance/${path}`, paged]),
);

// Time & leave: Leave, Shifts, Overtime and Attendance keep their daily work; their configuration is in Setup.
movedSectionSpecs([
  { id: 'leaveTypes', label: 'Leave types', group: 'Time & leave', old: '/leave?tab=types',
    ready: page => page.getByRole('button', { name: /New leave type|Add leave type/i }),
    module: { path: '/leave', goneTab: 'Leave Types', door: 'Leave settings' } },
  { id: 'leavePolicies', label: 'Leave policies', group: 'Time & leave', old: '/leave?tab=policies',
    ready: page => page.getByRole('button', { name: /New policy|Add policy/i }),
    mocks: { '/api/leave/policies': paged },
    module: { path: '/leave', goneTab: 'Policies', door: 'Leave settings' } },
  { id: 'holidayCalendars', label: 'Holiday calendars', group: 'Time & leave', old: '/leave?tab=holidays',
    ready: page => page.getByRole('button', { name: /holiday/i }).first(),
    module: { path: '/leave', goneTab: 'Holidays', door: 'Leave settings' } },
  { id: 'shiftDefinitions', label: 'Shifts', group: 'Time & leave', old: '/shifts?tab=definitions',
    ready: page => page.getByRole('button', { name: /New shift|Add shift/i }),
    mocks: roster,
    module: { path: '/shifts', goneTab: 'Definitions', door: 'Shift settings' } },
  { id: 'shiftPolicy', label: 'Shift policy', group: 'Time & leave', old: '/shifts?tab=policy',
    ready: page => page.getByRole('button', { name: /Save Policy/i }),
    mocks: { ...roster, '/api/shifts/policy': rosteringPolicy },
    module: { path: '/shifts', goneTab: 'Policy', door: 'Shift settings' } },
  // The rules had no screen before (the old setup studio could create one), so there is no old address to follow.
  { id: 'attendanceRules', label: 'Attendance rules', group: 'Time & leave', old: '/setup?tab=attendanceRules',
    ready: page => page.getByRole('button', { name: 'New rules' }),
    mocks: { '/api/attendance/policies': [] } },
  { id: 'overtimePolicies', label: 'Overtime policies', group: 'Time & leave', old: '/overtime?tab=policies',
    ready: page => page.getByRole('button', { name: /New Policy/i }),
    mocks: { '/api/overtime/requests': paged, '/api/overtime/summary': {} },
    module: { path: '/overtime', goneTab: 'Policies', door: 'Overtime settings' } },
  { id: 'attendanceDevices', label: 'Attendance devices', group: 'Time & leave', old: '/attendance?tab=devices',
    ready: page => page.getByRole('button', { name: /Add Device/i }),
    mocks: attendancePages,
    module: { path: '/attendance', goneTab: 'Device Management', door: 'Attendance settings' } },
]);
