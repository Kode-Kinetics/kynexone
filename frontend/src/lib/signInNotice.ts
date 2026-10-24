/**
 * Hand-off from a signed-in screen to the sign-in page: why the person is being asked to sign in
 * again.
 *
 * Turning two-step sign-in on or off ends every session of the account (the server rotates its
 * session stamp), so Sign-in security sends the person to /login. Without a word there, the
 * sign-in page would look like an unexplained sign-out. The reason travels in this module's memory
 * across the client-side navigation, never in a URL; a full page load starts empty, which only
 * means no sentence.
 *
 * One reason has to survive a full page load: the session reaching the company's maximum length
 * ('session-expired'). The API client learns it from a refused token refresh and leaves with
 * `window.location.href = '/login'`. That reason is also kept in sessionStorage (this tab only,
 * still never in a URL). The page that set it does not clear the stored copy, because it is about
 * to be replaced: the sign-in page the router may show first and the freshly loaded one both say
 * it, and the freshly loaded page clears it.
 *
 * `take` reads AND clears, so the sentence is shown once per page.
 */
export type SignInNotice = 'two-step-on' | 'two-step-off' | 'session-expired';

const STORAGE_KEY = 'kynexone.sign-in-notice';
const PERSISTED: ReadonlySet<SignInNotice> = new Set<SignInNotice>(['session-expired']);

let pending: SignInNotice | null = null;
/** True in the page that wrote the stored copy: it is leaving, so the next page clears it. */
let leavingWithStoredNotice = false;

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage;
  } catch {
    return null; // Blocked site data: the sentence is lost, nothing else is.
  }
}

export function setSignInNotice(value: SignInNotice): void {
  pending = value;
  if (!PERSISTED.has(value)) return;
  try {
    const store = storage();
    if (!store) return;
    store.setItem(STORAGE_KEY, value);
    leavingWithStoredNotice = true;
  } catch {
    // Storage full or blocked: the in-memory copy still covers a client-side navigation.
  }
}

export function takeSignInNotice(): SignInNotice | null {
  let value = pending;
  pending = null;
  try {
    const store = storage();
    const stored = store?.getItem(STORAGE_KEY) ?? null;
    if (!value && stored && PERSISTED.has(stored as SignInNotice)) value = stored as SignInNotice;
    if (stored != null && !leavingWithStoredNotice) store?.removeItem(STORAGE_KEY);
  } catch {
    // Unreadable storage only means no stored sentence.
  }
  return value;
}

/**
 * The sign-in notice a refused token refresh calls for, if any: 401 `ERR_SESSION_EXPIRED` means
 * the session reached the company's maximum length. Every other refusal is an ordinary sign-out
 * and says nothing, as before.
 */
export function signInNoticeForRefreshRefusal(err: unknown): SignInNotice | null {
  const response = (err as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status !== 401) return null;
  const data = response.data as { code?: unknown } | null | undefined;
  return data && typeof data === 'object' && data.code === 'ERR_SESSION_EXPIRED' ? 'session-expired' : null;
}
