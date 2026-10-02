/**
 * PERSONAL-DATA MASKING for the SHAREABLE half of an evidence bundle.
 *
 * An acceptance bundle is made to be sent to people — a reviewer, an auditor, a customer. The
 * screens it photographs are HR screens: names, work emails, IBANs, national and Iqama numbers,
 * phone numbers. So the harness produces TWO copies of every record:
 *
 *   originals/   exactly what was on screen, kept for whoever has to verify the claim. Written
 *                outside the repository (see recorder.ts) and never committed.
 *   shareable/   the same capture with personal data redacted, and the same manifest with personal
 *                data redacted in its text. This is the copy that goes in the bundle.
 *
 * The two are linked by step id and by the ORIGINAL's hash, which the shareable manifest also
 * carries — so "this redacted picture is a redaction of that original" is checkable rather than
 * asserted.
 *
 * Masking happens in two places, because a bundle leaks through both:
 *   • in the IMAGE, by tagging the DOM nodes that hold personal data and handing them to
 *     Playwright's own `mask` option, which paints over their boxes before the PNG/JPEG is encoded
 *     (nothing sensitive is ever encoded and then blurred);
 *   • in the TEXT of the manifest and index, by `redactText`, because observed outcomes and API
 *     URLs quote the same values.
 *
 * The patterns are deliberately conservative about what they KEEP: a fixture persona's name is not
 * masked (it is the actor, and the whole point is to say who acted), but an employee's personal
 * identifiers are.
 *
 * ── The lookarounds are load-bearing ──────────────────────────────────────────────────────────
 * Every pattern is fenced with `(?<![\w@.-])` … `(?![\w-])`. Without them the first run of this
 * harness redacted its own evidence: a numeric run of a UUID matched the phone pattern, so the
 * manifest recorded the run as `14b95f51-28d9-4eab-827*******49e1d7e`, the payment batch as
 * `PAY-202********59` and the wage file as `mudad_wps_000*****00_202609.xml`. A bundle whose
 * business record ids are starred out cannot be checked against anything, which makes it worse
 * than no bundle. Record ids, batch numbers, file names, hashes and money are EVIDENCE and are
 * never masked.
 *
 * The cost of that fence is a known, accepted gap: a bare unformatted 10-digit number that is a
 * phone and nothing else is indistinguishable from a large amount, so only the KSA mobile shape
 * (05…) is caught bare. Formatted and +country numbers are caught in every case.
 */
import type { Locator, Page } from '@playwright/test';

/** The attribute the DOM tagger marks personal-data nodes with, and Playwright then masks. */
export const PII_ATTRIBUTE = 'data-evidence-pii';

/**
 * Patterns that identify a PERSON rather than a business fact. Each is applied to leaf text nodes
 * in the page and to free text in the manifest.
 *
 * Money, dates, run ids, payslip numbers and batch numbers are deliberately ABSENT: they are the
 * evidence. A bundle that redacted the net-pay total would prove nothing.
 */
export const PII_PATTERNS: Array<{ name: string; re: RegExp; replace: (m: string) => string }> = [
  {
    name: 'email',
    re: /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g,
    replace: (m) => {
      const [local, domain] = m.split('@');
      return `${local.slice(0, 2)}${'*'.repeat(Math.max(3, local.length - 2))}@${domain}`;
    },
  },
  {
    // SA + 2 check digits + 18 — the IBAN the bank file is built from.
    name: 'iban',
    re: /(?<![\w-])[A-Z]{2}\d{2}[A-Z0-9]{11,30}(?![\w-])/g,
    replace: (m) => `${m.slice(0, 4)}${'*'.repeat(m.length - 8)}${m.slice(-4)}`,
  },
  {
    // Saudi national id / Iqama: 10 digits starting 1 or 2. Also covers most GCC id shapes.
    name: 'national-id',
    re: /(?<![\w@.-])[12]\d{9}(?![\w.-])/g,
    replace: (m) => `${m.slice(0, 2)}${'*'.repeat(6)}${m.slice(-2)}`,
  },
  {
    // Either an international number (+966 50 123 4567, +966501234567) or a nationally formatted
    // one carrying at least one space or hyphen. A bare run of digits is NOT treated as a phone —
    // see the header: that is how money and record ids used to be redacted.
    name: 'phone',
    re: /(?<![\w@.+-])(?:\+\d[\d\s-]{6,}\d|\d(?=[\d\s-]*[\s-])[\d\s-]{6,}\d)(?![\w-])/g,
    replace: (m) => `${m.trim().slice(0, 3)}${'*'.repeat(Math.max(3, m.trim().length - 5))}${m.trim().slice(-2)}`,
  },
  {
    // The one bare shape worth catching: a KSA mobile, which is unambiguous at 05 + 8 digits.
    name: 'ksa-mobile',
    re: /(?<![\w@.+-])05\d{8}(?![\w.-])/g,
    replace: (m) => `${m.slice(0, 3)}${'*'.repeat(5)}${m.slice(-2)}`,
  },
];

/** Redact personal data in free text — used for every string the shareable manifest carries. */
export function redactText(value: string): string {
  let out = value;
  for (const p of PII_PATTERNS) out = out.replace(p.re, (m) => p.replace(m));
  return out;
}

/** Deep-redact a JSON-shaped value. Keys are left alone; only string VALUES are redacted. */
export function redactDeep<T>(value: T): T {
  if (typeof value === 'string') return redactText(value) as unknown as T;
  if (Array.isArray(value)) return value.map(redactDeep) as unknown as T;
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) out[k] = redactDeep(v);
    return out as unknown as T;
  }
  return value;
}

/**
 * Tag every element in the page whose own text is personal data, so Playwright can paint over it.
 *
 * Works on LEAF elements only (no element children), so tagging a cell does not also blank the
 * table around it: the redaction has to be surgical or the capture stops being evidence of
 * anything. Returns how many nodes were tagged, which the manifest records — "0 masked" on a screen
 * full of employees is itself a signal that the masking did not run.
 */
export async function tagPii(page: Page): Promise<number> {
  return page.evaluate(
    ({ attr, sources }) => {
      const patterns = sources.map((s) => new RegExp(s, 'g'));
      let tagged = 0;
      for (const el of document.querySelectorAll('body *')) {
        if (el.children.length > 0) continue; // leaves only
        const text = (el.textContent ?? '').trim();
        if (!text || text.length > 200) continue;
        if (patterns.some((re) => { re.lastIndex = 0; return re.test(text); })) {
          el.setAttribute(attr, 'true');
          tagged += 1;
        }
      }
      return tagged;
    },
    { attr: PII_ATTRIBUTE, sources: PII_PATTERNS.map((p) => p.re.source) },
  );
}

/** Remove the tags again, so the page the spec keeps driving is the product's own DOM. */
export async function untagPii(page: Page): Promise<void> {
  await page.evaluate((attr) => {
    document.querySelectorAll(`[${attr}]`).forEach((el) => el.removeAttribute(attr));
  }, PII_ATTRIBUTE);
}

/** The locator Playwright's screenshot `mask` option is given. */
export const piiLocator = (page: Page): Locator => page.locator(`[${PII_ATTRIBUTE}]`);
