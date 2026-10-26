import type { DuplicateMatch } from '../../api/employees';

/** The i18n key (the English word) for a status the duplicate panel can show; an unknown status is shown as the server sent it. */
const STATUS_KEYS: Record<string, string> = {
  Active: 'Active', Draft: 'Draft', Terminated: 'Terminated', Resigned: 'Resigned', Exited: 'Exited', Archived: 'Archived', Offboarded: 'Offboarded',
  Retired: 'Retired', Absconded: 'Absconded', Probation: 'Probation', OnLeave: 'On leave', Suspended: 'Suspended', Inactive: 'Inactive',
};
export const duplicateStatusKey = (status: string): string => STATUS_KEYS[status] ?? status;

/** CD26: the server's name-only advisory match is "possible" and its signals name the name. */
export function isNameOnlyMatch(m: Pick<DuplicateMatch, 'matchType' | 'signals'>): boolean {
  return m.matchType === 'possible' && (m.signals ?? []).some(s => /name/i.test(s));
}
