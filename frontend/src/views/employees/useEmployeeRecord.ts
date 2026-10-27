'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { employeesApi, type EmployeeDetail, type EmployeeReadiness } from '../../api/employees';

export type EmployeeLoadErrorKind = 'not_found' | 'forbidden' | 'network' | 'unknown';

export interface EmployeeLoadError {
  kind: EmployeeLoadErrorKind;
  message: string;
  status?: number;
}

interface LoadState<T> {
  data: T | null;
  loading: boolean;
  error: EmployeeLoadError | null;
}

export interface EmployeeRecordState {
  detail: LoadState<EmployeeDetail>;
  readiness: LoadState<EmployeeReadiness>;
  refreshRecord(): Promise<void>;
  /** Re-reads the record without the full-page loading state, so a field the user is working in keeps its place. */
  reloadRecordQuietly(): Promise<void>;
  refreshReadiness(): Promise<void>;
}

function toLoadError(cause: unknown, resource: 'employee' | 'readiness'): EmployeeLoadError {
  const error = cause as { isAxiosError?: boolean; response?: { status?: number } } | null;
  const status = error?.response?.status;
  if (status === 404) return { kind: 'not_found', status, message: 'Employee record not found.' };
  if (status === 403) return { kind: 'forbidden', status, message: 'You do not have access to this employee.' };
  if (error?.isAxiosError && !error.response) {
    return {
      kind: 'network',
      message: resource === 'employee'
        ? 'Employee record could not be reached.'
        : 'Readiness could not be reached.',
    };
  }
  return {
    kind: 'unknown',
    status,
    message: resource === 'employee'
      ? 'Employee record could not be loaded.'
      : 'Readiness could not be loaded.',
  };
}

const emptyDetail: LoadState<EmployeeDetail> = { data: null, loading: true, error: null };
const emptyReadiness: LoadState<EmployeeReadiness> = { data: null, loading: true, error: null };

export function useEmployeeRecord(employeeId: number): EmployeeRecordState {
  const [detail, setDetail] = useState<LoadState<EmployeeDetail>>(emptyDetail);
  const [readiness, setReadiness] = useState<LoadState<EmployeeReadiness>>(emptyReadiness);
  const detailGeneration = useRef(0);
  const readinessGeneration = useRef(0);
  const employeeIdRef = useRef(employeeId);

  const loadDetail = useCallback(async (targetEmployeeId: number, generation: number) => {
    try {
      const data = await employeesApi.get(targetEmployeeId);
      if (detailGeneration.current !== generation || employeeIdRef.current !== targetEmployeeId) return;
      setDetail({ data, loading: false, error: null });
    } catch (cause) {
      if (detailGeneration.current !== generation || employeeIdRef.current !== targetEmployeeId) return;
      setDetail({ data: null, loading: false, error: toLoadError(cause, 'employee') });
    }
  }, []);

  const loadReadiness = useCallback(async (targetEmployeeId: number, generation: number) => {
    try {
      const data = await employeesApi.readiness(targetEmployeeId);
      if (readinessGeneration.current !== generation || employeeIdRef.current !== targetEmployeeId) return;
      setReadiness({ data, loading: false, error: null });
    } catch (cause) {
      if (readinessGeneration.current !== generation || employeeIdRef.current !== targetEmployeeId) return;
      setReadiness({ data: null, loading: false, error: toLoadError(cause, 'readiness') });
    }
  }, []);

  useEffect(() => {
    employeeIdRef.current = employeeId;
    const nextDetailGeneration = ++detailGeneration.current;
    const nextReadinessGeneration = ++readinessGeneration.current;
    setDetail(emptyDetail);
    setReadiness(emptyReadiness);
    void loadDetail(employeeId, nextDetailGeneration);
    void loadReadiness(employeeId, nextReadinessGeneration);

    return () => {
      detailGeneration.current += 1;
      readinessGeneration.current += 1;
    };
  }, [employeeId, loadDetail, loadReadiness]);

  const refreshRecord = useCallback(async () => {
    const targetEmployeeId = employeeIdRef.current;
    const nextDetailGeneration = ++detailGeneration.current;
    const nextReadinessGeneration = ++readinessGeneration.current;
    setDetail(current => ({ ...current, loading: true, error: null }));
    setReadiness(current => ({ ...current, loading: true, error: null }));
    await Promise.all([
      loadDetail(targetEmployeeId, nextDetailGeneration),
      loadReadiness(targetEmployeeId, nextReadinessGeneration),
    ]);
  }, [loadDetail, loadReadiness]);

  const reloadRecordQuietly = useCallback(async () => {
    const targetEmployeeId = employeeIdRef.current;
    await loadDetail(targetEmployeeId, ++detailGeneration.current);
  }, [loadDetail]);

  const refreshReadiness = useCallback(async () => {
    const targetEmployeeId = employeeIdRef.current;
    const nextGeneration = ++readinessGeneration.current;
    setReadiness(current => ({ ...current, loading: true, error: null }));
    await loadReadiness(targetEmployeeId, nextGeneration);
  }, [loadReadiness]);

  return { detail, readiness, refreshRecord, reloadRecordQuietly, refreshReadiness };
}
