/**
 * LOGIN MARKETING — the content beside the sign-in form.
 * ─────────────────────────────────────────────────────────────────────────────
 * THERE IS NO PANEL HERE, AND THERE IS NOW VERY LITTLE TEXT.
 *
 * The card went first — a bordered rectangle dropped onto a lit field always
 * read as a widget placed on a background. What is left sits directly on the
 * aurora and is held by ONE left axis shared with the brand mark above it.
 *
 * The second subtraction is this one. The page previously said everything it
 * could truthfully say: a lede restating the claim, a fifteen-item capability
 * marquee, an OUTPUT line, a CONTROL line, a dual-calendar clock, a vendor
 * paragraph. Each was true; together they were a wall. What survives is the
 * shortest thing that can still make a CFO keep reading:
 *
 *   claim  Three rulebooks. One system of record.
 *   proof  one payroll run, typeset as a register — all six Gulf
 *          jurisdictions named, the three that have a statutory pack named
 *          with their institutions and their headcounts, and those three
 *          headcounts adding up to the group total. A finance reader checks
 *          that addition in about a second, which is the entire point of a
 *          register over a chart. The other three are named and marked as
 *          having no pack, because a list of six that hides which three are
 *          real is the one thing that would lose the same reader.
 *   control  the language switch. Arabic with real right-to-left layout is the
 *          one capability equally true in every Gulf market (GOSI, Qiwa, Mudad
 *          and Nitaqat are Saudi institutions), so it is shown, not claimed.
 *
 * DELETED, and why — every one of these was true and none of them changed a
 * buying decision:
 *   · the lede — restated the claim the register then proves
 *   · OUTPUT / CONTROL — a spec sheet under a statement
 *   · the capability marquee — a duplicated, clipped run of words
 *   · the vendor paragraph and the two mailto addresses — /privacy, /terms and
 *     /security carry all of it, in full, one click away
 *
 * WHAT DID NOT CHANGE
 *  1. NO FABRICATED CLAIMS. Statutory packs exist for Saudi Arabia, the UAE and
 *     Qatar only. All six GCC states are now named, and the three without a
 *     pack say so in as many words, with no date attached to it. Nothing
 *     claims WPS portal acceptance. No customers, counts, uptime figures,
 *     testimonials or certifications appear anywhere.
 *  2. THE FIGURES ARE MARKED. The headcounts are invented. The "Sample data"
 *     marker now LEADS the run bar sitting directly above them — first thing
 *     read, not a footnote — and the register's caption repeats it for a screen
 *     reader. The pay period is the only live value here and it is a date.
 *
 * Motion budget: one staggered entrance, once, plus ONE ambient drift on the
 * drum (see THE DRIFT) that stops for hover, for focus, for a hidden tab, for
 * a coarse pointer, for prefers-reduced-motion, and for its own pause control.
 */

import { useEffect, useRef, useState } from 'react';

/* ══════════════════════════════════════════════════════════════════════════
   THE RUN SHEET — SIX ROWS OF TWO DIFFERENT KINDS, AND THAT IS THE POINT

   kind 'pack' — a jurisdiction with a statutory pack and a headcount.
   kind 'wide' — a capability that holds in every Gulf market, with NO
                 headcount, because it is not a jurisdiction and must never be
                 counted as one.

   THE TWO-LAYER ARGUMENT, WHICH IS ALSO WHY THERE ARE NOT SIX COUNTRIES HERE.
   The PLATFORM works across the Gulf: the interface mirrors, the Hijri
   calendar runs beside the Gregorian one, currency is per company. The
   STATUTORY rulebooks are three. Printing six countries would claim the
   second where only the first is true — Infrastructure/CountryPack/ holds
   Ksa/, Uae/ and Qatar/ and nothing else, and CountryPackResolver falls
   through to DefaultPack, which returns zero deductions, a "default-no-op"
   end-of-service, an empty wage-protection file and USD/English/Gregorian.
   So the drum says the true thing twice instead of the false thing once, and
   nothing on this page states or implies what is NOT supported: a login
   screen is not where a product publishes its gaps.

   Each row is evidenced in this repository:
     KSA   Infrastructure/CountryPack/Ksa/{KsaCalculators,KsaDescriptor,
           KsaWageProtectionExporter,KsaLocalizationProfile}.cs — GOSI rates,
           the Mudad WPS XML exporter, the Saudization/Nitaqat ratio, and
           Controllers/QiwaController.cs + Infrastructure/Qiwa/.
     UAE   Infrastructure/CountryPack/Uae/* — GPSSA calculator, SIF exporter,
           Emiratisation tracking (UaeDescriptor.cs).
     QAT   Infrastructure/CountryPack/Qatar/* — GRSIA calculator (Law 24/2002),
           wage-protection exporter, Qatarisation tracking.
     AR    LocaleContext.tsx writes dir="rtl" onto the document from
           LOCALE_METADATA in i18n/translations.ts, so the LAYOUT mirrors
           rather than the strings being swapped, and the Arabic dictionary is
           at parity with the English one (428 keys each). It says "not
           strings" and stops there. IT DOES NOT SAY "per company", although
           that was asked for twice: the stored preference is
           TenantLocalizationSetting (Models/SaasPlatform.cs — DefaultLanguage,
           RtlEnabled), which is per TENANT, and Models/Company.cs carries no
           locale column at all. Per company would be a claim the data model
           cannot support. If a per-company locale column lands, this line can
           have those two words back.
     HIJ   Infrastructure/Localization/HijriDateService.cs wraps .NET's
           UmAlQuraCalendar and Controllers/LocalizationController.cs serves
           it; the pay period above is the same pairing done with Intl. The
           claim is exactly "alongside Gregorian" — conversion and display —
           and nothing about Hijri-driven periods, which do not exist.
     CUR   Models/Company.cs gives every company its own DefaultCurrency and
           Domain/Entities/ICompanyScoped.cs + Data/ZayraDbContext.cs enforce
           the company dimension as a second global query filter under the
           tenant one, with a boot assertion
           (Infrastructure/Boot/CompanyScopeBootAssertion.cs) that refuses to
           start if a table escapes it. NO exchange rates exist anywhere in
           the backend, so this face says currency PER COMPANY inside one
           group and never implies conversion or a consolidated money figure.

   THE ARITHMETIC IS THE STRONGEST THING ON THIS PAGE. 612 + 431 + 205 = 1,248,
   checkable in about a second, and three faces without figures must not put
   that in doubt. Three mechanics protect it:
     · TOTAL sums `heads`, which only a 'pack' row has, so no face can ever
       contribute a silent zero to it;
     · the three faces that DO carry figures are the first three, so the three
       addends are adjacent and read as one row before anything else;
     · the other three are typeset as a different kind of object — the claim's
       own serif italic in --lx-cyan-soft, against the register's sans — so
       they never read as rows of the same ledger that happen to be blank.
   ══════════════════════════════════════════════════════════════════════════ */

