import type { EmployeeAccessState, IssuedWelcomeCode, WorkEmailRow } from '../api/employeeAccess';
import { dateLocale } from './format';

/**
 * Pure helpers for the HR side of employee sign-in access: the one label, tone and button per
 * state, the slip's formatting, and the paste parser for IT's work-email list. No React, so the
 * unit lane (unit/employeeAccess.spec.ts) can pin them.
 *
 * Wording is the SME copy deck's (contract Amendment 2). HR never sees: user, link, access mode,
 * staged, invitation.
 */

export type AccessTone = 'slate' | 'amber' | 'blue' | 'emerald' | 'rose';

interface StateCopy {
  /** The status pill (an i18n key, English is the key). */
  label: string;
  tone: AccessTone;
  /** The one HR button, or null when there is nothing HR can do from here. */
  action: string | null;
}

export const ACCESS_STATE_COPY: Record<EmployeeAccessState, StateCopy> = {
  waiting_for_work_email: { label: 'Waiting for work email', tone: 'slate', action: 'Add work email' },
  not_started: { label: 'No access yet', tone: 'amber', action: 'Give access' },
  code_given: { label: 'Code given, not signed in yet', tone: 'blue', action: 'Give new code' },
  active: { label: 'Using KynexOne', tone: 'emerald', action: 'Reset sign-in' },
  stopped: { label: 'Access stopped', tone: 'slate', action: null },
  blocked: { label: 'Needs admin help', tone: 'rose', action: null },
};

/**
 * A bulk print takes these states only. `active` is never bulk (Reset sign-in is one person at a
 * time, by a holder of employees.access.reset); it is skipped on the screen before the request.
 */
export const BULK_PRINTABLE_STATES: ReadonlySet<EmployeeAccessState> = new Set(['not_started', 'code_given']);

/** A bulk selection with any of these confirms first: their old codes stop working. */
export const REPLACES_A_CODE: ReadonlySet<EmployeeAccessState> = new Set(['code_given']);

export function isAccessState(value: unknown): value is EmployeeAccessState {
  return typeof value === 'string' && value in ACCESS_STATE_COPY;
}

/** "48217730" → "4821 7730". Anything that is not 8 digits is returned as-is. */
export function formatWelcomeCode(code: string): string {
  const digits = (code ?? '').replace(/\s+/g, '');
  return /^\d{8}$/.test(digits) ? `${digits.slice(0, 4)} ${digits.slice(4)}` : code;
}

/**
 * What the slip's QR opens: the welcome page with the email, code and company in the HASH, which a
 * browser never sends to a server (never a query string).
 */
export function welcomeQrUrl(origin: string, email: string, code: string, tenantSlug?: string | null): string {
  const hash = `e=${encodeURIComponent(email)}&c=${encodeURIComponent(code)}${tenantSlug ? `&w=${encodeURIComponent(tenantSlug)}` : ''}`;
  return `${origin.replace(/\/+$/, '')}/welcome#${hash}`;
}

