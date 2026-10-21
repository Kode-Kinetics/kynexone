'use client';

import { useEffect, useState } from 'react';
import { masterDataApi, type MasterDataValue } from '../api/setup';

/** The employee reference lists, by the form field that stores them, and the Setup type that holds them. */
export const EMPLOYEE_REFERENCE_TYPES = {
  gender: 'Gender',
  maritalStatus: 'MaritalStatus',
  employmentType: 'EmploymentType',
  contractType: 'ContractType',
} as const;

export type EmployeeReferenceKey = keyof typeof EMPLOYEE_REFERENCE_TYPES;
export type EmployeeReferenceLists = Record<EmployeeReferenceKey, MasterDataValue[]>;

export const EMPTY_EMPLOYEE_REFERENCE_LISTS: EmployeeReferenceLists = { gender: [], maritalStatus: [], employmentType: [], contractType: [] };

const KEYS = Object.keys(EMPLOYEE_REFERENCE_TYPES) as EmployeeReferenceKey[];

/**
 * Loads every value of the four lists, retired ones included — an employee may still hold a retired value and must
 * still read with its label. Forms offer only the active ones (lib/referenceLists.referenceOptions). A list that
 * fails to load comes back empty, which makes readers show the stored value as typed and forms offer their fallback.
 */
export async function loadEmployeeReferenceLists(): Promise<EmployeeReferenceLists> {
  const results = await Promise.allSettled(KEYS.map((key) => masterDataApi.listValuesByCode(EMPLOYEE_REFERENCE_TYPES[key], false)));
  const lists = { ...EMPTY_EMPLOYEE_REFERENCE_LISTS };
  KEYS.forEach((key, index) => {
    const result = results[index];
    lists[key] = result.status === 'fulfilled' && Array.isArray(result.value) ? result.value.filter((value) => value.code?.trim()) : [];
  });
  return lists;
}

export function useEmployeeReferenceLists(): { lists: EmployeeReferenceLists; loaded: boolean } {
  const [lists, setLists] = useState<EmployeeReferenceLists>(EMPTY_EMPLOYEE_REFERENCE_LISTS);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    let cancelled = false;
    void loadEmployeeReferenceLists().then((next) => {
      if (cancelled) return;
      setLists(next);
      setLoaded(true);
    });
    return () => { cancelled = true; };
  }, []);
  return { lists, loaded };
}
