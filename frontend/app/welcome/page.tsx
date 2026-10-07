import { Suspense } from 'react';
import type { Metadata } from 'next';
// Same vendored type stack as /login — see ../fonts/fonts.ts.
import { publicPageFontVariables } from '../fonts/fonts';
import '@/src/styles/login-aurora.css';
import { WELCOME_BOOT } from '@/src/lib/welcomeHandoff';
import { WelcomePage } from '@/src/views/WelcomePage';

export const dynamic = 'force-dynamic';
export const metadata: Metadata = {
  title: 'Welcome — KynexOne',
  // The address can carry a sign-in code in its fragment; keep it out of every index and Referer.
  robots: { index: false, follow: false },
  referrer: 'no-referrer',
};

export default function Page() {
  return (
    <div className={publicPageFontVariables}>
      {/* Before any app script runs: move the slip's #e=…&c=… out of the address bar into memory. */}
      <script dangerouslySetInnerHTML={{ __html: WELCOME_BOOT }} />
      <Suspense fallback={null}><WelcomePage /></Suspense>
    </div>
  );
}
