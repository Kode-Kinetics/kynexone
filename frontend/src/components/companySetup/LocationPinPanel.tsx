'use client';

import { useId, useRef } from 'react';
import { useT } from '../../hooks/useT';
import type { CompanySetupLocation } from '../../api/companySetup';
import { RADIUS_PRESETS } from '../../lib/mapLink';
import { WizardField, useWizardForm } from './wizardForm';
import { LocationPinCore } from './LocationPinCore';

const ADDRESS_MAX = 200;

/**
 * The wizard's "Pin this location": the shared pin panel (LocationPinCore) plus this form's own fields, bound to the
 * wizard's state and field errors: Address, the geofence radius presets, and the raw Coordinates.
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
  const radiusInput = useRef<HTMLInputElement>(null);

  const lat = location.latitude ?? null;
  const lng = location.longitude ?? null;
  const radius = location.radiusMeters && location.radiusMeters > 0 ? location.radiusMeters : null;
  const custom = radius != null && !RADIUS_PRESETS.includes(radius as (typeof RADIUS_PRESETS)[number]);
  const coordinateError = errorFor(`${path}.latitude`) || errorFor(`${path}.longitude`);
  const number = (value: string) => (value.trim() === '' ? null : Number(value));

  return (
    <LocationPinCore
      value={{ latitude: lat, longitude: lng, radiusMeters: location.radiusMeters, address: location.address }}
      onChange={patch => onChange(patch)} countryCode={countryCode} name={name} readOnly={readOnly} addressMax={ADDRESS_MAX}
      address={(
        <WizardField field={`${path}.address`} label={t('Address')} help={t('Saved with the location. Searching an address fills it in; you can edit it.')}>
          {control => <input {...control} className="input w-full" dir="auto" maxLength={ADDRESS_MAX} readOnly={readOnly} value={location.address ?? ''}
            onChange={e => onChange({ address: e.target.value })} data-testid="pin-address" />}
        </WizardField>
      )}
      footer={({ clearFeedback }) => (
        <details className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-white/[0.03]" open={!!coordinateError || undefined} data-testid="pin-coordinates">
          <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Coordinates')}</summary>
          <div className="mt-2 flex flex-wrap gap-3">
            <WizardField field={`${path}.latitude`} label={t('Latitude')} className="w-full sm:w-40">
              {control => <input {...control} className="input w-full" dir="ltr" type="number" inputMode="decimal" step="any" min={-90} max={90} readOnly={readOnly}
                value={lat ?? ''} onChange={e => { onChange({ latitude: number(e.target.value) }); clearFeedback(); }} />}
            </WizardField>
            <WizardField field={`${path}.longitude`} label={t('Longitude')} className="w-full sm:w-40">
              {control => <input {...control} className="input w-full" dir="ltr" type="number" inputMode="decimal" step="any" min={-180} max={180} readOnly={readOnly}
                value={lng ?? ''} onChange={e => { onChange({ longitude: number(e.target.value) }); clearFeedback(); }} />}
            </WizardField>
          </div>
        </details>
      )}
    >
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
    </LocationPinCore>
  );
}
