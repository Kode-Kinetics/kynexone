import type { Metadata } from 'next';

/* The sign-in surface's own type stack — applied HERE, never in the root layout, so no other
 * route pays for it. The faces themselves are vendored .woff2 files declared in
 * ../fonts/fonts.ts, shared with /reset-password and /accept-invitation. */
import { publicPageFontVariables } from '../fonts/fonts';

import '@/src/styles/login-aurora.css';
import { LoginPage } from '@/src/views/LoginPage';

export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'Sign in — KynexOne',
  description: 'KynexOne — enterprise workforce platform. HR, payroll, attendance and compliance, unified.',
};

export default function Page() {
  return (
    <div className={publicPageFontVariables}>
      <LoginPage />
    </div>
  );
}
