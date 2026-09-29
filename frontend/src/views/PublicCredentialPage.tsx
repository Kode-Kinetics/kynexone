'use client';

import { useLayoutEffect, useRef, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import { AlertCircle, CheckCircle2, Eye, EyeOff, KeyRound } from 'lucide-react';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';
import { Brief, VendorFooter } from '../components/LoginMarketing';
import { consumeFragmentToken, requireWorkspace, resolveWorkspaceAlias } from '../lib/publicAuth';

type CredentialKind = 'reset' | 'invitation';

export function PublicCredentialPage({ kind }: { kind: CredentialKind }) {
  const searchParams = useSearchParams();
  const [workspace, setWorkspace] = useState('');
  const [token, setToken] = useState('');
  const [fragmentRead, setFragmentRead] = useState(false);
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [completed, setCompleted] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const [error, setError] = useState('');
  const [completedWorkspace, setCompletedWorkspace] = useState('');
  const fragmentConsumed = useRef(false);
  const requestInFlight = useRef(false);

  useLayoutEffect(() => {
    setWorkspace(resolveWorkspaceAlias(searchParams));
  }, [searchParams]);

  useLayoutEffect(() => {
    // React Strict Mode re-runs effects in development. The ref prevents the
    // second pass from overwriting the captured secret after the fragment was
    // scrubbed by the first pass.
    if (fragmentConsumed.current) return;
    fragmentConsumed.current = true;
    const captured = consumeFragmentToken(window.location, window.history);
    setToken(captured);
    setFragmentRead(true);
  }, []);

  const title = kind === 'reset' ? 'Set a new password' : 'Accept your invitation';
  const success = kind === 'reset'
    ? 'Password updated. Sign in with your new password.'
    : 'Password set. Sign in to continue.';
  const loginHref = completedWorkspace
    ? `/login?workspace=${encodeURIComponent(completedWorkspace)}`
    : '/login';

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (requestInFlight.current || uncertain || completed) return;
    setError('');

    if (!fragmentRead || !token) {
      setError(`This ${kind === 'reset' ? 'reset' : 'invitation'} link is missing its secure token. Request a new link.`);
      return;
    }
    let normalizedWorkspace: string;
    try {
      normalizedWorkspace = requireWorkspace(workspace);
    } catch {
      setError('Workspace is required.');
      return;
    }
    // Preserve the authoritative workspace even when the one-use request has an
    // ambiguous outcome; the only safe next step is the canonical login surface.
    setCompletedWorkspace(normalizedWorkspace);
    if (password.length < 10) {
      setError('Password must be at least 10 characters.');
      return;
    }
    if (password !== confirmPassword) {
      setError('Passwords do not match.');
      return;
    }

    requestInFlight.current = true;
    setBusy(true);
    try {
      if (kind === 'reset') await authApi.resetPassword(token, password, normalizedWorkspace);
      else await authApi.acceptInvitation(token, password, normalizedWorkspace);
      setCompleted(true);
      setToken('');
    } catch (requestError: any) {
      if (!requestError?.response
        || requestError.code === 'ECONNABORTED'
        || requestError.response.status === 408
        || requestError.response.status >= 500) {
        // A one-use credential may have committed even when its HTTP response
        // was lost. Lock the form: automatically or manually replaying it could
        // turn a successful operation into a misleading token-reuse failure.
        setUncertain(true);
        setError(
          'We could not confirm the result. It may have succeeded. Do not submit again; return to sign in, or request a new link if sign-in fails.',
        );
      } else {
        setError(
          requestError.response?.data?.message
            ?? `This ${kind === 'reset' ? 'reset' : 'invitation'} link is invalid or expired.`,
        );
      }
    } finally {
      requestInFlight.current = false;
      setBusy(false);
    }
  };

  return (
    <div className="tenant-login-shell lx-shell">
      <div className="lx-field-static" aria-hidden="true" />
      <div className="lx-page">
        <header className="lx-rail-top">
          <div className="lx-lockup">
            <div className="lx-lockup-mark"><Logo size="xl" collapsed theme="dark" /></div>
            <div className="lx-lockup-type">
              <span className="lx-wordmark">Kynex<em>One</em></span>
              <span className="lx-descriptor">Payroll, HR and compliance</span>
            </div>
          </div>
        </header>

        <main className="lx-stage">
          <div className="lx-slot">
            <div className="lx-pane">
              <div className="lx-card">
                <form onSubmit={submit} noValidate className="lx-form">
                  <div className="lx-head-block" aria-live="polite">
                    <p className="lx-kicker"><KeyRound aria-hidden />Account security</p>
                    <h1 className="lx-title">{title}</h1>
                    <p className="lx-sub">Use the workspace from your secure link and choose a password only you know.</p>
                  </div>

                  {!completed && (
                    <>
                      <CredentialField label="Workspace" htmlFor="credential-workspace">
                        <input
                          id="credential-workspace"
                          className="lx-in lx-in-mono"
                          value={workspace}
                          onChange={(event) => setWorkspace(event.target.value)}
                          placeholder="your-workspace"
                          autoComplete="organization"
                          required
                        />
                      </CredentialField>
                      <CredentialField label="New password" htmlFor="credential-password" hint="Minimum 10 characters">
                        <span className="lx-inwrap">
                          <input
                            id="credential-password"
                            className="lx-in lx-in-pw"
                            type={showPassword ? 'text' : 'password'}
                            value={password}
                            onChange={(event) => setPassword(event.target.value)}
                            autoComplete="new-password"
                            required
                          />
                          <button
                            type="button"
                            className="lx-reveal"
                            onClick={() => setShowPassword((shown) => !shown)}
                            aria-label={showPassword ? 'Hide password' : 'Show password'}
                            aria-pressed={showPassword}
                          >
                            {showPassword ? <EyeOff /> : <Eye />}
                          </button>
                        </span>
                      </CredentialField>
                      <CredentialField label="Confirm password" htmlFor="credential-confirm">
                        <input
                          id="credential-confirm"
                          className="lx-in"
                          type="password"
                          value={confirmPassword}
                          onChange={(event) => setConfirmPassword(event.target.value)}
                          autoComplete="new-password"
                          required
                        />
                      </CredentialField>
                    </>
                  )}

                  {error && (
                    <div className="lx-fault" role="alert">
                      <AlertCircle aria-hidden /><p>{error}</p>
                    </div>
                  )}
                  {completed && (
                    <div className="lx-ok" role="status">
                      <CheckCircle2 aria-hidden /><p>{success}</p>
                    </div>
                  )}

                  {completed || uncertain ? (
                    <a className="lx-submit" href={loginHref}>Return to sign in</a>
                  ) : (
                    <button type="submit" disabled={busy || !fragmentRead} aria-busy={busy} className="lx-submit">
                      {busy ? <span className="lx-spin" aria-hidden /> : kind === 'reset' ? 'Update password' : 'Set password'}
                    </button>
                  )}
                </form>
              </div>
            </div>
          </div>
          <Brief />
        </main>
        <footer className="lx-rail-bottom"><VendorFooter /></footer>
      </div>
    </div>
  );
}

function CredentialField({ label, htmlFor, hint, children }: {
  label: string;
  htmlFor: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="lx-field">
      <div className="lx-legend"><label htmlFor={htmlFor}>{label}</label></div>
      {children}
      {hint && <p className="lx-hint">{hint}</p>}
    </div>
  );
}
