/**
 * In-memory hand-off from the sign-in page to /welcome.
 *
 * When someone types their 8-digit welcome code into the Password box and sign-in fails, the sign-in
 * page sends them to /welcome with the email and code already filled in. The code is a credential,
 * so it travels in this module's memory across a client-side navigation: never in a URL (query OR
 * fragment), never in storage. A full page load starts empty, which only means retyping the code.
 *
 * `take` reads AND clears, so the code is held for exactly one hand-off.
 */
export interface WelcomeHandoff {
  email?: string;
  code?: string;
  workspace?: string;
  /** 'login' when the sign-in page diverted a code typed as a password. */
  source?: 'login';
}

let pending: WelcomeHandoff | null = null;

export function setWelcomeHandoff(value: WelcomeHandoff): void {
  pending = { ...value };
}

export function takeWelcomeHandoff(): WelcomeHandoff | null {
  const value = pending;
  pending = null;
  return value;
}

/**
 * The fragment the inline boot script on /welcome captured before any app script ran (see
 * app/welcome/page.tsx). Read once, then deleted from `window`.
 */
export const WELCOME_FRAGMENT_GLOBAL = '__kynexoneWelcomeFragment';

export function takeBootFragment(): string {
  if (typeof window === 'undefined') return '';
  const holder = window as unknown as Record<string, unknown>;
  const value = typeof holder[WELCOME_FRAGMENT_GLOBAL] === 'string' ? holder[WELCOME_FRAGMENT_GLOBAL] as string : '';
  delete holder[WELCOME_FRAGMENT_GLOBAL];
  return value;
}

/**
 * Runs inline in /welcome's HTML, before the app's scripts: stash the fragment in memory and remove
 * it from the address bar, so no script, extension hook or error reporter that loads later ever sees
 * the code in `location`.
 */
export const WELCOME_BOOT = `(function(){try{var h=window.location.hash;if(h&&h.length>1){window.${WELCOME_FRAGMENT_GLOBAL}=h;window.history.replaceState(window.history.state,'',window.location.pathname+window.location.search);}}catch(e){}})();`;
