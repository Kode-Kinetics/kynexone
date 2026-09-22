import type { Metadata, Viewport } from 'next';
import { Geist, Geist_Mono } from 'next/font/google';
import { Providers } from '@/src/components/Providers';

import '@/src/styles/index.css';

/* Archivo and IBM Plex Mono used to be declared here as --font-kx-sans /
 * --font-kx-mono. They are gone, and nothing lost a face.
 *
 * They were referenced in exactly one stylesheet — login-aurora.css — and
 * always in SECOND position: `var(--lx-sans), var(--font-kx-sans), Inter, …`.
 * --lx-sans and --lx-mono are set by app/login/page.tsx on the wrapper that
 * contains every element those rules match, so the fallback slot was never
 * reached. Declaring them in the ROOT layout made all ~40 routes download and
 * apply five extra font weights to serve a branch of a font stack that only
 * /login can enter and that /login never takes. IBM Plex Mono was also being
 * fetched twice on /login — once here, once as --lx-mono.
 *
 * The comment in app/login/page.tsx states the rule this restores: the sign-in
 * surface's type stack is loaded on the login route, "never in the root
 * layout, so no other route pays for it".
 */

/* The product typeface. Geist is drawn for dense product UI: a tall x-height and open
 * apertures keep 12-14 px labels legible, and its figures are clear in tables. Loaded with
 * next/font, so it is self-hosted with size-adjusted fallbacks (no layout shift, no request
 * to Google at runtime). Arabic codepoints fall through to IBM Plex Sans Arabic, below. */
const geist = Geist({ subsets: ['latin'], variable: '--font-geist', display: 'swap' });
const geistMono = Geist_Mono({ subsets: ['latin'], variable: '--font-geist-mono', display: 'swap' });

export const metadata: Metadata = {
  title: 'KynexOne — One Platform for Every Workforce Operation',
  description: 'HR, payroll, recruitment, attendance and compliance — unified.',
};

export const viewport: Viewport = {
  themeColor: '#0B1020',
  // Lets content (and the glass bottom nav) extend under the iPhone home indicator; without
  // it env(safe-area-inset-bottom) is always 0.
  viewportFit: 'cover',
};

/**
 * Apply the stored locale's direction BEFORE first paint.
 *
 * LocaleProvider sets document.documentElement.dir in an effect, which only runs after
 * hydration and only inside AppLayout. That left two holes: every Arabic page rendered
 * one LTR frame before flipping, and routes outside the tenant shell (/login, /platform)
 * never flipped at all — so an Arabic user's sign-in screen was always left-to-right.
 * Reading the same localStorage key the provider owns, in a blocking script, closes both.
 * Keep the rtl map below in step with LOCALE_METADATA in src/i18n/translations.ts — today
 * `ar` is the only right-to-left locale there.
 */
const LOCALE_BOOT = `(function(){try{
var l=localStorage.getItem('kynexone-locale')||'en';
var d={ar:'rtl'}[l]||'ltr';
document.documentElement.lang=l;document.documentElement.dir=d;
}catch(e){}})();`;

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    // suppressHydrationWarning: LOCALE_BOOT rewrites lang/dir before React hydrates.
    <html lang="en" dir="ltr" suppressHydrationWarning className={`${geist.variable} ${geistMono.variable}`}>
      <head>
        <script dangerouslySetInnerHTML={{ __html: LOCALE_BOOT }} />
        <link rel="preconnect" href="https://fonts.googleapis.com" />
        <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="" />
        {/*
          Geist has no Arabic coverage, so Arabic codepoints fall through to IBM Plex Sans
          Arabic, whose weight and x-height sit close to Geist; without it an Arabic page
          fell back to whatever the OS offered.
        */}
        <link
          href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans+Arabic:wght@400;500;600;700&display=swap"
          rel="stylesheet"
        />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
