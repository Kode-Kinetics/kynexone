import localFont from 'next/font/local';

/**
 * Every typeface the application uses, self-hosted.
 *
 * These were declared with the `next/font` Google loader until the Google
 * Fonts fetch — which that loader performs AT BUILD TIME — started failing.
 * When it fails the loader throws `TypeError: Cannot read properties of null
 * (reading '1')` and the build dies. That blocked five separate releases on
 * 2026-09-23, every one of them on a green codebase and none of them caused by
 * a change in this repository. Retries in CI and in the Dockerfile reduced the
 * frequency without removing the dependency, so the .woff2 files now live in
 * this directory and the release path never talks to Google.
 *
 * All six families are SIL Open Font License 1.1 — see ./LICENSE for the
 * copyright notices and the exact files vendored.
 *
 * Only the weights and subsets the call sites actually asked for are here.
 * Adding a weight means adding a file; do not reach back to the Google loader.
 */

/* ── Product shell (app/layout.tsx, every route) ───────────────────────────── */

/* The product typeface. Geist is drawn for dense product UI: a tall x-height and open
 * apertures keep 12-14 px labels legible, and its figures are clear in tables. The variable
 * file carries the full 100-900 axis, which is what the Google loader served when no `weight`
 * was passed. Arabic codepoints fall through to plexArabicFallback, below. */
export const geist = localFont({
  src: './Geist-Variable-latin.woff2',
  weight: '100 900',
  style: 'normal',
  variable: '--font-geist',
  display: 'swap',
});

export const geistMono = localFont({
  src: './GeistMono-Variable-latin.woff2',
  weight: '100 900',
  style: 'normal',
  variable: '--font-geist-mono',
  display: 'swap',
});

/* Geist has no Arabic coverage, so Arabic codepoints fall through to IBM Plex Sans Arabic,
 * whose weight and x-height sit close to Geist; without it an Arabic page fell back to
 * whatever the OS offered. This used to be a plain <link> to fonts.googleapis.com in the
 * document head — the one remaining runtime call to Google — so it is vendored with the rest.
 *
 * preload is off deliberately: it is the second entry in a font stack whose first entry draws
 * every Latin glyph, so on a Latin route the browser never needs it. Preloading it would push
 * ~140 KB onto all ~40 routes to serve codepoints most of them never render. It is fetched the
 * moment an Arabic character actually appears, exactly as the <link> behaved. */
export const plexArabicFallback = localFont({
  src: [
    { path: './IBMPlexSansArabic-400-arabic.woff2', weight: '400', style: 'normal' },
    { path: './IBMPlexSansArabic-500-arabic.woff2', weight: '500', style: 'normal' },
    { path: './IBMPlexSansArabic-600-arabic.woff2', weight: '600', style: 'normal' },
    { path: './IBMPlexSansArabic-700-arabic.woff2', weight: '700', style: 'normal' },
  ],
  variable: '--font-plex-arabic',
  display: 'swap',
  preload: false,
});

/* ── Sign-in surface (app/login, app/reset-password, app/accept-invitation) ──
 *
 * The sign-in surface's own type stack. It is applied by those three route files
 * and never by the root layout, so no other route pays for it. The three pages
 * declared these separately and identically; they share one declaration now so
 * the faces cannot drift apart between the login page and the password pages. */

/* Manrope ships as one variable file, and the five weights the sign-in surface asks for are
 * five @font-face rules over that same file — which is exactly what Google served for
 * `weight: ['300','400','500','600','700']`. Declaring it instead as a single face with the
 * range '300 700' is NOT equivalent and is visibly wrong: with five pinned faces the browser
 * snaps an off-scale request (font-weight: 650, or `bold` on an element whose stack tops out
 * at 700) to the nearest declared weight, while a ranged face interpolates to the exact value.
 * That shifted the stroke weight of the "Update password" button, which is how this was
 * caught. Keep the five. */
export const manrope = localFont({
  src: [
    { path: './Manrope-Variable-latin.woff2', weight: '300', style: 'normal' },
    { path: './Manrope-Variable-latin.woff2', weight: '400', style: 'normal' },
    { path: './Manrope-Variable-latin.woff2', weight: '500', style: 'normal' },
    { path: './Manrope-Variable-latin.woff2', weight: '600', style: 'normal' },
    { path: './Manrope-Variable-latin.woff2', weight: '700', style: 'normal' },
  ],
  variable: '--lx-sans',
  display: 'swap',
});

export const instrumentSerif = localFont({
  src: './InstrumentSerif-Italic-400-latin.woff2',
  weight: '400',
  style: 'italic',
  variable: '--lx-serif',
  display: 'swap',
});

/* Arabic is a selling point on this page, so it is set in a face that actually
   shapes it: IBM Plex Sans Arabic joins letters correctly. Letting العربية fall
   back to a Latin face would render it disconnected, which is worse than not
   showing it at all. */
export const plexArabic = localFont({
  src: [
    { path: './IBMPlexSansArabic-400-arabic.woff2', weight: '400', style: 'normal' },
    { path: './IBMPlexSansArabic-500-arabic.woff2', weight: '500', style: 'normal' },
    { path: './IBMPlexSansArabic-600-arabic.woff2', weight: '600', style: 'normal' },
  ],
  variable: '--lx-ar',
  display: 'swap',
});

export const plexMono = localFont({
  src: [
    { path: './IBMPlexMono-400-latin.woff2', weight: '400', style: 'normal' },
    { path: './IBMPlexMono-500-latin.woff2', weight: '500', style: 'normal' },
  ],
  variable: '--lx-mono',
  display: 'swap',
});

/** The four variables every public credential page sets on its wrapper. */
export const publicPageFontVariables = [
  manrope.variable,
  instrumentSerif.variable,
  plexMono.variable,
  plexArabic.variable,
].join(' ');
