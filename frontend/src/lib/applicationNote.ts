/**
 * The timeline note on an application event. A few notes are written by the server itself, in English; those are shown through the translator.
 * A note a person wrote, or any sentence not listed here, is shown exactly as stored.
 */
const SERVER_NOTES = new Set(['Application created.']);

export function timelineNote(note: string, t: (key: string) => string): string {
  return SERVER_NOTES.has(note) ? t(note) : note;
}