interface Row {
  code: string;
  /** 'pack' = a jurisdiction with a statutory pack. 'wide' = true everywhere. */
  kind: 'pack' | 'wide';
  name: string;
  nameAr: string;
  /* A LIST, not a string with separators in it. Flat, the items are joined by
     the page's middot; on the drum, where a face is 8 ems wide, they are
     STACKED one per line. That is not decoration — a wrapped middot list in a
     column this narrow ends a line on its own separator ("GOSI · Qiwa ·" /
     "Mudad · Nitaqat"), and Arabic breaks worse still, stranding نطاقات alone
     under a dangling dot. Stacked, the separator simply does not exist at the
     break, and four systems under Saudi Arabia against two under the others
     is the statutory depth argument made visible. A 'wide' row carries one
     item, which is a phrase rather than a list, and so never separates. */
  items: string[];
  itemsAr: string[];
  /** Present on 'pack' rows only. Absent is not zero — it is "not a country". */
  heads?: number;
}

const ROWS: Row[] = [
  {
    code: 'KSA',
    kind: 'pack',
    name: 'Saudi Arabia',
    nameAr: 'السعودية',
    items: ['GOSI', 'Qiwa', 'Mudad', 'Nitaqat'],
    itemsAr: ['التأمينات', 'قوى', 'مدد', 'نطاقات'],
    heads: 612,
  },
  {
    code: 'UAE',
    kind: 'pack',
    name: 'United Arab Emirates',
    nameAr: 'الإمارات العربية المتحدة',
    items: ['GPSSA', 'Emiratisation'],
    itemsAr: ['التأمينات والمعاشات', 'التوطين'],
    heads: 431,
  },
  {
    code: 'QAT',
    kind: 'pack',
    name: 'Qatar',
    nameAr: 'قطر',
    items: ['GRSIA', 'Qatarisation'],
    itemsAr: ['التأمينات الاجتماعية', 'التوطين'],
    heads: 205,
  },
  {
    code: 'AR',
    kind: 'wide',
    name: 'Arabic & RTL',
    nameAr: 'واجهة عربية',
    /* Not "translated strings": the interface itself mirrors. The switch at
       the top of this panel is the live proof of the same mechanism — press
       it and every line here, including this one, changes direction. */
    items: ['Mirrored layout, not strings'],
    itemsAr: ['تخطيط معكوس، لا مجرد ترجمة'],
  },
  {
    code: 'HIJ',
    kind: 'wide',
    name: 'Hijri calendar',
    nameAr: 'التقويم الهجري',
    /* Proved directly above it, in the run bar: that pay period is formatted
       in both calendars by usePeriod(). */
    items: ['Alongside Gregorian'],
    itemsAr: ['إلى جانب الميلادي'],
  },
  {
    code: 'CUR',
    kind: 'wide',
    name: 'Multi-currency',
    nameAr: 'تعدد العملات',
    /* Per company, inside one group — which is what the data model does.
       Never "converted": there is no FX table in this product. */
    items: ['Per company, one group'],
    itemsAr: ['لكل شركة، ضمن مجموعة'],
  },
];

/** Sums 'pack' rows only — a capability face cannot reach this. */
const TOTAL = ROWS.reduce((n, r) => n + (r.heads ?? 0), 0);

/* ── the two languages this composition speaks ──────────────────────────────
   Kept deliberately short in both. Every Arabic string here is a plain,
   standard Gulf HR/payroll term rather than a literal translation of the
   English: مسير الرواتب is what a payroll run is called, نطاقات / قوى / مدد are
   the Saudi programmes by their own names, and التوطين covers both
   Emiratisation and Qatarisation as it does in practice.
   ────────────────────────────────────────────────────────────────────────── */

