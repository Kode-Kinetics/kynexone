import type { NitaqatStandingResponse } from '../api/nitaqat';

/**
 * What the home Saudization tile says about the Nitaqat band. The tile used to print the band of the latest stored
 * snapshot, while the Saudization screen withholds the band unless the MHRSD thresholds behind it are verified, so the
 * two could disagree ("Platinum" on the tile, "no band is shown" on the screen). Both now read the live standing:
 * a band is named only when that standing says its thresholds are verified (a server that does not say counts as
 * verified, as on the screen). Anything else shows the percentage with no band name.
 */
export type NitaqatTileBand =
  | { state: 'none' }
  | { state: 'pending' }
  | { state: 'unverified' }
  | { state: 'band'; band: string };

export function nitaqatTileBand(
  snapshotBand: string | null | undefined,
  live: NitaqatStandingResponse | 'failed' | null,
): NitaqatTileBand {
  if (!snapshotBand) return { state: 'none' };
  if (live === null) return { state: 'pending' };
  if (live === 'failed' || !live.ok || !live.standing) return { state: 'unverified' };
  if (live.standing.thresholdsVerified === false) return { state: 'unverified' };
  return { state: 'band', band: live.standing.band || snapshotBand };
}