/** The app address printed in the steps, without the scheme: "app.kynexone.com". */
export function appAddress(origin: string): string {
  return origin.replace(/^https?:\/\//, '').replace(/\/+$/, '');
}

const blankLast = (a: string, b: string) => {
  if (!a && b) return 1;
  if (a && !b) return -1;
  return a.localeCompare(b, 'en', { sensitivity: 'base' });
};

/** Slips are cut and handed out by site, then department, then name. */
export function sortSlips<T extends Pick<IssuedWelcomeCode, 'site' | 'department' | 'employeeName'>>(slips: readonly T[]): T[] {
  return [...slips].sort((a, b) =>
    blankLast((a.site ?? '').trim(), (b.site ?? '').trim())
    || blankLast((a.department ?? '').trim(), (b.department ?? '').trim())
    || blankLast((a.employeeName ?? '').trim(), (b.employeeName ?? '').trim()));
}

/** Two slips per A4 sheet. */
export function pairUp<T>(items: readonly T[]): T[][] {
  const pages: T[][] = [];
  for (let i = 0; i < items.length; i += 2) pages.push(items.slice(i, i + 2));
  return pages;
}

/** An issued code went by email (per item; an older API marks it only by leaving `code` out). */
export function wasEmailed(item: Pick<IssuedWelcomeCode, 'delivery' | 'code'>): boolean {
  return item.delivery ? item.delivery === 'email' : !item.code;
}

/** An issued code that is printed on a slip. */
export function isPrintable(item: Pick<IssuedWelcomeCode, 'delivery' | 'code'>): boolean {
  return !!item.code && item.delivery !== 'email';
}

export const DEFAULT_COMPANY_TIME_ZONE = 'Asia/Riyadh';

function zoneOrDefault(timeZone?: string | null): string {
  const tz = timeZone?.trim();
  if (!tz) return DEFAULT_COMPANY_TIME_ZONE;
  try { new Intl.DateTimeFormat(dateLocale('en'), { timeZone: tz }); return tz; } catch { return DEFAULT_COMPANY_TIME_ZONE; }
}

/**
 * The copy deck's date line, month spelled out, Umm al-Qura in brackets, Western digits, in the
 * company's zone (a code that ends 23:59 in Riyadh is that Riyadh day, not the UTC one):
 *   en  "Wednesday 14 October 2026 (3 Jumada I 1448 AH)"
 *   ar  "الأربعاء، 14 أكتوبر 2026م (3 جمادى الأولى 1448هـ)"
 */
export function dateLine(iso: string, lang: 'en' | 'ar', timeZone?: string | null): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const tz = zoneOrDefault(timeZone);
  const day: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'long', year: 'numeric', timeZone: tz };
  if (lang === 'en') {
    const g = new Intl.DateTimeFormat(dateLocale('en', 'gregory'), { ...day, weekday: 'long' }).format(date).replace(/^(\p{L}+),\s/u, '$1 ');
    const h = new Intl.DateTimeFormat(dateLocale('en', 'islamic-umalqura'), day).format(date);
    return `${g} (${h})`;
  }
  const g = new Intl.DateTimeFormat(dateLocale('ar', 'gregory'), { ...day, weekday: 'long' }).format(date);
  const h = new Intl.DateTimeFormat(dateLocale('ar', 'islamic-umalqura'), day).format(date).replace(/\s*هـ$/u, 'هـ');
  return `${g}م (${h})`;
}

export interface ParsedWorkEmails {
  rows: WorkEmailRow[];
  /** Lines that had neither an employee number nor an email we could read. */
  unreadable: number;
}

/**
 * IT's list: two columns, employee number and work email, pasted from Excel (tab), or a CSV
 * (comma / semicolon). Either column order works; a header line is skipped because it has no "@".
 */
export function parseWorkEmailRows(text: string): ParsedWorkEmails {
  const rows: WorkEmailRow[] = [];
  let unreadable = 0;
  for (const raw of (text ?? '').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    const cells = line.split(/[\t,;]|\s+/).map((c) => c.trim().replace(/^"(.*)"$/, '$1').trim()).filter(Boolean);
    const emailIndex = cells.findIndex((c) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(c));
    if (emailIndex < 0) {
      // A header ("Employee no., Email") or a stray line.
      if (!/e-?mail|employee|code|number|رقم|بريد/i.test(line)) unreadable++;
      continue;
    }
    const code = cells.find((c, i) => i !== emailIndex && !c.includes('@'));
    if (!code) { unreadable++; continue; }
    rows.push({ employeeCode: code, workEmail: cells[emailIndex].toLowerCase() });
  }
  return { rows, unreadable };
}

/**
 * Why the API skipped someone, as an i18n key (copy deck, "Skip reasons"). Several spellings map to
 * one sentence. An unknown code returns null: the caller shows a generic sentence in Arabic and the
 * server's own words only in English (never raw server text in Arabic).
 */
export const SKIP_REASON_KEYS: Record<string, string> = {
  waiting_for_work_email: 'No work email yet.',
  no_work_email: 'No work email yet.',
  stopped: 'Access has been stopped for this employee.',
  self: "You can't give access to yourself.",
  cannot_issue_for_self: "You can't give access to yourself.",
  above_caller: 'This person has more permissions than you.',
  ceiling: 'This person has more permissions than you.',
  above_ceiling: 'This person has more permissions than you.',
  privileged_login: 'This person has admin permissions. A security admin must reset their sign-in.',
  blocked: 'This needs a system admin first.',
  company_email_domain_missing: 'This needs a system admin first.',
  email_belongs_to_existing_login: 'This needs a system admin first.',
  work_email_set_by_caller: "You set this person's work email, so another HR colleague must give access.",
  email_belongs_to_former_employee: 'This needs a system admin first.',
  seat_limit: "Your company's KynexOne plan is full.",
  // Reset sign-in is one person at a time, from their profile (never bulk).
  active: "Use Reset sign-in on the person's profile.",
  reset_is_single: 'Reset sign-in is done one person at a time, from their profile.',
  reset_requires_permission: "Ask an HR Manager to reset this person's sign-in.",
  awaiting_approval: 'Waiting for approval. You can give access once {name} is approved.',
};

