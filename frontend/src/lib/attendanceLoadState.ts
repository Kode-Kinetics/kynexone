export const ATTENDANCE_DOMAINS = [
  'dashboard',
  'daily',
  'raw',
  'devices',
  'employees',
  'regularizations',
  'pendingRegularizations',
  'payrollSummary',
  'deviceSync',
  'insights',
] as const;

export type AttendanceDomain = typeof ATTENDANCE_DOMAINS[number];
export type AttendanceLoadErrors = Partial<Record<AttendanceDomain, string>>;

export const ATTENDANCE_DOMAIN_LABELS: Record<AttendanceDomain, string> = {
  dashboard: 'attendance summary',
  daily: 'daily attendance',
  raw: 'raw punch logs',
  devices: 'configured devices',
  employees: 'employee choices',
  regularizations: 'your correction requests',
  pendingRegularizations: 'pending correction approvals',
  payrollSummary: 'payroll attendance summary',
  deviceSync: 'device health',
  insights: 'attendance insights',
};

export function attendanceErrorSummary(errors: AttendanceLoadErrors): string {
  const failed = ATTENDANCE_DOMAINS.filter((key) => errors[key]);
  if (failed.length === 0) return '';
  return `${failed.length} attendance data source${failed.length === 1 ? '' : 's'} unavailable: ${failed.map((key) => ATTENDANCE_DOMAIN_LABELS[key]).join(', ')}.`;
}

export function unavailableMessage(domain: AttendanceDomain, errors: AttendanceLoadErrors): string | null {
  return errors[domain] ? `${ATTENDANCE_DOMAIN_LABELS[domain]} unavailable. Retry to load this data.` : null;
}

/**
 * The approval queue falls back to the current user's correction requests only
 * when the pending queue is genuinely empty. If that fallback source failed,
 * the UI must not turn the failure into an empty-success message.
 */
export function regularizationQueueUnavailableMessage(
  errors: AttendanceLoadErrors,
  pendingCount: number,
): string | null {
  const pendingFailure = unavailableMessage('pendingRegularizations', errors);
  if (pendingFailure) return pendingFailure;
  if (pendingCount === 0) return unavailableMessage('regularizations', errors);
  return null;
}
