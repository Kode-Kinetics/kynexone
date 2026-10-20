'use client';

import { useCallback } from 'react';
import { clearEmployeeCreateResume, readEmployeeCreateResume, writeEmployeeCreateResume, type EmployeeCreateResume } from '../../lib/employeeCreateDraft';

export function useEmployeeCreateDraft() {
  const read = useCallback(() => typeof window === 'undefined' ? null : readEmployeeCreateResume(window.localStorage), []);
  const remember = useCallback((value: EmployeeCreateResume) => {
    if (typeof window !== 'undefined') writeEmployeeCreateResume(window.localStorage, value);
  }, []);
  const clear = useCallback(() => {
    if (typeof window !== 'undefined') clearEmployeeCreateResume(window.localStorage);
  }, []);
  return { read, remember, clear };
}
