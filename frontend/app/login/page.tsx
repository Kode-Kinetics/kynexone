import type { Metadata } from 'next';
import {
  Manrope, Instrument_Serif, IBM_Plex_Mono, IBM_Plex_Sans_Arabic,
} from 'next/font/google';

import '@/src/styles/login-aurora.css';
import { LoginPage } from '@/src/views/LoginPage';

/* The sign-in surface's own type stack — loaded HERE, never in the root
 * layout, so no other route pays for it. */
const manrope = Manrope({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700'],
  variable: '--lx-sans',
  display: 'swap',
});
const instrument = Instrument_Serif({
  subsets: ['latin'],
  weight: ['400'],
  style: ['italic'],
  variable: '--lx-serif',
  display: 'swap',
});
/* Arabic is a selling point on this page, so it is set in a face that actually
   shapes it: IBM Plex Sans Arabic joins letters correctly. Letting العربية fall
   back to a Latin face would render it disconnected, which is worse than not
   showing it at all. */
const plexArabic = IBM_Plex_Sans_Arabic({
  subsets: ['arabic'],
  weight: ['400', '500', '600'],
  variable: '--lx-ar',
  display: 'swap',
});
const plexMono = IBM_Plex_Mono({
  subsets: ['latin'],
  weight: ['400', '500'],
  variable: '--lx-mono',
  display: 'swap',
});

export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'Sign in — KynexOne',
  description: 'KynexOne — enterprise workforce platform. HR, payroll, attendance and compliance, unified.',
};

export default function Page() {
  return (
    <div className={`${manrope.variable} ${instrument.variable} ${plexMono.variable} ${plexArabic.variable}`}>
      <LoginPage />
    </div>
  );
}