/** The reason codes the API defines (contract); unit/employeeAccess.spec.ts proves each has a sentence. */
export const CANONICAL_SKIP_CODES = [
  'cannot_issue_for_self', 'above_ceiling', 'seat_limit', 'reset_is_single', 'reset_requires_permission',
  'work_email_set_by_caller', 'privileged_login',
] as const;
export const CANONICAL_CONFLICT_CODES = ['duplicate_in_file', 'username_differs'] as const;

export const GENERIC_SKIP_KEY = 'This needs a system admin first.';

export function skipReasonKey(reasonCode: string | null | undefined): string | null {
  return reasonCode ? SKIP_REASON_KEYS[reasonCode] ?? null : null;
}

/** Work-email refusals the create / edit / backfill saves can return (422 `error` or `code`). */
export const WORK_EMAIL_ERROR_KEYS: Record<string, string> = {
  work_email_plus_address: "Work email can't contain '+'.",
  work_email_invalid_characters: 'Work email can only use English letters, numbers, dots, dashes and underscores before the @.',
  work_email_wrong_domain: 'Work email must end in @{domain}.',
};

export function workEmailErrorCode(e: unknown): string | null {
  const res = (e as { response?: { status?: number; data?: { error?: string; code?: string } } })?.response;
  if (res?.status !== 422) return null;
  const code = res.data?.error ?? res.data?.code;
  return code && code in WORK_EMAIL_ERROR_KEYS ? code : null;
}

/** Backfill rows that can't be saved, by `reason` code. Anything else: the email belongs to someone else. */
export const CONFLICT_REASON_KEYS: Record<string, string> = {
  username_differs: "This person already signs in with {username}; their sign-in won't change.",
  duplicate_in_file: 'This row repeats an employee number or email from earlier in the list.',
  duplicate_employee_number: 'This row repeats an employee number or email from earlier in the list.',
  duplicate_email: 'This row repeats an employee number or email from earlier in the list.',
  in_file_duplicate: 'This row repeats an employee number or email from earlier in the list.',
};
export const DEFAULT_CONFLICT_KEY = 'This email is already used by another employee.';

/**
 * The same rule the server applies to the part before the @, checked as HR types: a "+" has its own
 * sentence; anything but English letters, digits, dots, dashes and underscores is refused. Returns
 * the 422 code it matches, or null. Blank is fine (a work email is optional).
 */
export function workEmailLocalProblem(email: string | null | undefined): 'work_email_plus_address' | 'work_email_invalid_characters' | null {
  const value = (email ?? '').trim();
  if (!value) return null;
  const local = value.includes('@') ? value.slice(0, value.indexOf('@')) : value;
  if (local.includes('+')) return 'work_email_plus_address';
  return /^[A-Za-z0-9._-]*$/.test(local) ? null : 'work_email_invalid_characters';
}

/** The sentence for a work-email refusal (server 422 or the as-you-type check); null for anything else. */
export function workEmailProblemKey(code: string | null): string | null {
  return code && code !== 'work_email_wrong_domain' ? WORK_EMAIL_ERROR_KEYS[code] ?? null : null;
}

/** Why access stopped, by code (copy deck). Unknown codes say nothing beyond the "Access stopped" pill. */
export const STOPPED_REASON_KEYS: Record<string, string> = {
  left_company: 'Access stopped because the employee has left the company.',
  disabled_by_admin: 'Access stopped by a system admin.',
};

/** What needs an admin, by blocked code (copy deck). Unknown codes: "This needs a system admin first." */
export const BLOCKED_REASON_KEYS: Record<string, string> = {
  company_email_domain_missing: 'The company email ending (for example @evostel.com) is not set up. Ask your system admin to add it in company settings.',
  email_belongs_to_existing_login: 'This email is already used to sign in by someone else. Ask your system admin to fix it.',
  email_belongs_to_former_employee: 'This email is already used to sign in by someone else. Ask your system admin to fix it.',
};

/**
 * The work email typed with a domain other than the company's: refused, never quietly re-domained.
 * Returns the domain HR must use, or null when the address is fine (or has no domain yet).
 */
export function workEmailDomainProblem(email: string | null | undefined, domain: string | null | undefined): string | null {
  const want = (domain ?? '').trim().toLowerCase();
  const value = (email ?? '').trim();
  if (!want || !value.includes('@')) return null;
  return value.slice(value.indexOf('@') + 1).toLowerCase() === want ? null : want;
}
