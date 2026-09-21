import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

/**
 * RTL ratchet — physical direction utilities may only go DOWN.
 *
 * WHY THIS EXISTS. The Arabic toggle flipped `dir` to rtl while 595 Tailwind direction
 * utilities across app/ and src/ stayed physical (`ml-`, `pr-`, `text-right`, `left-0`,
 * `border-l`, `rounded-r…`). The result was not an untranslated page but a broken one:
 * labels colliding, icons on the wrong side, the mobile drawer sliding across the viewport
 * instead of off it. 584 of those were converted to logical equivalents (`ms-`, `pe-`,
 * `text-end`, `start-0`, `border-s`, `rounded-e…`). Nine were deliberately left physical,
 * and every one of them is listed in ALLOWED below with its reason.
 *
 * This is the same shape as the backend's ExecutionStrategyLintTests and
 * QueryFilterBypassRatchetTests: a pinned inventory that a future change may shrink but
 * never grow. It is a source lint — no browser, no stack — so it runs in
 * e2e/playwright.browserless.config.ts alongside ui-truthfulness-state.spec.ts.
 *
 * IF THIS TEST FAILS because you added a physical class:
 *   use the logical one. ml-→ms-, mr-→me-, pl-→ps-, pr-→pe-, text-left→text-start,
 *   text-right→text-end, left-→start-, right-→end-, border-l→border-s, border-r→border-e,
 *   rounded-l→rounded-s, rounded-r→rounded-e, rounded-tl→rounded-ss, rounded-tr→rounded-se,
 *   rounded-bl→rounded-es, rounded-br→rounded-ee. They compile to the inset-inline and
 *   margin-inline family and flip themselves; Tailwind 3.4 supports all of them.
 *
 * IF THIS TEST FAILS because your class genuinely must stay physical:
 *   add it to ALLOWED **with a reason a reviewer can disagree with**. "Genuinely physical"
 *   means tied to the viewport or to a glyph, not to reading order — a decorative gradient,
 *   a centering idiom, a mirrored-by-hand control, a chart axis.
 *
 * IF THIS TEST FAILS saying the pin is now too high: you removed one. Lower the pin.
 */

const FRONTEND_ROOT = process.cwd();
const SCANNED_DIRS = ['app', 'src'];
const SCANNED_EXT = new Set(['.tsx', '.ts', '.css']);

/**
 * Physical direction utilities, with the same value grammar Tailwind uses so that
 * `rounded-lg`, `border-rose-500` and the word "right-hand" are not false positives.
 */
const SPACE_VALUE = String.raw`(?:\[[^\]\s]*\]|[0-9]+(?:\.[0-9]+)?|px|auto|full)`;
const INSET_VALUE = String.raw`(?:\[[^\]\s]*\]|[0-9]+(?:\.[0-9]+)?|[0-9]+/[0-9]+|px|auto|full)`;
const VARIANT = String.raw`(?:(?:[a-z0-9_.\[\]#%/&>:-]+:)*)`;
const NOT_CLASS_CHAR = String.raw`(?<![\w:.\-/])`;

