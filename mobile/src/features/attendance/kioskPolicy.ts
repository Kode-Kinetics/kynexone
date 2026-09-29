import type { PunchType, TodayAttendance } from '../../types';

export function nextKioskPunch(attendance: TodayAttendance | null | undefined): PunchType {
  return attendance?.currentlyActive ? 'CLOCK_OUT' : 'CLOCK_IN';
}

export function kioskPunchLabel(attendance: TodayAttendance | null | undefined): string {
  return nextKioskPunch(attendance) === 'CLOCK_OUT' ? 'Clock Out' : 'Clock In';
}
