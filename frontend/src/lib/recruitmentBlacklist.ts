import { apiErrorReason } from '../api/client';

/**
 * Blacklisting a candidate, as the Recruitment screens present it. The rules live on the API (POST candidates/{id}/blacklist: Admin and
 * HR Manager only, reason of 10 to 1,000 characters, refused at apply, advance, interview and offer); nothing here decides them, it only
 * keeps the screens from offering what the API will refuse. There is no un-blacklist endpoint, so none is offered.
 */
export const BLACKLIST_REASON_MIN = 10;
export const BLACKLIST_REASON_MAX = 1000;

/** The API's role gate for the blacklist action. HR Officer is refused there too. */
export function canBlacklistCandidate(hasRole: (role: string) => boolean): boolean {
  return hasRole('Admin') || hasRole('HR Manager');
}

export function isBlacklisted(candidate: { status?: string | null } | null | undefined): boolean {
  return candidate?.status === 'Blacklisted';
}

/** Why moving an application to the next stage is not offered, or null when it is. The text is an i18n key. */
export function stageMoveBlock(candidate: { status?: string | null } | null | undefined): string | null {
  return isBlacklisted(candidate) ? 'Blacklisted candidates cannot be moved to another stage.' : null;
}

/**
 * The plain sentence for a refused action: the API's own sentence in the reader's language (its Arabic one when it sends one, else the
 * English one run through the translator), otherwise the caller's fallback.
 */
export function refusalReason(err: unknown, fallback: string, t: (key: string) => string, locale: string): string {
  const reason = apiErrorReason(err, fallback, locale);
  return reason === fallback ? fallback : t(reason);
}
