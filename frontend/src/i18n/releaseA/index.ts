import { accessCeiling } from './accessCeiling';
import { deductions } from './deductions';
import { employeeLogin } from './employeeLogin';
import { ess } from './ess';
import { matrix } from './matrix';
import { packageStrings } from './package';
import { renewals } from './renewals';
import { renewalsOffer } from './renewals.offer';
import { shared } from './shared';
import type { ReleaseADict } from './types';

/**
 * Release A strings, one file per slice so slices never edit the same dictionary. translations.ts spreads these
 * once (R0); a slice adds keys to its own file only. Later files win on a duplicate key — keep keys unique.
 */
export const releaseASlices: Record<string, ReleaseADict> = {
  shared, matrix, package: packageStrings, deductions, renewals, renewalsOffer, ess, accessCeiling, employeeLogin,
};

const merge = (lang: 'en' | 'ar') =>
  Object.assign({}, ...Object.values(releaseASlices).map((slice) => slice[lang])) as Record<string, string>;

export const releaseA = { en: merge('en'), ar: merge('ar') };