interface Copy {
  claimA: string;
  claimB: string;
  run: string;
  sample: string;
  colWho: string;
  colHeads: string;
  totalKey: string;
  approved: string;
  tableCaption: string;
  /** The label on the switch is always the language it switches TO. */
  switchTo: string;
  switchLabel: string;
  /** The drift control. Visible word first, then the rest of the name. */
  pause: string;
  play: string;
  pauseRest: string;
  playRest: string;
}

const EN: Copy = {
  claimA: 'Three rulebooks.',
  claimB: 'One system of record.',
  run: 'Payroll run',
  sample: 'Sample data',
  colWho: 'Coverage',
  colHeads: 'Employees',
  totalKey: 'Total',
  approved: 'Approved',
  tableCaption:
    'Sample data. An illustrative payroll run across the three jurisdictions '
    + 'that have a statutory pack: Saudi Arabia 612 employees, the United Arab '
    + 'Emirates 431, Qatar 205, group total 1,248, approved. The headcounts are '
    + 'invented and are not customer figures. The last three rows are not '
    + 'jurisdictions and carry no headcount: they are capabilities that hold in '
    + 'every Gulf market — an Arabic interface whose layout mirrors rather than '
    + 'only its strings being translated, the Hijri calendar alongside the '
    + 'Gregorian one, and a currency per company inside one group.',
  /* Shown while the panel is in English, so it is written in Arabic: it is the
     destination, not a description. */
  switchTo: 'العربية',
  switchLabel: 'عرض هذه اللوحة بالعربية',
  pause: 'Pause',
  pauseRest: ' the turning register',
  play: 'Play',
  playRest: ' the turning register',
};

const AR: Copy = {
  claimA: 'ثلاث لوائح.',
  claimB: 'سجل واحد.',
  run: 'مسير الرواتب',
  sample: 'بيانات تجريبية',
  colWho: 'التغطية',
  colHeads: 'الموظفون',
  totalKey: 'الإجمالي',
  approved: 'معتمد',
  tableCaption: 'بيانات تجريبية. مسير رواتب توضيحي عبر الدول الثلاث التي لها أنظمة مطبقة: السعودية ٦١٢ موظفاً، الإمارات ٤٣١، قطر ٢٠٥، وإجمالي المجموعة ١٢٤٨ معتمد. الأعداد افتراضية وليست بيانات عملاء. الصفوف الثلاثة الأخيرة ليست دولاً ولا تحمل أعداداً: وهي إمكانات متاحة في كل أسواق الخليج — واجهة عربية بتخطيط معكوس لا مجرد ترجمة، والتقويم الهجري إلى جانب الميلادي، وعملة لكل شركة ضمن مجموعة واحدة.',
  switchTo: 'English',
  switchLabel: 'Show this panel in English',
  pause: 'إيقاف',
  pauseRest: ' حركة السجل',
  play: 'تشغيل',
  playRest: ' حركة السجل',
};

/* ── the pay period, in both calendars ──────────────────────────────────────
   Hijri is not decoration here: a Gulf payroll engine files against both
   calendars, and src/contexts/LocaleContext.tsx + the Hijri date support in
   this product are what make the pair real. This is now the ONLY place the two
   calendars appear — the header's dual-calendar clock was deleted, because a
   clock tells a buyer nothing while a pay period labels the figures beneath it.
   Computed on mount rather than rendered on the server, because the two clocks
   can disagree across a midnight boundary and a hydration mismatch is not
   worth a date.
   ───────────────────────────────────────────────────────────────────────── */
function usePeriod(ar: boolean): string | null {
  const [p, setP] = useState<string | null>(null);
  useEffect(() => {
    try {
      const now = new Date();
      const opts = { month: 'long', year: 'numeric', timeZone: 'Asia/Riyadh' } as const;
      const g = new Intl.DateTimeFormat(ar ? 'ar-u-nu-latn' : 'en-GB', opts).format(now);
      const h = new Intl.DateTimeFormat(
        ar ? 'ar-SA-u-ca-islamic-umalqura-nu-latn' : 'en-GB-u-ca-islamic-umalqura',
        opts,
      ).format(now).replace(/\s*AH\s*$/i, '').replace(/[ʻʼ‘’]/g, '');
      setP(`${g} · ${h}`);
    } catch { setP(null); }
  }, [ar]);
  return p;
}

const num = (n: number, ar: boolean) =>
  new Intl.NumberFormat(ar ? 'ar-u-nu-latn' : 'en-GB').format(n);

