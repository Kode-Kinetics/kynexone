'use client';

import { useEffect, useState } from 'react';
import { MAINTENANCE_EVENT } from '../api/client';
import { maintenanceApi } from '../api/maintenance';

/**
 * Full-page notice while the installation is in maintenance (an upgrade or restore). The API answers every request
 * 503 `{ error: 'maintenance', message }` until an administrator or `upgrade.sh` switches it off; the "Try again"
 * button reloads, which finds out. An administrator can switch it off from here (the switch is exempt from the
 * gate); for anyone else the server refuses and the notice says so.
 */
export function MaintenanceNotice() {
  const [message, setMessage] = useState<string | null>(null);
  const [adminNote, setAdminNote] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function switchOff() {
    setBusy(true);
    setAdminNote(null);
    try {
      await maintenanceApi.set(false);
      window.location.reload();
    } catch {
      setAdminNote('Only an administrator can switch maintenance mode off.');
      setBusy(false);
    }
  }

  useEffect(() => {
    const onMaintenance = (e: Event) => {
      const detail = (e as CustomEvent<string | undefined>).detail;
      setMessage(detail || 'KynexOne is being updated. Please try again in a few minutes.');
    };
    window.addEventListener(MAINTENANCE_EVENT, onMaintenance);
    return () => window.removeEventListener(MAINTENANCE_EVENT, onMaintenance);
  }, []);

  if (!message) return null;

  return (
    <div role="alertdialog" aria-modal="true" aria-labelledby="maintenance-title"
      className="fixed inset-0 z-[1000] flex items-center justify-center bg-white/95 p-6 dark:bg-gray-950/95">
      <div className="max-w-md text-center">
        <h1 id="maintenance-title" className="text-xl font-semibold text-gray-900 dark:text-gray-100">Maintenance in progress</h1>
        <p className="mt-3 text-sm text-gray-600 dark:text-gray-300">{message}</p>
        <button
          type="button"
          className="mt-6 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
          onClick={() => window.location.reload()}
        >
          Try again
        </button>
        <button
          type="button"
          disabled={busy}
          className="mt-3 block w-full text-xs text-gray-500 underline hover:text-gray-700 disabled:opacity-60 dark:text-gray-400"
          onClick={() => void switchOff()}
        >
          {busy ? 'Switching off…' : 'Administrator? Switch maintenance off'}
        </button>
        {adminNote && <p className="mt-2 text-xs text-gray-500 dark:text-gray-400" role="status">{adminNote}</p>}
      </div>
    </div>
  );
}
