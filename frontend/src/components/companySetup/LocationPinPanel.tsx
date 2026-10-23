'use client';

import { useId, useRef, useState, type ClipboardEvent } from 'react';
import dynamic from 'next/dynamic';
import { ExternalLink, LocateFixed, MapPin } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { geoApi } from '../../api/geo';
import type { CompanySetupLocation } from '../../api/companySetup';
import {
  RADIUS_PRESETS, countryCentre, formatCoordinates, googleMapsUrl, isInGcc, looksSwapped, parseLocationInput, roundCoordinate,
} from '../../lib/mapLink';
import { Notice, WizardField, useWizardForm } from './wizardForm';

// Leaflet touches `window`, and most steps never show a map: load it with the first location panel.
const LocationMap = dynamic(() => import('./LocationMap').then(m => m.LocationMap), {
  ssr: false,
  loading: () => <div className="h-60 rounded-lg border border-slate-200 bg-slate-100 dark:border-white/10 dark:bg-slate-800 sm:h-72" aria-hidden="true" />,
});

type Feedback = { tone: 'success' | 'error'; text: string } | null;

/** What a new location, or a first pin on a location without one, starts with: visible and saved, never implied. */
const DEFAULT_RADIUS = 150;

/**
 * "Pin this location", replacing bare latitude / longitude / radius inputs: paste a Google Maps link or coordinates
 * (parsed in the browser; short links via GET /api/geo/resolve-map-link), use the device's location, or click / drag
 * on an OpenStreetMap preview that draws the geofence. The raw numbers stay editable under "Coordinates". Outside
 * the GCC is a warning, never a block: a company can have a site anywhere.
 */