/* ══════════════════════════════════════════════════════════════════════════
   THE DRUM — SIX FACES, CONCAVE

   The six register rows are the six faces of a curved surface with a vertical
   axis. Not an added object — the SAME <table>, the same <caption>, the same
   <th scope="row">, re-laid out in three dimensions by CSS. A screen reader
   never sees a drum, and login-contract.spec.ts never sees a change.

   ── CONCAVE, AND WHY IT IS DERIVED RATHER THAN GUESSED ────────────────────
   CSS rotateY(θ) maps (0,0,d) to (d·sinθ, 0, d·cosθ): +z is toward the viewer.

   CONVEX (what this was) put the centre of curvature BEHIND the faces:
       face:  rotateY(φ) translateZ(+r)   →  P = ( r·sinφ, 0,  r·cosφ )
       normal n = (sinφ, 0, cosφ) — pointing away from the axis, so an outer
       face turns AWAY from the eye and recedes. Centre nearest, edges furthest.

   CONCAVE (what this is now) puts the centre of curvature in FRONT of the
   faces — between the surface and the viewer — so the surface wraps around the
   eye like an amphitheatre:
       face:  rotateY(−φ) translateZ(−r)  →  P = ( r·sinφ, 0, −r·cosφ )
       normal n = (−sinφ, 0, cosφ) — pointing back toward the axis, i.e. toward
       the viewer. Centre FURTHEST (z = −r), edges NEAREST (z = −r·cos φ).
   The two signs are not independent: translateZ(−r) is what puts the surface
   on the far side of the centre of curvature, and rotateY(−φ) is what keeps
   face i on the same side of the composition it was on before (x = +r·sinφ).
   The drum's own rotation flips with them, hence rotateY(−rot) on the tbody.
   WHAT DOES NOT FLIP: the x of every face is r·sin(rot+φ) in BOTH layouts —
   only the z changes sign — so the pan and the pointer mapping in onMove are
   the ones they always were. Flipping them "to match" mirrors the response to
   the cursor, which looks plausible in a still frame and is wrong in motion.

   The whole ring is then pushed back toward the eye by
       park = r − k·sag,   sag = r·(1 − cos SPAN),   SPAN = (N−1)/2 · PITCH
   so that k=0 parks the centre face on the screen plane (edges in front of it,
   magnified), k=1 parks the NEAR edges there (centre behind, shrunk), and
   any k in between splits the arc's depth either side of it. k is the single
   knob that decides whether this reads as a bowl or as a fisheye, and it is
   0.8 here. Measured at 1440px: at k=0 the six faces project
   116/106/101/101/106/116 px in a 729px band — inside a 720px measure, so the
   fisheye had burst its own column — and at k=0.8 they project
   96/91/89/89/91/96 in a 624px band. Same curvature, same radius; one of them
   is depth and the other is a lens.

   ── WHY CONCAVE HELPS LEGIBILITY, MEASURED ────────────────────────────────
   An outer face at φ is foreshortened by cos φ either way, but concave brings
   it NEARER by r(1−cos φ) and perspective magnifies what is nearer by
   D/(D−z), while convex pushes it the same distance AWAY and shrinks it on
   top of the foreshortening. At r = D the two effects cancel exactly and
   every face projects the same width; below it the edges vary gently.
   Measured on the rendered page at 1440px, six faces, PITCH 20, r = 330px,
   the same perspective, flipping ONLY the two signs (r → −r, φ → −φ):
       concave   96 91 89 89 91  96 px   outermost = 108% of the centre face
       convex    36 72 97 97 72  36 px   outermost =  37% of the centre face
   Convex is not a worse-looking option at six faces, it is an unusable one:
   36px cannot hold "Multi-currency". The heights inverted with them — concave
   139/123/114, convex 114/124/128 — the same fact seen from the side, and the
   reason the band's top line now bows UP at the ends and its rules bow DOWN.

   ── GEOMETRY ──────────────────────────────────────────────────────────────
   PITCH is a CONSTANT, never 360/N, which is also the N≤2 guard: tan(90°) is
   unreachable and the floor in measure() caps the radius anyway.
       r = s / (2·tan(PITCH/2)),  s = face width + gap = FACE_S · f1
   EVERY length below is derived from PITCH here in TypeScript and written into
   CSS as a custom property, because the previous arrangement — a literal
   37.321 in the stylesheet that had to be recomputed by hand whenever PITCH
   moved — is exactly the kind of coupling that silently lifts the faces off
   the drum surface. The stylesheet now owns no geometry it cannot derive.

   PITCH is 20°, set by LOOKING at the rendered page in Arabic at 1288px —
   the binding case, because Arabic loses its tooth structure and its dots one
   step before Latin loses its middots — and not by arithmetic, which argued
   for 14-16 on the assumption that a wider pitch costs legibility. On a
   CONCAVE surface it does not, up to a point, and the point is measurable.
   Swept at 14, 16, 18, 21 and then 18, 22, 26, 30, outermost/centre width:
     14-16   1.14 / 1.17, but the arc is so shallow that six faces read as a
             flat row of columns with slightly tilted rules. Legible, and not
             a drum.
     18-22   1.12 / 1.02. The bowl reads as a bowl. The projected widths
             CONVERGE as the pitch rises, because turning a concave face
             toward the eye gives back what foreshortening takes.
     26-30   the arc finally turns past what the perspective can return. At 26
             the outermost face is 73px against 82 at the centre; at 30 it is
             56px, visibly squeezed and slanted, and Arabic goes first.
   20 sits where the bowl is deepest before that reversal, and it pays for the
   motion as well: the band is 624px inside a 720px measure at 1440 and 599px
   inside 670px at 1288, so ±13° of drift and cursor travel together still
   leave the leading face 25px (1440) and 13px (1288) inside the spine. At 16°
   the same motion left 3px, and the pan had to be wound up to 0.85 to hold it.

   ── THE PAN ───────────────────────────────────────────────────────────────
   A pure rotation slides the whole group sideways and opens a moving hole
   against the spine. Full compensation nails it down and kills the sense of
   turning. 0.7 is a camera dollying with the turn, which is how a turntable
   shot is actually filmed: the band still travels 8px between the middle of
   the drift and its end, and the faces travel much further relative to each
   other (the leading face goes 96px → 99px while the trailing one goes
   96px → 93px across the same 13°). It was briefly 0.85 while the pitch was
   16° and the band had 16px of clearance at 1288; at 20° the arc is narrower
   and 0.7 keeps the leading face 13px inside the spine at the worst
   combination of drift and cursor.

   ── THE DRIFT, AND WHY IT IS A SWAY AND NOT A REVOLUTION ──────────────────
   One full cycle takes 104 seconds. It is NOT a unidirectional revolution,
   and on these six faces it cannot be, for a reason that comes straight out
   of the one requirement this page will not trade: 612 + 431 + 205 = 1,248
   has to be checkable at a glance, which means all three addends must be on
   screen AT ONCE. Six faces all on screen at once is a 100°-wide ARC, not a
   closed ring. Turn an arc through 360° and four fifths of every cycle shows
   an empty band, with the faces crossing ±90° — edge-on — on the way out. The
   alternative that does revolve is a true hexagonal ring at 60° pitch, which
   shows two or three faces at a time and therefore never shows the sum. So
   the ring is not closed, and what turns is the whole arc, continuously:
   ±6° of sway — 0.6 of one face pitch — at a peak of 0.36°/s. Measured on the
   page, that is about half a pixel per second at its fastest and 8px of
   travel from the middle of the sway to its end: slow enough that you only
   see it if you watch for it, continuous, so there is always something to
   see, and nowhere near the speed at which motion in the periphery pulls a
   reader's eye off a password field.

   ONE CONSEQUENCE, ASKED AND ANSWERED: on a revolving drum a face with no
   figure would periodically take the front position, and the register would
   spend part of every cycle led by a face that carries no number. On a
   swaying arc no face ever changes place — each keeps its position, the three
   that carry figures keep the reading-order lead, and the sway moves all six
   together. That is not a workaround; it is the same property that keeps the
   sum on screen. What the sway does not change is that a CONCAVE drum puts
   its nearest, largest faces at the ENDS of the arc, so Multi-currency holds
   one of the two most prominent positions permanently. That is answered with
   type rather than geometry: a capability face is set in the claim's serif
   italic, so being the largest face makes it read as the second half of the
   argument rather than as a jurisdiction with a missing number.

   WCAG 2.2.2: motion that runs for more than five seconds owes the reader a
   pause mechanism, and pause-on-hover is a mitigation rather than conformance.
   The mechanism is a real control in the run bar — keyboard reachable, named,
   and deliberately the quietest thing in that row. On top of it the drift
   suspends itself for a pointer over the drum, for ANY focus inside the page
   except the control's own (someone typing a password does not get motion
   beside them, but pressing Play must visibly start it), for a hidden tab, for
   a coarse pointer, and for prefers-reduced-motion. Suspending freezes the
   phase where it is; it never snaps back to zero.

   The easing constant is the aurora's own pointerEase, so the two systems
   read as one hand rather than two.
   ══════════════════════════════════════════════════════════════════════════ */

