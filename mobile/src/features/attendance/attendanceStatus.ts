import type { AttendanceStatus } from '../../types';

export function attendanceStatusLabel(status?: AttendanceStatus): string {
  if (!status || status === 'UNKNOWN') return 'Unavailable';
  return status
    .toLowerCase()
    .split('_')
    .map((word, index) => index === 0 ? word[0].toUpperCase() + word.slice(1) : word)
    .join(' ');
}
