'use client';

import { useEffect, useState } from 'react';
import { offersApi, type OfferPlacementOptions } from '../api/recruitment';

export interface OfferPlacementValue {
  departmentId: string;
  designationId: string;
}

interface OfferPlacementFieldsProps {
  value: OfferPlacementValue;
  onChange: (value: OfferPlacementValue, names: { department: string; designation: string }) => void;
  /**
   * The drawer generates an offer for one application, whose job opening already names a department
   * and designation; leaving a field on "the opening's" keeps that default. The Offers tab has no
   * such default, so the designation must be picked.
   */
  openingDefaults: boolean;
  idPrefix: string;
}

/**
 * Department and designation for an offer, picked from the organisation's own records. They used to
 * be free text, and a name no record matched surfaced only when HR tried to activate the accepted
 * offer's employee record, as a refusal.
 */
export function OfferPlacementFields({ value, onChange, openingDefaults, idPrefix }: OfferPlacementFieldsProps) {
  const [options, setOptions] = useState<OfferPlacementOptions | null>(null);
  const [loadError, setLoadError] = useState('');

  useEffect(() => {
    let cancelled = false;
    offersApi.placementOptions()
      .then((o) => { if (!cancelled) setOptions(o); })
      .catch((err) => {
        if (cancelled) return;
        const message = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
        setLoadError(message ?? 'The department and designation lists could not be loaded.');
      });
    return () => { cancelled = true; };
  }, []);

  const nameOf = (list: { id: string; name: string }[] | undefined, id: string) => list?.find((x) => x.id === id)?.name ?? '';
  const change = (next: OfferPlacementValue) =>
    onChange(next, { department: nameOf(options?.departments, next.departmentId), designation: nameOf(options?.designations, next.designationId) });

  if (loadError) {
    return (
      <p role="alert" className="text-xs text-rose-600 dark:text-rose-300">
        {loadError}{openingDefaults ? " The offer will use the job opening's department and designation." : ''}
      </p>
    );
  }

  const noRecords = options && options.designations.length === 0;
  return (
    <div className="grid grid-cols-2 gap-2">
      <label htmlFor={`${idPrefix}-designation`} className="block text-xs font-medium text-slate-600 dark:text-slate-400">
        Designation{openingDefaults ? '' : ' *'}
        <select
          id={`${idPrefix}-designation`}
          className="input mt-1 w-full"
          value={value.designationId}
          disabled={!options}
          onChange={(e) => change({ ...value, designationId: e.target.value })}
        >
          <option value="">{options ? (openingDefaults ? "The opening's designation" : 'Choose a designation') : 'Loading…'}</option>
          {options?.designations.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      </label>
      <label htmlFor={`${idPrefix}-department`} className="block text-xs font-medium text-slate-600 dark:text-slate-400">
        Department
        <select
          id={`${idPrefix}-department`}
          className="input mt-1 w-full"
          value={value.departmentId}
          disabled={!options}
          onChange={(e) => change({ ...value, departmentId: e.target.value })}
        >
          <option value="">{options ? (openingDefaults ? "The opening's department" : 'No department') : 'Loading…'}</option>
          {options?.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      </label>
      {noRecords && (
        <p className="col-span-2 text-xs text-amber-700 dark:text-amber-300">
          Your organisation has no designations yet. Add them under Setup before making offers, or the hire cannot be activated.
        </p>
      )}
    </div>
  );
}
