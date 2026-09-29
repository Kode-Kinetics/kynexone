'use client';
import { useEffect, useState } from 'react';
import dynamic from 'next/dynamic';
import { useAuth } from '../../contexts/AuthContext';
import { saudiBankExportsApi } from '../../api/saudiBankExports';

// The server allow-list, not a browser flag, owns activation. Old APIs, denied requests and
// failed probes keep this additive feature hidden without disrupting existing payroll.
const Panel = dynamic(() => import('./SaudiBankExportPanel').then(m => m.SaudiBankExportPanel));
export function SaudiBankExportGate({ batchId, employeeIds }: { batchId: string; employeeIds: number[] }) {
  const { hasPermission } = useAuth();
  const permitted = hasPermission('payroll.export');
  const [enabledFor, setEnabledFor] = useState<string | null>(null);
  useEffect(() => {
    setEnabledFor(null);
    if (!permitted) return;
    const ac = new AbortController();
    saudiBankExportsApi.availability(batchId, { signal: ac.signal })
      .then(value => { if (!ac.signal.aborted && value.enabled === true) setEnabledFor(batchId); })
      .catch(() => { if (!ac.signal.aborted) setEnabledFor(null); });
    return () => ac.abort();
  }, [batchId, permitted]);
  return permitted && enabledFor === batchId
    ? <Panel key={batchId} batchId={batchId} employeeIds={employeeIds} /> : null;
}
