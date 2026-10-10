// Owned by lane A2 (Leave, payslips, overtime, deductions).
// Merged into i18n under the `leavePay.*` keys — use t('leavePay.someKey'). Only this lane edits this file;
// nobody edits config/i18n.ts for these keys.
//
// Rules: every key in `en` must exist in `ar` (tsc fails otherwise, via i18n.ts, and
// tests/strings.test.ts checks parity). Arabic reads naturally in a Gulf business register.
// Keep this file import-free so tests can load it under node --test.

const en = {};

const ar: typeof en = {};

export const leavePayStrings = { en, ar };
