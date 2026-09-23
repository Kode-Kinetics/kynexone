import { Suspense } from 'react';
import type { Metadata } from 'next';
import { IBM_Plex_Mono, IBM_Plex_Sans_Arabic, Instrument_Serif, Manrope } from 'next/font/google';
import '@/src/styles/login-aurora.css';
import { PublicCredentialPage } from '@/src/views/PublicCredentialPage';

const manrope = Manrope({ subsets: ['latin'], weight: ['300', '400', '500', '600', '700'], variable: '--lx-sans' });
const instrument = Instrument_Serif({ subsets: ['latin'], weight: ['400'], style: ['italic'], variable: '--lx-serif' });
const plexArabic = IBM_Plex_Sans_Arabic({ subsets: ['arabic'], weight: ['400', '500', '600'], variable: '--lx-ar' });
const plexMono = IBM_Plex_Mono({ subsets: ['latin'], weight: ['400', '500'], variable: '--lx-mono' });

export const dynamic = 'force-dynamic';
export const metadata: Metadata = { title: 'Accept invitation — KynexOne' };

export default function Page() {
  return (
    <div className={`${manrope.variable} ${instrument.variable} ${plexMono.variable} ${plexArabic.variable}`}>
      <Suspense fallback={null}><PublicCredentialPage kind="invitation" /></Suspense>
    </div>
  );
}
