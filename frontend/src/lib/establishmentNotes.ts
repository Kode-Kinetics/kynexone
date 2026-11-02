/**
 * Notes on the Headcount & budget screen (one per department, one per level budget cell).
 * Plain text only. Mirrors the server: trimmed, at most 500 characters, null/omitted = unchanged,
 * an empty string clears, and a level note needs a budgeted headcount (0 = frozen counts).
 */

export const NOTE_MAX_LENGTH = 500;

/**
 * What to send for a note: undefined when it is unchanged (omit it, the server keeps the saved
 * one), '' to clear, otherwise the trimmed text. `edited` undefined = the field was never touched.
 */
export function noteChange(saved: string | null | undefined, edited: string | undefined): string | undefined {
  if (edited === undefined) return undefined;
  const next = edited.trim();
  return next === (saved ?? '').trim() ? undefined : next;
}

/** A level note lives on the budget cell, so it needs a budgeted headcount. */
export function levelNoteEditable(budgeted: number | null): boolean {
  return budgeted !== null;
}

export function noteCounter(text: string): string {
  return `${text.length}/${NOTE_MAX_LENGTH}`;
}