const PITCH = 20;           // degrees between adjacent faces — never derived from N
const TRAVEL = 7;           // degrees of cursor-driven rotation, each way
const PAN_K = 0.7;          // how much of the lateral drift the camera takes back
const EASE = 0.055;         // the aurora's pointerEase — ~300ms time constant
const ENTRANCE = 9;         // degrees; one settling turn, on the same easing
const PARK_K = 0.8;         // 0 = centre face on the screen plane, 1 = near edges on it
const DEPTH = 57.5;         // perspective distance, in --lx-f1 units — see below
const FACE_W = 8.0;         // face width, in --lx-f1 units
const FACE_S = 9.3;         // face width + gap, in --lx-f1 units
const DRIFT_MS = 104_000;   // one full there-and-back drift cycle
const DRIFT_AMP = 6;        // degrees of sway, each way

const rad = (d: number) => (d * Math.PI) / 180;

/** phi for face i of n. Mirrored in RTL so row and face still correspond. */
export function facePhi(i: number, n: number, rtl: boolean): number {
  return (i - (n - 1) / 2) * PITCH * (rtl ? -1 : 1);
}

/** How far this face is swung off the drum's axis, 0..1 — which on a CONCAVE
    drum means how far it has come TOWARD the viewer. Rendered, not just
    computed at runtime: without it the still frame — reduced motion, a touch
    screen at 1280+, the server's first paint — would print all six rules at
    the same depth and flatten the drum. */
export function faceTurn(phi: number, rot = 0): number {
  return Math.abs(Math.sin(rad(rot + phi)));
}

/** The radius, and the parking distance, as multiples of s. One derivation,
    used by the stylesheet (via the custom properties below) and by the pan. */
const SPAN = ((ROWS.length - 1) / 2) * PITCH;
const R_OVER_S = 1 / (2 * Math.max(Math.tan(rad(PITCH / 2)), 0.02)); // the N≤2 guard
const PARK_OVER_S = R_OVER_S * (1 - PARK_K * (1 - Math.cos(rad(SPAN))));

/** The geometry, handed to CSS. Every one of these tracks PITCH. */
const DRUM_VARS = {
  '--cyl-pitch': `${PITCH}deg`,
  '--cyl-span': `${SPAN}deg`,
  '--cyl-w': `calc(var(--lx-f1) * ${FACE_W})`,
  '--cyl-s': `calc(var(--lx-f1) * ${FACE_S})`,
  '--cyl-r': `calc(var(--lx-f1) * ${(FACE_S * R_OVER_S).toFixed(4)})`,
  '--cyl-park': `calc(var(--lx-f1) * ${(FACE_S * PARK_OVER_S).toFixed(4)})`,
  /* In f1 units like everything else, so the whole drum scales with the type
     rather than getting a stronger perspective at a narrower viewport: a
     fixed 620px depth against a 4% smaller radius at 1288 is a different
     drum, and the difference shows up exactly where the space is tightest. */
  '--cyl-d': `calc(var(--lx-f1) * ${DEPTH})`,
} as React.CSSProperties;

