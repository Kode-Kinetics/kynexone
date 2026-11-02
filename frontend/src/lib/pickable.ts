/**
 * Rows offered for a NEW choice: the ones that are on, plus any the record being edited already holds, so an existing
 * record that points at a turned-off row still shows (and keeps) it. The lists keep every row; only pickers narrow them.
 */
export function pickable<T extends { id: string; isActive?: boolean }>(items: readonly T[], ...held: (string | null | undefined)[]): T[] {
  return items.filter(item => item.isActive !== false || held.includes(item.id));
}
