/**
 * In-memory hand-off from a signed-in screen to the sign-in page: why the person is being asked to
 * sign in again.
 *
 * Turning two-step sign-in on or off ends every session of the account (the server rotates its
 * session stamp), so Sign-in security sends the person to /login. Without a word there, the
 * sign-in page would look like an unexplained sign-out. The reason travels in this module's memory
 * across the client-side navigation, never in a URL; a full page load starts empty, which only
 * means no sentence.
 *
 * `take` reads AND clears, so the sentence is shown exactly once.
 */
export type SignInNotice = 'two-step-on' | 'two-step-off';

let pending: SignInNotice | null = null;

export function setSignInNotice(value: SignInNotice): void {
  pending = value;
}

export function takeSignInNotice(): SignInNotice | null {
  const value = pending;
  pending = null;
  return value;
}
