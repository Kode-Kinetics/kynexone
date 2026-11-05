'use client';

import { useId, useState, type ClipboardEvent, type ReactNode } from 'react';
import dynamic from 'next/dynamic';
import { ExternalLink, LocateFixed, MapPin, Search } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { geoApi, type AddressSuggestion } from '../../api/geo';
import {
  RADIUS_PRESETS, addressQuery, countryCentre, formatCoordinates, googleMapsUrl, isInGcc, looksSwapped, parseLocationInput, roundCoordinate,
} from '../../lib/mapLink';
import { Notice } from './wizardForm';

// Leaflet touches `window`, and most screens never show a map: load it with the first pin panel, in the browser only.
const LocationMap = dynamic(() => import('./LocationMap').then(m => m.LocationMap), {
  ssr: false,
  loading: () => <div className="h-60 rounded-lg border border-slate-200 bg-slate-100 dark:border-white/10 dark:bg-slate-800 sm:h-72" aria-hidden="true" />,
});

type Feedback = { tone: 'success' | 'error'; text: string } | null;

/** What a new location, or a first pin on a location without one, starts with: visible and saved, never implied. */
export const DEFAULT_RADIUS = 150;
export { RADIUS_PRESETS };

/** The part of a location the pin panel reads and writes. Anything not listed is none of its business. */
export interface PinValue {
  latitude?: number | null;
  longitude?: number | null;
  radiusMeters?: number | null;
  address?: string | null;
  city?: string | null;
  countryCode?: string | null;
}
export type PinPatch = Partial<PinValue>;

/**
 * "Pin this location", independent of any form: search an address (on request, never per keystroke), paste a Google
 * Maps link or coordinates (parsed in the browser; short links via GET /api/geo/resolve-map-link), use the device's
 * location, or click / drag on an OpenStreetMap preview that draws the geofence. Every change is reported as a patch
 * through `onChange`; the host owns the state, so the raw inputs it renders itself stay in sync. Outside the GCC is a
 * warning, never a block: a company can have a site anywhere. A failed search never blocks saving.
 */