export function LocationPinPanel({ path, location, onChange, countryCode, name }: {
  /** Field path of this location, e.g. branches.0.locations.1. */
  path: string;
  location: CompanySetupLocation;
  onChange: (patch: Partial<CompanySetupLocation>) => void;
  countryCode: string;
  /** What the map is of: the location's name, or its row label. */
  name: string;
}) {
  const t = useT();
  const { readOnly, errorFor } = useWizardForm();
  const id = useId();
  const [paste, setPaste] = useState('');
  const [busy, setBusy] = useState<'' | 'resolving' | 'locating'>('');
  const [feedback, setFeedback] = useState<Feedback>(null);
  /** The device's accuracy for the pin it placed; dropped once the pin moves. */
  const [fix, setFix] = useState<{ key: string; accuracy: number } | null>(null);
  const radiusInput = useRef<HTMLInputElement>(null);

  const lat = location.latitude ?? null;
  const lng = location.longitude ?? null;
  const pinned = lat != null && lng != null && Number.isFinite(lat) && Number.isFinite(lng);
  // The radius shown is the radius saved: never a display-only default. Attendance verification skips a location
  // without one, so a pin with no radius says so instead of drawing a geofence that does not exist.
  const radius = location.radiusMeters && location.radiusMeters > 0 ? location.radiusMeters : null;
  const custom = radius != null && !RADIUS_PRESETS.includes(radius as (typeof RADIUS_PRESETS)[number]);
  const key = pinned ? `${lat},${lng}` : '';
  const accuracy = fix && fix.key === key ? fix.accuracy : null;
  const coordinateError = errorFor(`${path}.latitude`) || errorFor(`${path}.longitude`);

  const pin = (latitude: number, longitude: number, text?: string) => {
    // Placing a pin on a location with no radius also sets the default the person then sees selected.
    onChange({ latitude: roundCoordinate(latitude), longitude: roundCoordinate(longitude), ...(radius == null ? { radiusMeters: DEFAULT_RADIUS } : {}) });
    setFeedback(text ? { tone: 'success', text } : null);
  };

  const place = async (raw: string) => {
    const parsed = parseLocationInput(raw);
    if (parsed.kind === 'coordinates') {
      pin(parsed.latitude, parsed.longitude, parsed.from === 'link' ? t('Pin placed from the link.') : t('Pin placed from the coordinates.'));
      setPaste('');
      return;
    }
    if (parsed.kind === 'unreadable') {
      setFeedback({ tone: 'error', text: t('This is not a map link or coordinates we can read. Paste a Google Maps link, or coordinates such as 24.7136, 46.6753.') });
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
    setBusy('locating'); setFeedback(null);
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

  const number = (value: string) => (value.trim() === '' ? null : Number(value));
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
          <label htmlFor={`${id}-paste`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Paste a Google Maps link or coordinates')}</label>
          <p id={`${id}-paste-help`} className="text-xs leading-5 text-slate-500 dark:text-slate-400">{t('For example 24.7136, 46.6753, or the link from Share in Google Maps.')}</p>
          <div className="flex flex-wrap gap-2">
            <input id={`${id}-paste`} className="input min-w-0 flex-1 basis-56" dir="ltr" inputMode="url" autoComplete="off" spellCheck={false}
              aria-describedby={`${id}-paste-help`} value={paste} onChange={e => setPaste(e.target.value)} onPaste={onPaste}
              onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); if (paste.trim()) void place(paste); } }} data-testid="pin-paste" />
            <button type="button" className="btn-secondary" disabled={!paste.trim() || !!busy} onClick={() => void place(paste)}>
              {busy === 'resolving' ? t('Reading the link…') : t('Place pin')}
            </button>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button type="button" className="btn-secondary" disabled={!!busy} onClick={locate} data-testid="pin-locate">
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
      <div aria-live="polite" data-testid="pin-feedback">
        {feedback && <p className={`text-sm ${feedback.tone === 'error' ? 'font-medium text-rose-700 dark:text-rose-300' : 'text-emerald-800 dark:text-emerald-300'}`}>{feedback.text}</p>}
      </div>

      <LocationMap latitude={pinned ? lat : null} longitude={pinned ? lng : null} radius={radius} fallback={countryCentre(countryCode)} readOnly={readOnly}
        label={t('Map of {name}', { name })} onMove={(latitude, longitude) => pin(latitude, longitude)} />
      <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">
        {pinned ? t('Drag the pin, or click the map, to fine-tune it.') : t('Paste a link or coordinates, use your current location, or click the map where the site is.')}
      </p>

      <fieldset className="min-w-0">
        <legend className="mb-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">{t('Geofence radius')}</legend>
        <div className="flex flex-wrap items-end gap-2">
          {RADIUS_PRESETS.map(preset => (
            <label key={preset} className="inline-flex min-h-[2.5rem] cursor-pointer items-center rounded-lg border border-slate-300 px-3 text-sm font-medium text-slate-700 has-[:checked]:border-sapphire has-[:checked]:bg-sapphire/10 has-[:checked]:text-sapphire has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-sapphire/40 has-[:disabled]:cursor-not-allowed dark:border-white/15 dark:text-slate-200 dark:has-[:checked]:border-cyanAccent dark:has-[:checked]:bg-cyanAccent/10 dark:has-[:checked]:text-cyanAccent">
              <input type="radio" className="sr-only" name={`${id}-radius`} checked={radius === preset} disabled={readOnly} onChange={() => onChange({ radiusMeters: preset })} />
              <bdi>{t('{meters} m', { meters: preset })}</bdi>
            </label>
          ))}
          <label className="inline-flex min-h-[2.5rem] cursor-pointer items-center rounded-lg border border-slate-300 px-3 text-sm font-medium text-slate-700 has-[:checked]:border-sapphire has-[:checked]:bg-sapphire/10 has-[:checked]:text-sapphire has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-sapphire/40 dark:border-white/15 dark:text-slate-200 dark:has-[:checked]:border-cyanAccent dark:has-[:checked]:bg-cyanAccent/10 dark:has-[:checked]:text-cyanAccent">
            <input type="radio" className="sr-only" name={`${id}-radius`} checked={custom} disabled={readOnly} onChange={() => radiusInput.current?.focus()} />
            {t('Custom')}
          </label>
          <WizardField field={`${path}.radiusMeters`} label={t('Radius (m)')} className="w-28">
            {control => <input {...control} ref={radiusInput} className="input w-full" type="number" inputMode="numeric" min={1} max={100000} step={1} readOnly={readOnly}
              value={location.radiusMeters ?? ''} onChange={e => onChange({ radiusMeters: number(e.target.value) })} />}
          </WizardField>
        </div>
      </fieldset>

      {pinned && !isInGcc(lat!, lng!) && (
        <Notice tone="warning" role="status">
          <p>{t('These coordinates are outside the GCC. Check the pin before you continue.')}</p>
          {looksSwapped(lat!, lng!) && !readOnly && (
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

      <details className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-white/[0.03]" open={!!coordinateError || undefined} data-testid="pin-coordinates">
        <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Coordinates')}</summary>
        <div className="mt-2 flex flex-wrap gap-3">
          <WizardField field={`${path}.latitude`} label={t('Latitude')} className="w-full sm:w-40">
            {control => <input {...control} className="input w-full" dir="ltr" type="number" inputMode="decimal" step="any" min={-90} max={90} readOnly={readOnly}
              value={lat ?? ''} onChange={e => { onChange({ latitude: number(e.target.value) }); setFeedback(null); }} />}
          </WizardField>
          <WizardField field={`${path}.longitude`} label={t('Longitude')} className="w-full sm:w-40">
            {control => <input {...control} className="input w-full" dir="ltr" type="number" inputMode="decimal" step="any" min={-180} max={180} readOnly={readOnly}
              value={lng ?? ''} onChange={e => { onChange({ longitude: number(e.target.value) }); setFeedback(null); }} />}
          </WizardField>
        </div>
      </details>
    </section>
  );
}
