import { SignInSecurityPage } from '@/src/views/SignInSecurityPage';

/**
 * Deliberately NOT behind a PermissionGate: this is the signed-in person's own account, and every
 * endpoint it uses needs only a session. Payroll, finance and access roles must reach it (they are
 * the ones two-step sign-in is mandatory for), and not every such login holds ess.read, so it is
 * not a Self-Service tab either. The dashboard layout already sends a signed-out visitor to /login.
 */
export default function Page() {
  return <SignInSecurityPage />;
}
