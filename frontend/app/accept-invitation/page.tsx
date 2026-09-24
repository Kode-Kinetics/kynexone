import { Suspense } from 'react';
import type { Metadata } from 'next';
// Same vendored type stack as /login — see ../fonts/fonts.ts.
import { publicPageFontVariables } from '../fonts/fonts';
import '@/src/styles/login-aurora.css';
import { PublicCredentialPage } from '@/src/views/PublicCredentialPage';

export const dynamic = 'force-dynamic';
export const metadata: Metadata = { title: 'Accept invitation — KynexOne' };

export default function Page() {
  return (
    <div className={publicPageFontVariables}>
      <Suspense fallback={null}><PublicCredentialPage kind="invitation" /></Suspense>
    </div>
  );
}
