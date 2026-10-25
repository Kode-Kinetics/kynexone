import { movedSectionSpecs } from './fixture';

// Time & leave: Leave, Shifts, Overtime and Attendance keep their daily work; their configuration is in Setup.
movedSectionSpecs([
  { id: 'leaveTypes', label: 'Leave types', group: 'Time & leave', old: '/leave?tab=types',
    ready: page => page.getByRole('button', { name: /New leave type|Add leave type/i }),
    module: { path: '/leave', goneTab: 'Leave Types', door: 'Leave settings' } },
  { id: 'leavePolicies', label: 'Leave policies', group: 'Time & leave', old: '/leave?tab=policies',
    ready: page => page.getByRole('button', { name: /New policy|Add policy/i }),
    module: { path: '/leave', goneTab: 'Policies', door: 'Leave settings' } },
  { id: 'holidayCalendars', label: 'Holiday calendars', group: 'Time & leave', old: '/leave?tab=holidays',
    ready: page => page.getByRole('button', { name: /holiday/i }).first(),
    module: { path: '/leave', goneTab: 'Holidays', door: 'Leave settings' } },
  { id: 'shiftDefinitions', label: 'Shifts', group: 'Time & leave', old: '/shifts?tab=definitions',
    ready: page => page.getByRole('button', { name: /New shift|Add shift/i }),
    module: { path: '/shifts', goneTab: 'Definitions', door: 'Shift settings' } },
  { id: 'shiftPolicy', label: 'Shift policy', group: 'Time & leave', old: '/shifts?tab=policy',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    module: { path: '/shifts', goneTab: 'Policy', door: 'Shift settings' } },
  { id: 'overtimePolicies', label: 'Overtime policies', group: 'Time & leave', old: '/overtime?tab=policies',
    ready: page => page.getByRole('button', { name: /New Policy/i }),
    module: { path: '/overtime', goneTab: 'Policies', door: 'Overtime settings' } },
  { id: 'attendanceDevices', label: 'Attendance devices', group: 'Time & leave', old: '/attendance?tab=devices',
    ready: page => page.getByRole('button', { name: /Add Device/i }),
    module: { path: '/attendance', goneTab: 'Device Management', door: 'Attendance settings' } },
]);