const PHYSICAL_PATTERNS: RegExp[] = [
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}-?m[lr]-${SPACE_VALUE}(?![\\w./-])`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}p[lr]-${SPACE_VALUE}(?![\\w./-])`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}text-(?:left|right)(?![\\w./-])`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}-?(?:left|right)-${INSET_VALUE}(?![\\w./-])`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}border-[lr](?=[-\\s"'\`}]|$)`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}rounded-(?:tl|tr|bl|br|l|r)(?=[-\\s"'\`}]|$)`, 'g'),
  new RegExp(`${NOT_CLASS_CHAR}${VARIANT}(?:float|clear)-(?:left|right)(?![\\w./-])`, 'g'),
];

/**
 * The complete, deliberate exception list. Key is `<path>::<class>`.
 *
 * Every entry is a place where the physical class is CORRECT — it is bound to the viewport
 * or to a transform, not to reading order — so converting it would be a regression, not a fix.
 */
const ALLOWED: Record<string, string> = {
  // ── Decorative background glows ────────────────────────────────────────────
  // Aurora/radial-gradient blobs behind the sign-in art and the ESS hero. They are
  // composition, not content: nothing reads along them, and mirroring the art in Arabic
  // buys no legibility while changing the brand image on the first screen a client sees.
  // Recognisable by the blur-2xl/blur-3xl + pointer-events-none pairing.
  // src/views/LoginPage.tsx had three entries here (-left-1/4, right-[-15%],
  // left-[20%]) for the blurred aurora blobs of the old panel-grid sign-in
  // page. The V3 rebuild deleted that markup: the field is now drawn by a
  // shader on a full-bleed canvas, with a CSS static field behind it, and
  // neither uses a physical inset. The exceptions went with the code, which
  // is why the pin below drops from 9 to 6.
  'app/platform/login/page.tsx::-left-1/4': 'decorative radial glow; viewport composition, not reading order',
  'app/platform/login/page.tsx::right-[-15%]': 'decorative radial glow; viewport composition, not reading order',
  'src/views/EmployeeSelfServicePage.tsx::right-0': 'decorative blurred disc on the ESS hero card (translate-x-16 + blur-3xl)',
  'src/views/EmployeeSelfServicePage.tsx::left-1/3': 'decorative blurred disc on the ESS hero card (blur-2xl)',

  // ── Centering idiom ────────────────────────────────────────────────────────
  // `left-1/2` + `-translate-x-1/2` is horizontal CENTERING, not a leading edge. The
  // transform is physical, so swapping only the inset to `start-1/2` would shift the
  // tooltip a further half-width to the right in Arabic instead of centring it.
  'src/components/InfoTip.tsx::left-1/2': 'centering idiom paired with -translate-x-1/2; logical inset would double-shift in RTL',
};

type Finding = { key: string; file: string; token: string; line: number };

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name === '.next' || entry.name === 'dist') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (SCANNED_EXT.has(path.extname(entry.name))) out.push(full);
  }
  return out;
}

function scan(): { findings: Finding[]; files: string[] } {
  const files: string[] = [];
  for (const d of SCANNED_DIRS) files.push(...walk(path.join(FRONTEND_ROOT, d)));

  const findings: Finding[] = [];
  for (const full of files) {
    const rel = path.relative(FRONTEND_ROOT, full);
    const lines = fs.readFileSync(full, 'utf8').split('\n');
    lines.forEach((line, i) => {
      for (const pattern of PHYSICAL_PATTERNS) {
        pattern.lastIndex = 0;
        for (const match of line.matchAll(pattern)) {
          const token = match[0].split(':').pop()!;
          findings.push({ key: `${rel}::${token}`, file: rel, token, line: i + 1 });
        }
      }
    });
  }
  return { findings, files: files.map((f) => path.relative(FRONTEND_ROOT, f)) };
}

