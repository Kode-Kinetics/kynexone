/**
 * Multi-select state for the attendance bulk screens.
 *
 * The selection is a Map keyed by employee id that lives OUTSIDE the search results, so picks survive
 * a new search or a different department filter. "Select all shown" only ever touches what is on screen
 * (search and department already applied) and only Active employees can be chosen. Every function returns a
 * new Map; none mutates its input.
 */

export interface PickEmployee {
  id: number;
  employeeCode: string;
  fullName: string;
  department: string;
  status: string;
}

export type Selection = ReadonlyMap<number, PickEmployee>;

/** Active employees from a search result, narrowed to one department when a filter is set. */
export function shownEmployees<T extends PickEmployee>(results: readonly T[], department: string): T[] {
  return results.filter((r) => r.status === 'Active' && (!department || r.department === department));
}

export const selectedIds = (selection: Selection): number[] => [...selection.keys()].sort((a, b) => a - b);

export function toggleEmployee(selection: Selection, employee: PickEmployee, max: number): { selection: Selection; atCap: boolean } {
  const next = new Map(selection);
  if (next.has(employee.id)) {
    next.delete(employee.id);
    return { selection: next, atCap: false };
  }
  if (next.size >= max) return { selection, atCap: true };
  next.set(employee.id, employee);
  return { selection: next, atCap: false };
}

export function selectAllShown(selection: Selection, shown: readonly PickEmployee[], max: number): { selection: Selection; added: number; capped: boolean } {
  const next = new Map(selection);
  let added = 0;
  let capped = false;
  for (const e of shown) {
    if (next.has(e.id)) continue;
    if (next.size >= max) { capped = true; break; }
    next.set(e.id, e);
    added += 1;
  }
  return { selection: next, added, capped };
}

export function clearShown(selection: Selection, shown: readonly PickEmployee[]): Selection {
  const next = new Map(selection);
  for (const e of shown) next.delete(e.id);
  return next;
}

export const allShownSelected = (selection: Selection, shown: readonly PickEmployee[]): boolean =>
  shown.length > 0 && shown.every((e) => selection.has(e.id));
