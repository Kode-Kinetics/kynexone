'use client';

import { useEffect, useState } from 'react';
import { payrollApi } from '../api/payroll';

/**
 * Employee id -> "CODE · Name" for the people in a payroll run, read from the run's resolved population (an existing endpoint). Screens that
 * hold only employee ids (validation findings, bank-file blockers, cash/cheque lists) show the name with it instead of "Emp #226".
 * The names are an enrichment: when the read fails or the run is not chosen the map is empty and each screen falls back to its id wording.
 */
export function useRunEmployeeLabels(runId: string | null | undefined): Record<number, string> {
  const [labels, setLabels] = useState<Record<number, string>>({});
  useEffect(() => {
    setLabels({});
    if (!runId) return;
    let cancelled = false;
    payrollApi.runPopulation(runId)
      .then(population => {
        if (cancelled) return;
        const out: Record<number, string> = {};
        for (const m of [...(population.included ?? []), ...(population.excluded ?? []), ...(population.notEligible ?? [])]) {
          if (m.name) out[m.employeeId] = m.code ? `${m.code} · ${m.name}` : m.name;
        }
        setLabels(out);
      })
      .catch(() => { /* names are optional */ });
    return () => { cancelled = true; };
  }, [runId]);
  return labels;
}