test.describe('RTL logical-property ratchet', () => {
  test('the lint actually sees the frontend', () => {
    const { files } = scan();
    // A silent zero (wrong cwd, renamed directory) would make every assertion below vacuous.
    expect(files.length).toBeGreaterThan(150);
    expect(files).toContain('src/layouts/Sidebar.tsx');
  });

  test('no physical direction utility outside the documented allow-list', () => {
    const { findings } = scan();
    const unexpected = findings.filter((f) => !(f.key in ALLOWED));

    const report = unexpected
      .map((f) => `  ${f.file}:${f.line}  ${f.token}`)
      .sort()
      .join('\n');

    expect(
      unexpected.length,
      unexpected.length === 0
        ? ''
        : `${unexpected.length} physical direction utilit${unexpected.length === 1 ? 'y' : 'ies'} found.\n` +
          `Arabic (dir=rtl) does not flip these — they are the defect this ratchet exists to stop.\n` +
          `Use the logical form (ms-/me-/ps-/pe-/text-start/text-end/start-/end-/border-s/border-e/\n` +
          `rounded-s/rounded-e/rounded-ss/rounded-se/rounded-es/rounded-ee), or add an entry to\n` +
          `ALLOWED in this file with a reason a reviewer can argue with.\n\n${report}\n`,
    ).toBe(0);
  });

  test('the pinned exception count may only go down', () => {
    // Pinned 2026-09-20 at the nine exceptions documented in ALLOWED. If you removed one,
    // lower this number in the same commit so the ratchet keeps its teeth.
    //
    // 2026-09-21: 9 → 6. The login V3 rebuild deleted the three blurred aurora
    // blobs in src/views/LoginPage.tsx that held the only physical insets on
    // that page; the shader canvas and the CSS static field that replaced them
    // use none. Ratcheted down, never up.
    const PINNED_PHYSICAL_EXCEPTIONS = 6;

    const { findings } = scan();
    const allowed = findings.filter((f) => f.key in ALLOWED);

    expect(
      allowed.length,
      `Physical-class exceptions changed from the pinned ${PINNED_PHYSICAL_EXCEPTIONS} to ${allowed.length}. ` +
        `If you converted one, lower the pin. If you added one, it needs a reason in ALLOWED and a reviewer.`,
    ).toBe(PINNED_PHYSICAL_EXCEPTIONS);

    // Nothing in ALLOWED may rot into a dead entry that quietly licenses a future physical class.
    const live = new Set(allowed.map((f) => f.key));
    const dead = Object.keys(ALLOWED).filter((k) => !live.has(k));
    expect(dead, `ALLOWED lists exceptions that no longer exist; delete them: ${dead.join(', ')}`).toEqual([]);
  });

  test('divide-x is paired with rtl:divide-x-reverse', () => {
    // divide-x puts the border on each child except the FIRST. Under dir=rtl the first child
    // is the rightmost one, so the divider between the first two columns disappears and a
    // stray border appears on the outer edge. divide-x-reverse moves it to the other side.
    const { files } = scan();
    const offenders: string[] = [];
    for (const rel of files) {
      const lines = fs.readFileSync(path.join(FRONTEND_ROOT, rel), 'utf8').split('\n');
      lines.forEach((line, i) => {
        if (/(?<![\w:.\-/])divide-x(?![\w.-])/.test(line) && !line.includes('rtl:divide-x-reverse')) {
          offenders.push(`  ${rel}:${i + 1}`);
        }
      });
    }
    expect(offenders, `divide-x without rtl:divide-x-reverse:\n${offenders.join('\n')}`).toEqual([]);
  });

  test('off-canvas drawers carry an RTL counterpart for their transform', () => {
    // Transforms are physical even when the panel is anchored logically. A drawer at `start-0`
    // hidden with `-translate-x-full` slides ACROSS the viewport in Arabic instead of off it.
    const { files } = scan();
    const offenders: string[] = [];
    for (const rel of files) {
      const lines = fs.readFileSync(path.join(FRONTEND_ROOT, rel), 'utf8').split('\n');
      lines.forEach((line, i) => {
        if (line.includes('-translate-x-full') && !line.includes('rtl:translate-x-full')) {
          offenders.push(`  ${rel}:${i + 1}`);
        }
      });
    }
    expect(offenders, `-translate-x-full without rtl:translate-x-full:\n${offenders.join('\n')}`).toEqual([]);
  });

  test('the RTL shell contract is in place', () => {
    const layout = fs.readFileSync(path.join(FRONTEND_ROOT, 'app/layout.tsx'), 'utf8');
    // dir must exist on the server-rendered html, and be corrected before first paint —
    // LocaleProvider's effect runs too late and only inside the tenant shell.
    expect(layout).toContain('dir="ltr"');
    expect(layout).toContain("localStorage.getItem('kynexone-locale')");
    expect(layout).toContain('suppressHydrationWarning');
    // Inter has no Arabic coverage; without a fallback the Arabic UI renders in whatever
    // the OS happens to supply.
    expect(layout).toContain('IBM+Plex+Sans+Arabic');

    const css = fs.readFileSync(path.join(FRONTEND_ROOT, 'src/styles/index.css'), 'utf8');
    expect(css).toContain("[dir='rtl'] .lucide-chevron-right");
    expect(css).toContain("[dir='rtl'] .field-ltr");
    // Toggle glyphs encode their state in which side they point at; mirroring inverts them.
    expect(css).not.toContain("[dir='rtl'] .lucide-toggle-left");
    expect(css).not.toContain("[dir='rtl'] .lucide-play");
  });
});
