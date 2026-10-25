import { expect, test } from '@playwright/test';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleAccess } from '../src/lib/setupModuleSections';

/**
 * Who may open each Time & leave section in Setup. A section opens for what its old module tab opened for, narrowed to
 * what the API serves for READ, so nobody is shown a screen that answers 403 on load. Each case cites the backend rule
 * (paths under backend-dotnet/Zayra.Api/Controllers).
 */

const persona = (roles: string[], permissions: string[]): SetupModuleAccess => ({
  hasPermission: key => permissions.includes(key),
  hasRole: role => roles.includes(role),
  isFeatureEnabled: () => true,
  verdictForPath: () => ({ allowed: true }),
  flagStatus: 'ready',
});
const canOpen = (id: string, access: SetupModuleAccess) => {
  const found = SETUP_MODULE_SECTIONS.find(section => section.id === id);
  expect(found, `Missing Setup section ${id}`).toBeDefined();
  return moduleSectionAvailable(found!, access);
};

const hrOfficer = persona(['HR Officer'], ['leave.read', 'attendance.read', 'overtime.read']);
const hrManager = persona(['HR Manager'], ['leave.read', 'leave.write', 'attendance.read', 'attendance.write', 'attendance.policy_manage', 'overtime.read', 'overtime.write']);
const lineManager = persona(['Manager'], ['leave.read', 'attendance.read', 'overtime.read']);
const employee = persona(['Employee'], ['ess.read', 'leave.read', 'attendance.read']);

test('leave_types_policies_and_holidays_open_for_whoever_can_change_them', () => {
  // Reading is open to any signed-in caller: LeaveTypesController.cs:18,36 / LeavePoliciesController.cs:15,27 /
  // HolidayCalendarController.cs:12,19 (class [Authorize], no role on the GET). Writing is Admin, HR Manager only
  // (LeaveTypesController.cs:84, LeavePoliciesController.cs:86, HolidayCalendarController.cs:37). The old Leave tabs were
  // `isAdmin` in LeavePage (Admin, HR Manager, HR Officer). A settings section opens for whoever can change it (Setup's
  // rule for these three before the move): an HR Officer read them but had every save refused.
  for (const id of ['leaveTypes', 'leavePolicies', 'holidayCalendars']) {
    expect(canOpen(id, hrOfficer), `${id} for HR Officer`).toBe(false);
    expect(canOpen(id, hrManager), `${id} for HR Manager`).toBe(true);
    expect(canOpen(id, lineManager), `${id} for Manager`).toBe(false);
    expect(canOpen(id, employee), `${id} for Employee`).toBe(false);
  }
});

test('shift_definitions_open_for_whoever_can_change_them', () => {
  // ShiftsController.cs:18,37: the definitions list needs only a signed-in caller; writes are Admin, HR Manager
  // (ShiftsController.cs:49). The old tab was `isAdmin` in ShiftsPage (Admin, HR Manager, HR Officer); as a settings
  // section it opens for whoever can change it, so not for an HR Officer.
  expect(canOpen('shiftDefinitions', hrOfficer)).toBe(false);
  expect(canOpen('shiftDefinitions', hrManager)).toBe(true);
  expect(canOpen('shiftDefinitions', lineManager)).toBe(false);
  expect(canOpen('shiftDefinitions', employee)).toBe(false);
});

test('shift_policy_opens_for_Admin_and_HR_Manager_only', () => {
  // ShiftsController.cs:303-304: GET /api/shifts/policy is [Authorize(Roles = "Admin,HR Manager")]. The old tab was shown
  // to an HR Officer too, who then got a 403, so the section is narrower than the tab was.
  expect(canOpen('shiftPolicy', hrManager)).toBe(true);
  expect(canOpen('shiftPolicy', persona(['Admin'], ['attendance.read']))).toBe(true);
  expect(canOpen('shiftPolicy', hrOfficer)).toBe(false);
  expect(canOpen('shiftPolicy', lineManager)).toBe(false);
});

test('overtime_policies_open_for_Admin_and_HR_Manager_only', () => {
  // OvertimeController.cs:21,42: reading policies needs only a signed-in caller. The old tab was shown to Admin and
  // HR Manager only (OvertimePage: "HR Manager & Admin see all tabs"), and that stays; a Payroll Officer may create a
  // policy (OvertimeController.cs:51) but never had the tab.
  expect(canOpen('overtimePolicies', hrManager)).toBe(true);
  expect(canOpen('overtimePolicies', hrOfficer)).toBe(false);
  expect(canOpen('overtimePolicies', persona(['Payroll Officer'], ['overtime.read', 'overtime.write']))).toBe(false);
  expect(canOpen('overtimePolicies', lineManager)).toBe(false);
});

test('attendance_devices_open_for_the_roles_the_device_list_serves', () => {
  // AttendanceController.cs:60-61: GET /api/attendance/devices is [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")].
  // An Auditor reads the list (and sees no secrets: CanConfigureDevices, :58); adding or changing a device also needs
  // attendance.bulk_import (:79), which the panel checks for its buttons. A Manager or Payroll Manager gets a 403.
  expect(canOpen('attendanceDevices', persona(['Auditor'], ['attendance.read']))).toBe(true);
  expect(canOpen('attendanceDevices', hrOfficer)).toBe(true);
  expect(canOpen('attendanceDevices', hrManager)).toBe(true);
  expect(canOpen('attendanceDevices', lineManager)).toBe(false);
  expect(canOpen('attendanceDevices', persona(['Payroll Manager'], ['attendance.read']))).toBe(false);
  expect(canOpen('attendanceDevices', employee)).toBe(false);
});

test('attendance_rules_open_for_whoever_holds_attendance_policy_manage', () => {
  // AttendancePoliciesController: every action needs attendance.policy_manage (Admin, HR Manager, HR Director hold it).
  // attendance.read / attendance.write are held by HR Officer, Payroll Manager, Supervisors and others, who would get a
  // 403 on load, so the section keys on the dedicated permission alone.
  expect(canOpen('attendanceRules', hrManager)).toBe(true);
  expect(canOpen('attendanceRules', persona(['Admin'], ['attendance.policy_manage']))).toBe(true);
  expect(canOpen('attendanceRules', persona(['HR Director'], ['attendance.read', 'attendance.policy_manage']))).toBe(true);
  expect(canOpen('attendanceRules', persona(['Payroll Manager'], ['attendance.read', 'attendance.write']))).toBe(false);
  expect(canOpen('attendanceRules', hrOfficer)).toBe(false);
  expect(canOpen('attendanceRules', persona(['Auditor'], ['attendance.read']))).toBe(false);
  expect(canOpen('attendanceRules', lineManager)).toBe(false);
  expect(canOpen('attendanceRules', employee)).toBe(false);
});

test('a_module_switched_off_hides_its_settings', () => {
  const off: SetupModuleAccess = { ...hrManager, isFeatureEnabled: () => false };
  expect(canOpen('shiftDefinitions', off)).toBe(false);
  expect(canOpen('overtimePolicies', off)).toBe(false);
  // Leave and Attendance have no switch of their own.
  expect(canOpen('leaveTypes', off)).toBe(true);
});
