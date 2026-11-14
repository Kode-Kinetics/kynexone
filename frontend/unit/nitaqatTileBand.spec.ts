import { test, expect } from '@playwright/test';
import { nitaqatTileBand } from '../src/lib/nitaqatTile';
import type { NitaqatStandingResponse } from '../src/api/nitaqat';
import { translate } from '../src/i18n/translations';

// The home tile and the Saudization screen must agree: a band is named only when the screen's own live standing
// says its thresholds are verified. Otherwise the tile shows the percentage with no band name.
const live = (over: Record<string, unknown> = {}): NitaqatStandingResponse => ({
  ok: true, refusal: null,
  standing: { band: 'Platinum', thresholdsVerified: true, ...over } as unknown as NitaqatStandingResponse['standing'],
});

test.describe('home Saudization tile band', () => {
  test('a snapshot band with unverified thresholds is not named', () => {
    expect(nitaqatTileBand('Platinum', live({ thresholdsVerified: false }))).toEqual({ state: 'unverified' });
  });

  test('a refusal from the live standing (thresholds not loaded) is not a band', () => {
    expect(nitaqatTileBand('Platinum', { ok: false, standing: null, refusal: { reason: 'nitaqat_thresholds_not_published', message: '', remedy: '' } } as NitaqatStandingResponse)).toEqual({ state: 'unverified' });
  });

  test('a failed read of the live standing never names a band', () => {
    expect(nitaqatTileBand('Platinum', 'failed')).toEqual({ state: 'unverified' });
  });

  test('verified thresholds name the live band, the one the screen shows', () => {
    expect(nitaqatTileBand('Platinum', live({ band: 'HighGreen' }))).toEqual({ state: 'band', band: 'HighGreen' });
  });

  test('a server that does not say is treated as verified, like the screen', () => {
    expect(nitaqatTileBand('Platinum', live({ thresholdsVerified: undefined }))).toEqual({ state: 'band', band: 'Platinum' });
  });

  test('no snapshot at all is no band, and an unanswered read is pending, not a band', () => {
    expect(nitaqatTileBand(null, live())).toEqual({ state: 'none' });
    expect(nitaqatTileBand('Platinum', null)).toEqual({ state: 'pending' });
  });

  test('the unverified pill has an Arabic text', () => {
    for (const key of ['Band not verified', 'No band is shown: the MHRSD thresholds for this activity are not loaded or not verified.']) {
      expect(translate('ar', key), key).not.toBe(key);
    }
  });
});