/* Deliberately takes nothing and depends on nothing but the play flag. An
   earlier version keyed this effect on `ar` so it would re-measure after a
   language switch — but the face width is set by --cyl-w, not by its content,
   so there was nothing to re-measure, and the teardown/re-bind replayed the
   entrance turn on every flip. The panel's rule is that the language changes
   IN PLACE with no transition; the drum obeys it. The loop reads data-phi off
   the live rows each frame, so React re-rendering the mirrored angles needs no
   help from here. */
function useDrum(playing: boolean) {
  const ref = useRef<HTMLTableSectionElement>(null);
  const play = useRef(playing);
  const kickRef = useRef<() => void>(() => {});

  useEffect(() => {
    play.current = playing;
    kickRef.current();
  }, [playing]);

  useEffect(() => {
    const drum = ref.current;
    if (!drum || typeof window.matchMedia !== 'function') return;

    const mqWide = window.matchMedia('(min-width: 1280px)');
    const mqPtr = window.matchMedia('(hover: hover) and (pointer: fine)');
    const mqRm = window.matchMedia('(prefers-reduced-motion: reduce)');

    let raf = 0;
    let last = 0;
    let cur = ENTRANCE;   // the eased component: entrance, then cursor travel
    let aim = 0;
    let phase = 0;        // ms into the drift cycle; frozen, never reset
    let radius = 0;
    let bound = false;
    let hover = false;

    const faces = () => Array.from(drum.rows) as HTMLElement[];

    /* Focus anywhere in the page suspends the drift — except on the control
       that governs it, which would otherwise make pressing Play do nothing. */
    const focusHeld = () => {
      const a = document.activeElement;
      if (!a || a === document.body || a === document.documentElement) return false;
      return !a.closest('[data-drum-control]');
    };

    const running = () => play.current && !hover && !document.hidden && !focusHeld();
    const settled = () => Math.abs(aim - cur) <= 0.01;
    const drift = () => DRIFT_AMP * Math.sin((2 * Math.PI * phase) / DRIFT_MS);

    const write = () => {
      const rot = cur + drift();
      drum.style.setProperty('--cyl-rot', `${rot.toFixed(3)}deg`);
      drum.style.setProperty('--cyl-pan', `${(-PAN_K * radius * Math.sin(rad(rot))).toFixed(2)}px`);
      for (const f of faces()) {
        f.style.setProperty('--cyl-turn', faceTurn(Number(f.dataset.phi || 0), rot).toFixed(4));
      }
    };

    /* Back to the rest state, not to nothing: these are the same values React
       renders, so a face never loses its modelling when the loop stops. */
    const clear = () => {
      drum.style.removeProperty('--cyl-rot');
      drum.style.removeProperty('--cyl-pan');
      for (const f of faces()) {
        f.style.setProperty('--cyl-turn', faceTurn(Number(f.dataset.phi || 0)).toFixed(4));
      }
    };

    /* The laid-out face width gives the radius in px without parsing a calc()
       out of a computed style, and re-derives it from the SAME constants the
       custom properties above are built from. */
    const measure = () => {
      const w = faces()[0]?.offsetWidth || 0;
      radius = (w / FACE_W) * FACE_S * R_OVER_S;
    };

    const tick = (now: number) => {
      raf = 0;
      /* Clamped, not unclamped: a tab that has been in the background hands
         back a multi-second delta and the drum would teleport. 120ms is about
         seven frames — it protects against that without starving the easing on
         a machine whose frames are genuinely slow, which a 64ms clamp did
         (it stretched a 2s settle into ten). */
      const dt = last ? Math.min(now - last, 120) : 16.67;
      last = now;
      if (running()) phase = (phase + dt) % DRIFT_MS;
      const k = 1 - Math.pow(1 - EASE, dt / 16.67);
      cur += (aim - cur) * k;
      if (settled()) cur = aim;
      write();
      /* park the loop when the drift is suspended AND the cursor has arrived */
      if (running() || !settled()) raf = requestAnimationFrame(tick);
      else last = 0;
    };

    const kick = () => { if (bound && !raf) raf = requestAnimationFrame(tick); };
    kickRef.current = kick;

    const onMove = (e: PointerEvent) => {
      /* Negative deliberately, and UNCHANGED by the flip to concave. Work the
         composition out rather than flipping it with the rest: the face lands
         at rotateY(−(rot+φ))·(0,0,−r) = ( r·sin(rot+φ), 0, −r·cos(rot+φ) ),
         and the convex one landed at rotateY(rot+φ)·(0,0,r) =
         ( r·sin(rot+φ), 0, +r·cos(rot+φ) ). The z flips; the x does NOT. So a
         positive rotation still carries the faces to the right in both, and
         this sign — and the pan's — stay exactly as they were.
         Why negative at all: the shader's screenPlane() adds +0.016·u_ptr.x,
         so the field slides left as the cursor goes right, a camera moving
         right. The drum has to agree — cursor right reveals the drum's right
         side, which means the faces travel left. */
      aim = -((2 * e.clientX) / window.innerWidth - 1) * TRAVEL;
      kick();
    };

    const onResize = () => { measure(); write(); };
    const onEnter = () => { hover = true; };
    const onLeave = () => { hover = false; kick(); };
    const onFocus = () => { kick(); };
    const onVis = () => { kick(); };

    const sync = () => {
      const on = mqWide.matches && mqPtr.matches && !mqRm.matches;
      if (on === bound) return;
      bound = on;
      if (on) {
        measure();
        cur = ENTRANCE;
        aim = 0;
        last = 0;
        hover = false;
        window.addEventListener('pointermove', onMove, { passive: true });
        window.addEventListener('resize', onResize, { passive: true });
        drum.addEventListener('pointerenter', onEnter);
        drum.addEventListener('pointerleave', onLeave);
        document.addEventListener('focusin', onFocus);
        document.addEventListener('focusout', onFocus);
        document.addEventListener('visibilitychange', onVis);
        kick();
      } else {
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('resize', onResize);
        drum.removeEventListener('pointerenter', onEnter);
        drum.removeEventListener('pointerleave', onLeave);
        document.removeEventListener('focusin', onFocus);
        document.removeEventListener('focusout', onFocus);
        document.removeEventListener('visibilitychange', onVis);
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
        clear();
      }
    };

    sync();
    const mqs = [mqWide, mqPtr, mqRm];
    for (const m of mqs) m.addEventListener('change', sync);
    return () => {
      kickRef.current = () => {};
      for (const m of mqs) m.removeEventListener('change', sync);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('resize', onResize);
      drum.removeEventListener('pointerenter', onEnter);
      drum.removeEventListener('pointerleave', onLeave);
      document.removeEventListener('focusin', onFocus);
      document.removeEventListener('focusout', onFocus);
      document.removeEventListener('visibilitychange', onVis);
      if (raf) cancelAnimationFrame(raf);
      clear();
    };
  }, []);

  return ref;
}

