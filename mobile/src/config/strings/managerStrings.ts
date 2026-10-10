// Owned by lane B (Manager: approvals, team, team calendar, manager dashboard).
// Merged into i18n under the `manager.*` keys — use t('manager.someKey'). Only this lane edits this file;
// nobody edits config/i18n.ts for these keys.
//
// Rules: every key in `en` must exist in `ar` (tsc fails otherwise, via i18n.ts, and
// tests/strings.test.ts checks parity). Arabic reads naturally in a Gulf business register.
// Keep this file import-free so tests can load it under node --test.

const en = {};

const ar: typeof en = {};

export const managerStrings = { en, ar };
