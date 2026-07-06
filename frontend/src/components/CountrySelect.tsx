'use client';

import { useEffect, useState } from 'react';
import { referenceApi, type IsoCountry } from '../api/reference';

// Module-level cache — the ISO list is static, fetch once per session.
let cache: IsoCountry[] | null = null;
let inflight: Promise<IsoCountry[]> | null = null;

function loadCountries(): Promise<IsoCountry[]> {
  if (cache) return Promise.resolve(cache);
  if (!inflight) inflight = referenceApi.countries().then((c) => { cache = c; return c; }).catch(() => []);
  return inflight;
}

/**
 * ISO 3166-1 alpha-2 country picker. Stores the canonical 2-letter code (e.g. "SA"),
 * replacing free-text country fields so data is standards-clean.
 */
export function CountrySelect({
  value, onChange, className, ariaLabel = 'Country', allowEmpty = true, disabled = false,
}: {
  value: string;
  onChange: (code: string) => void;
  className?: string;
  ariaLabel?: string;
  allowEmpty?: boolean;
  disabled?: boolean;
}) {
  const [countries, setCountries] = useState<IsoCountry[]>(cache ?? []);

  useEffect(() => { loadCountries().then(setCountries); }, []);

  // Surface a legacy/non-ISO value (e.g. "KSA") so it stays visible until corrected.
  const isKnown = countries.some((c) => c.code === value);

  return (
    <select aria-label={ariaLabel} disabled={disabled} value={value} onChange={(e) => onChange(e.target.value)} className={className ?? 'select w-full'}>
      {allowEmpty && <option value="">— Select country —</option>}
      {!isKnown && value && <option value={value}>{value} (non-standard)</option>}
      {countries.map((c) => <option key={c.code} value={c.code}>{c.name} ({c.code})</option>)}
    </select>
  );
}