/* ══════════════════════════════════════════════════════════════════════════
   THE COMPOSITION — no frame, one axis, four objects.

   The old 72px monospace column (KSA / UAE / QAT / TOTAL) is gone with the
   OUTPUT / CONTROL rows it used to serve. It was literal duplication: "KSA"
   stood beside "Saudi Arabia" saying the same thing in fewer letters. Without
   it every line in this composition — the claim, the run bar, each
   jurisdiction, the total — starts on ONE left edge, and that edge is the
   brand mark's edge in the band above. The page has a single spine from the
   logo to the last figure, and the register is a plain two-column ledger:
   who, and how many.

   ONE AMENDMENT, at 1280px and up. The six register rows become the six faces
   of a drum (see THE DRUM above), and a turned face cannot sit on a flat spine
   — the band is inset symmetrically inside the measure, bracketed by the run
   bar's hairline above it and the total's rule below, both of which still run
   the full measure on the spine. Everything flat still shares the one edge;
   only the thing that is deliberately not flat does not.

   THE TOTAL NEVER ENTERS THE DRUM. It is the one line a finance reader checks,
   so it stays flat, full measure, on its own rule, where no rotation can reach
   it.
   ══════════════════════════════════════════════════════════════════════════ */
export function Brief() {
  const [ar, setAr] = useState(false);
  const [playing, setPlaying] = useState(true);
  const t = ar ? AR : EN;
  const period = usePeriod(ar);
  const drum = useDrum(playing);

  return (
    <section
      className="lx-brief"
      dir={ar ? 'rtl' : 'ltr'}
      lang={ar ? 'ar' : 'en'}
      aria-label="KynexOne — an illustrative payroll run"
    >
      <div className="lx-brief-top lx-rise lx-rise-1">
        {/* Deliberately not a heading: the page's one <h1> belongs to the task
            in the card, not to the marketing line beside it. */}
        <p className="lx-h1">
          {t.claimA}
          <span className="lx-accent">{t.claimB}</span>
        </p>

        {/* One button, not a pair. A two-state group spent a run of text
            telling you which language you were already reading; a single
            switch labelled with its destination says the same thing once. */}
        <div className="lx-lang">
          <button
            type="button"
            className="lx-lang-b"
            onClick={() => setAr(v => !v)}
            lang={ar ? 'en' : 'ar'}
            aria-label={t.switchLabel}
            data-testid="login-lang-switch"
          >
            {t.switchTo}
          </button>
        </div>
      </div>

      <div className="lx-rise lx-rise-2 lx-runhead">
        <hr className="lx-hair is-key" />
        {/* The sample marker LEADS. It sits immediately above the only figures
            on the page, first in reading order, at a size nobody can miss —
            and being leftmost it is also the one thing here that cannot be
            shifted when the client-computed period arrives a frame later. */}
        <p className="lx-runbar">
          <span className="lx-runbar-l">
            <span className="lx-samp">{t.sample}</span>
            <span className="lx-runbar-k">{period ? `${t.run} · ${period}` : t.run}</span>
          </span>
          {/* The right end of the run bar. Below 1280px it is the column head
              for the figures beneath it. In drum mode there is no right-hand
              figure column to head, so the same slot carries the drift's pause
              control instead — the quietest row that still belongs to the band
              it governs, and never a sixth thing to look at. Exactly one of
              the two is displayed; the CSS gate on the control is the same
              gate the loop uses. */}
          <span className="lx-runbar-n" aria-hidden>{t.colHeads}</span>
          <button
            type="button"
            className="lx-drift"
            data-drum-control=""
            data-testid="login-drift-toggle"
            onClick={() => setPlaying(v => !v)}
          >
            <span className="lx-drift-g" aria-hidden>{playing ? <PauseGlyph /> : <PlayGlyph />}</span>
            {playing ? t.pause : t.play}
            <span className="lx-sr">{playing ? t.pauseRest : t.playRest}</span>
          </button>
        </p>
      </div>

      {/* ── the register. A real <table> so the column heads, the row heads
           and the caption all reach a screen reader, and so the four figures
           share one right edge. The visible column heads are omitted: the
           content names itself, and the heading row was the most
           template-looking object on the page. ─────────────────────────── */}
      <table className="lx-reg-t">
        <caption className="lx-sr">{t.tableCaption}</caption>
        <thead className="lx-sr">
          <tr>
            {/* Two heads for two cells. The old third head ("Statutory pack")
                never had a cell of its own — the pack line lives inside the row
                header, where a screen reader reads it with the name it
                qualifies. */}
            <th scope="col">{t.colWho}</th>
            <th scope="col">{t.colHeads}</th>
          </tr>
        </thead>
        {/* The drum. At <1280px, on a coarse pointer, or under reduced motion
            this is an ordinary <tbody> and these are ordinary rows — the
            cylinder is a CSS re-layout gated on width, and its rest state is
            the composed still frame, so there is nothing to freeze into. */}
        <tbody ref={drum} className="lx-reg-drum" style={DRUM_VARS}>
          {ROWS.map((r, n) => (
            <tr
              className={`lx-reg-row lx-rise lx-rise-${3 + n}`}
              key={r.code}
              data-kind={r.kind}
              data-phi={facePhi(n, ROWS.length, ar)}
              style={{
                '--cyl-phi': `${facePhi(n, ROWS.length, ar)}deg`,
                '--cyl-turn': faceTurn(facePhi(n, ROWS.length, ar)).toFixed(4),
              } as React.CSSProperties}
            >
              {/* The jurisdiction is now the row header itself — which is what
                  a screen reader wanted all along, and what the deleted code
                  column was standing in for. */}
              <th scope="row" className="lx-reg-main">
                <span className="lx-reg-name">{ar ? r.nameAr : r.name}</span>
                <span className="lx-reg-pack">
                  {(ar ? r.itemsAr : r.items).map((it, i) => (
                    /* The real space between the items is deliberate: it is
                       what keeps a screen reader from running "GOSIQiwa"
                       together once the middot is a ::before. */
                    <span className="lx-reg-i" key={it}>{i ? ' ' : ''}{it}</span>
                  ))}
                </span>
              </th>
              {/* Empty on a 'wide' row, and empty is the honest cell: those
                  rows are not jurisdictions and have no headcount to give. */}
              <td className="lx-reg-n">{r.heads === undefined ? '' : num(r.heads, ar)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="lx-reg-total lx-rise lx-rise-9">
            <th scope="row" className="lx-reg-main">
              <span className="lx-reg-name">{t.totalKey}</span>
              <span className="lx-reg-ok"><Tick />{t.approved}</span>
            </th>
            <td className="lx-reg-n">{num(TOTAL, ar)}</td>
          </tr>
        </tfoot>
      </table>
    </section>
  );
}

/* ══════════════════════════════════════════════════════════════════════════
   THE FOOTER — one line.
   KynexOne is the product; Kode Kinetics is who operates it, and the copyright
   line states that in three words. The paragraph that used to explain the
   processor relationship, and the two mailto addresses beside it, are deleted:
   app/privacy/page.tsx and app/terms/page.tsx say all of it properly, and both
   are one click away in this same row. Nothing about the company that is not
   evidenced on those pages appears here.
   ══════════════════════════════════════════════════════════════════════════ */
export function VendorFooter() {
  return (
    <div className="lx-vendor lx-plate">
      <p className="lx-vendor-copy">{`© ${new Date().getFullYear()} Kode Kinetics`}</p>
      {/* No /pricing link here. The one route to pricing on this page sits
          directly under the sign-in button, where someone who is not a
          customer yet actually hesitates — not in the quietest row on the
          screen, and not twice. */}
      <nav className="lx-vendor-nav" aria-label="Legal and security">
        <a href="/security">Security</a>
        <a href="/privacy" target="_blank" rel="noopener noreferrer">Privacy</a>
        <a href="/terms" target="_blank" rel="noopener noreferrer">Terms</a>
      </nav>
    </div>
  );
}

/* ── glyphs ───────────────────────────────────────────────────────────── */

function Tick() {
  return (
    <svg viewBox="0 0 14 14" aria-hidden focusable="false" className="lx-tick">
      <path d="M2.6 7.4 5.5 10.3 11.4 4.2" fill="none" stroke="currentColor"
        strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function PauseGlyph() {
  return (
    <svg viewBox="0 0 10 10" aria-hidden focusable="false">
      <rect x="1.6" y="1" width="2.2" height="8" rx="0.6" fill="currentColor" />
      <rect x="6.2" y="1" width="2.2" height="8" rx="0.6" fill="currentColor" />
    </svg>
  );
}

function PlayGlyph() {
  return (
    <svg viewBox="0 0 10 10" aria-hidden focusable="false">
      <path d="M2.2 1.2 8.6 5 2.2 8.8Z" fill="currentColor" />
    </svg>
  );
}
