/**
 * The privacy notice must read as words. The API sends a notice id ("ess-1") and a hosting-region key ("us-east"); neither is shown raw.
 * `labelKey` is an English sentence-style key for t(); `outsideKingdom` selects the PDPL transfer wording.
 */
export function noticeVersionNumber(raw: string): string | null {
  const m = /(\d+)\s*$/.exec(raw ?? '');
  return m ? m[1] : null;
}

export interface HostingRegion { labelKey: string; outsideKingdom: boolean }

export function hostingRegion(raw: string): HostingRegion {
  const key = (raw ?? '').trim().toLowerCase();
  if (/^us-east/.test(key)) return { labelKey: 'United States (east)', outsideKingdom: true };
  if (/^us-west/.test(key)) return { labelKey: 'United States (west)', outsideKingdom: true };
  if (/^eu-west/.test(key)) return { labelKey: 'Europe (west)', outsideKingdom: true };
  if (/^eu-central/.test(key)) return { labelKey: 'Europe (central)', outsideKingdom: true };
  if (/^(me-sa|sa-|ksa)/.test(key)) return { labelKey: 'Saudi Arabia', outsideKingdom: false };
  return { labelKey: '', outsideKingdom: false };
}
