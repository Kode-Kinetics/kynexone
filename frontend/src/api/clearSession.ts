import { LEGACY_LOCALE_KEY, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../i18n/localeBoot';
import { clearStepUpToken } from './stepUp';
import { clearFlashResult } from '../lib/flashResult';

// A dead session wipes everything the signed-in user left behind, but not the
// display language: without it the login page would come back English/LTR.
const LOCALE_KEYS_KEPT_ON_LOGOUT = [LOCALE_CHOICE_KEY, LEGACY_LOCALE_KEY, TENANT_LOCALE_KEY];

export function clearSessionKeepingLocale(
  storage: Pick<Storage, 'getItem' | 'setItem' | 'clear'> = localStorage,
  session: Pick<Storage, 'removeItem'> | null = typeof sessionStorage === 'undefined' ? null : sessionStorage,
) {
  clearStepUpToken();
  // A result left for the next screen belongs to the person who signed out, not to whoever signs in next.
  if (session) clearFlashResult(session);
  const kept = LOCALE_KEYS_KEPT_ON_LOGOUT.map((k) => [k, storage.getItem(k)] as const);
  storage.clear();
  for (const [k, v] of kept) if (v != null) storage.setItem(k, v);
}