export function LocationPinCore({
  value, onChange, countryCode, name, readOnly = false, disabled = false, addressMax = 200, protectAddress = false, fillDetails = false,
  address, children, footer,
}: {
  value: PinValue;
  onChange: (patch: PinPatch) => void;
  /** Biases the address search and centres the empty map. */
  countryCode: string;
  /** What the map is of: the location's name, or its row label. */
  name: string;
  /** No search, no pin moves: the person may look but not change. */
  readOnly?: boolean;
  /** Locked while a save is in flight: the controls stay visible but inert. */
  disabled?: boolean;
  /** The longest address the host stores; a chosen place is cut to it. */
  addressMax?: number;
  /** Choosing a place leaves a non-empty address as typed and offers to replace it, instead of overwriting it. */
  protectAddress?: boolean;
  /** Choosing a place also fills the city and country when the host's are empty. Never overwrites. */
  fillDetails?: boolean;
  /** The host's address input, shown under the search feedback. */
  address?: ReactNode;
  /** Shown under the map hint (the host's radius controls). */
  children?: ReactNode;
  /** Shown last (the host's raw coordinate inputs). A function receives `clearFeedback`, for inputs that edit the pin by hand. */
  footer?: ReactNode | ((api: { clearFeedback: () => void }) => ReactNode);
}) {
  const t = useT();
  const { locale } = useLocale();
  const id = useId();
  const locked = readOnly || disabled;
  const [paste, setPaste] = useState('');
  const [busy, setBusy] = useState<'' | 'resolving' | 'locating' | 'searching'>('');
  /** Candidate places for the typed address, until one is chosen or the person keeps their own pin. null = no search shown. */
  const [suggestions, setSuggestions] = useState<{ query: string; items: AddressSuggestion[] } | null>(null);
  const [feedback, setFeedback] = useState<Feedback>(null);
  /** An address the person's own was kept over; shown until they take it or search again. */
  const [offer, setOffer] = useState<string | null>(null);
  /** The device's accuracy for the pin it placed; dropped once the pin moves. */
  const [fix, setFix] = useState<{ key: string; accuracy: number } | null>(null);

  const lat = value.latitude ?? null;
  const lng = value.longitude ?? null;
  const pinned = lat != null && lng != null && Number.isFinite(lat) && Number.isFinite(lng);
  // The radius shown is the radius saved: never a display-only default. Attendance verification skips a location
  // without one, so a pin with no radius says so instead of drawing a geofence that does not exist.
  const radius = value.radiusMeters && value.radiusMeters > 0 ? value.radiusMeters : null;
  const key = pinned ? `${lat},${lng}` : '';
  const accuracy = fix && fix.key === key ? fix.accuracy : null;

  const pin = (latitude: number, longitude: number, text?: string, extra: PinPatch = {}) => {
    // Placing a pin on a location with no radius also sets the default the person then sees selected.
    onChange({ latitude: roundCoordinate(latitude), longitude: roundCoordinate(longitude), ...(radius == null ? { radiusMeters: DEFAULT_RADIUS } : {}), ...extra });
    setFeedback(text ? { tone: 'success', text } : null);
  };

  const search = async (query: string) => {
    setBusy('searching'); setFeedback(null); setSuggestions(null); setOffer(null);
    try {
      const items = await geoApi.searchAddress(query, countryCode || undefined, locale === 'ar' ? 'ar' : 'en');
      setSuggestions({ query, items });
    } catch {
      setFeedback({ tone: 'error', text: t('The address search is not available right now. Paste a Google Maps link, enter coordinates, or click the map.') });
    } finally { setBusy(''); }
  };

  const choose = (item: AddressSuggestion) => {
    const label = item.label.slice(0, addressMax);
    const typed = (value.address ?? '').trim();
    const keepTyped = protectAddress && typed !== '' && typed !== label;
    const extra: PinPatch = {};
    if (!keepTyped) extra.address = label;
    if (fillDetails) {
      if (!(value.city ?? '').trim() && item.city) extra.city = item.city;
      if (!(value.countryCode ?? '').trim() && item.countryCode) extra.countryCode = item.countryCode.toUpperCase();
    }
    pin(item.latitude, item.longitude, keepTyped ? t('Pin placed at the place you chose. Your address was left as you typed it.') : t('Pin placed at the address you chose. Drag it to fine-tune.'), extra);
    setOffer(keepTyped ? label : null);
    setSuggestions(null);
  };

  const place = async (raw: string) => {
    setSuggestions(null); setOffer(null);
    const parsed = parseLocationInput(raw);
    if (parsed.kind === 'coordinates') {
      pin(parsed.latitude, parsed.longitude, parsed.from === 'link' ? t('Pin placed from the link.') : t('Pin placed from the coordinates.'));
      setPaste('');
      return;
    }
    if (parsed.kind === 'unreadable') {
      const query = addressQuery(raw);
      if (query) { await search(query); return; }
      setFeedback({ tone: 'error', text: t('This is not a map link, coordinates or address we can read. Type an address, paste a Google Maps link, or enter coordinates such as 24.7136, 46.6753.') });
      return;
    }
    setBusy('resolving'); setFeedback(null);
    try {
      const found = await geoApi.resolveMapLink(parsed.url);
      pin(found.latitude, found.longitude, t('Pin placed from the link.'));
      setPaste('');
    } catch {
      setFeedback({ tone: 'error', text: t('This link could not be read. Open it, then copy the full address from the browser bar, or paste the coordinates.') });
    } finally { setBusy(''); }
  };

  const onPaste = (event: ClipboardEvent<HTMLInputElement>) => {
    const text = event.clipboardData.getData('text');
    if (!text.trim()) return;
    event.preventDefault();
    setPaste(text.trim());
    void place(text);
  };

  const locate = () => {
    if (typeof navigator === 'undefined' || !navigator.geolocation) {
      setFeedback({ tone: 'error', text: t('This browser cannot share its location. Paste a link or the coordinates instead.') });
      return;
    }
    setBusy('locating'); setFeedback(null); setOffer(null);
    navigator.geolocation.getCurrentPosition(position => {
      setBusy('');
      const latitude = roundCoordinate(position.coords.latitude);
      const longitude = roundCoordinate(position.coords.longitude);
      const meters = Math.max(1, Math.round(position.coords.accuracy));
      setFix({ key: `${latitude},${longitude}`, accuracy: meters });
      pin(latitude, longitude, t('Pin placed at your current location, accurate to ±{accuracy} m.', { accuracy: meters }));
    }, error => {
      setBusy('');
      setFeedback({ tone: 'error', text: error.code === error.PERMISSION_DENIED
        ? t('Location access was refused. Allow it for this site in the browser, or paste a link instead.')
        : t('Your location could not be found. Try again outdoors or near a window, or paste a link.') });
    }, { enableHighAccuracy: true, timeout: 15_000, maximumAge: 0 });
  };

  const headingId = `${id}-pin`;
  return (
    <section aria-labelledby={headingId} className="mt-3 space-y-3 rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="location-pin">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <h4 id={headingId} className="flex items-center gap-1.5 text-sm font-semibold text-slate-900 dark:text-white">
          <MapPin className="h-4 w-4 text-sapphire dark:text-cyanAccent" aria-hidden="true" />{t('Pin this location')}
        </h4>
        <p className="text-xs text-slate-600 dark:text-slate-400" data-testid="pin-status">
          {pinned
            ? <>{t('Pinned')}{' · '}<bdi dir="ltr" className="font-medium text-slate-800 dark:text-slate-200">{formatCoordinates(lat!, lng!)}</bdi>{' · '}{radius != null ? <bdi>{t('{meters} m', { meters: radius })}</bdi> : <span className="font-medium text-amber-800 dark:text-amber-300">{t('No geofence radius set')}</span>}</>
            : t('Not pinned yet.')}
        </p>
      </div>

      {!readOnly && (
        <div className="space-y-2">
          <label htmlFor={`${id}-paste`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Search an address, or paste a Google Maps link or coordinates')}</label>
          <p id={`${id}-paste-help`} className="text-xs leading-5 text-slate-500 dark:text-slate-400">{t('For example Olaya Street, Riyadh, or 24.7136, 46.6753, or the link from Share in Google Maps.')}</p>
          <div className="flex flex-wrap gap-2">
            <input id={`${id}-paste`} className="input min-w-0 flex-1 basis-56" dir="auto" inputMode="search" autoComplete="off" spellCheck={false} disabled={disabled}
              aria-describedby={`${id}-paste-help`} value={paste} onChange={e => { setPaste(e.target.value); setSuggestions(null); }} onPaste={onPaste}
              onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); if (paste.trim() && !busy) void place(paste); } }} data-testid="pin-paste" />
            <button type="button" className="btn-secondary" disabled={!paste.trim() || !!busy || disabled} onClick={() => void place(paste)}>
              <Search className="h-4 w-4" aria-hidden="true" />{busy === 'resolving' ? t('Reading the link…') : busy === 'searching' ? t('Searching…') : t('Find place')}
            </button>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button type="button" className="btn-secondary" disabled={!!busy || disabled} onClick={locate} data-testid="pin-locate">
              <LocateFixed className="h-4 w-4" aria-hidden="true" />{busy === 'locating' ? t('Finding your location…') : t('Use my current location')}
            </button>
            {pinned && (
              <a href={googleMapsUrl(lat!, lng!)} target="_blank" rel="noopener noreferrer" className="btn-secondary" data-testid="pin-google">
                {t('Check in Google Maps')}<ExternalLink className="h-3.5 w-3.5 rtl:-scale-x-100" aria-hidden="true" /><span className="sr-only"> {t('(opens in a new tab)')}</span>
              </a>
            )}
          </div>
        </div>
      )}
      <div aria-live="polite" data-testid="pin-feedback" className="space-y-2">
        {feedback && <p className={`text-sm ${feedback.tone === 'error' ? 'font-medium text-rose-700 dark:text-rose-300' : 'text-emerald-800 dark:text-emerald-300'}`}>{feedback.text}</p>}
        {offer && !locked && (
          <button type="button" className="btn-secondary" onClick={() => { onChange({ address: offer }); setOffer(null); }}>{t('Replace the address with the place I chose')}</button>
        )}
      </div>

      {address}

      {suggestions && (
        <div className="space-y-2 rounded-lg border border-sapphire/30 bg-sapphire/5 p-3 dark:border-cyanAccent/30 dark:bg-cyanAccent/5" data-testid="pin-suggestions">
          {suggestions.items.length > 0 ? (
            <>
              <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{t('Is it one of these? Choose the right place, or keep what you typed.')}</p>
              <ul className="space-y-1.5">
                {suggestions.items.map((item, index) => (
                  <li key={`${item.latitude},${item.longitude},${index}`} className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-white px-3 py-2 text-sm dark:bg-white/5">
                    <span className="min-w-0 flex-1 text-slate-800 dark:text-slate-200"><bdi>{item.label}</bdi></span>
                    <button type="button" className="btn-secondary" disabled={locked} onClick={() => choose(item)} data-testid="pin-suggestion">{t('Use this place')}</button>
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <p className="text-sm text-slate-700 dark:text-slate-300">{t('No match for that address. Try fewer words, add the city, paste a Google Maps link, or enter coordinates.')}</p>
          )}
          {(suggestions.items.length > 0 || !protectAddress) && (
            <button type="button" className="btn-secondary" disabled={locked} data-testid="pin-keep" onClick={() => {
              if (!protectAddress) onChange({ address: suggestions.query.slice(0, addressMax) });
              setSuggestions(null);
              setFeedback({ tone: 'success', text: t('Kept your address as typed. The pin was not moved.') });
            }}>{t('Keep what I typed')}</button>
          )}
        </div>
      )}

      <LocationMap latitude={pinned ? lat : null} longitude={pinned ? lng : null} radius={radius} fallback={countryCentre(countryCode)} readOnly={locked}
        label={t('Map of {name}', { name })} onMove={(latitude, longitude) => { setOffer(null); pin(latitude, longitude); }} />
      <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">
        {pinned ? t('Drag the pin, or click the map, to fine-tune it.') : t('Paste a link or coordinates, use your current location, or click the map where the site is.')}
      </p>

      {children}

      {pinned && !isInGcc(lat!, lng!) && (
        <Notice tone="warning" role="status">
          <p>{t('These coordinates are outside the GCC. Check the pin before you continue.')}</p>
          {looksSwapped(lat!, lng!) && !locked && (
            <button type="button" className="btn-secondary mt-2" onClick={() => pin(lng!, lat!, t('Latitude and longitude swapped.'))}>{t('Swap latitude and longitude')}</button>
          )}
        </Notice>
      )}
      {pinned && radius == null && (
        <div data-testid="pin-no-radius"><Notice tone="warning" role="status">{t('No geofence radius set. Check-ins at this location are not verified until you choose a radius above.')}</Notice></div>
      )}
      {accuracy != null && radius != null && accuracy > radius && (
        <Notice tone="warning" role="status">{t('Your device placed you within ±{accuracy} m, wider than the {radius} m geofence. Drag the pin to the exact spot, or choose a larger radius.', { accuracy, radius })}</Notice>
      )}

      {typeof footer === 'function' ? footer({ clearFeedback: () => setFeedback(null) }) : footer}
    </section>
  );
}
