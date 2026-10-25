'use client';

import { useCallback, useEffect, useState } from 'react';
import { maintenanceApi, type MaintenanceStatus } from '../api/maintenance';
import { getPublicInstallation } from '../api/publicInstallation';

/** What the mobile app's server screen scans: kynexone://server?url=<this installation's public address>. */
export function serverQrPayload(origin: string): string {
  return `kynexone://server?url=${encodeURIComponent(origin)}`;
}

/**
 * Installation administration beside the licence: the code the mobile app scans to find this server, and maintenance
 * mode (an upgrade switches it on and off by itself; an administrator can too, for example before a restore).
 */
export function InstallationPanel() {
  const [origin, setOrigin] = useState(() => (typeof window === 'undefined' ? '' : window.location.origin));
  const [qr, setQr] = useState<string | null>(null);
  const [maintenance, setMaintenance] = useState<MaintenanceStatus | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // The server's own public address (APP_URL) wins over the address this browser used: on a LAN they can differ.
  useEffect(() => {
    getPublicInstallation()
      .then(info => { if (info.publicBaseUrl.startsWith('https://')) setOrigin(info.publicBaseUrl); })
      .catch(() => { /* keep the browser's origin */ });
  }, []);

  // Rendered locally (no network), like the welcome slips.
  useEffect(() => {
    if (!origin) return;
    let cancelled = false;
    void import('qrcode')
      .then(QRCode => QRCode.toDataURL(serverQrPayload(origin), {
        errorCorrectionLevel: 'M', margin: 1, width: 220, color: { dark: '#0f172a', light: '#ffffff' },
      }))
      .then(url => { if (!cancelled) setQr(url); })
      .catch(() => { if (!cancelled) setQr(null); });
    return () => { cancelled = true; };
  }, [origin]);

  const load = useCallback(async () => {
    try {
      const status = await maintenanceApi.get();
      setMaintenance(status);
      setDraft(status.message ?? '');
    } catch {
      setError('Could not read the maintenance state.');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  async function toggle(on: boolean) {
    setBusy(true);
    setError(null);
    try {
      const status = await maintenanceApi.set(on, on ? draft.trim() : undefined);
      setMaintenance(status);
    } catch (err: unknown) {
      const detail = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(detail ?? 'Maintenance mode could not be changed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mt-6 grid gap-6 md:grid-cols-2">
      <div className="rounded-xl border border-gray-200 bg-white p-6">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Mobile app</h2>
        <p className="mb-4 text-xs text-gray-500">
          Employees install the KynexOne app from their app store, tap <strong>Scan QR code</strong> and scan this code,
          or type the address below. The app only connects over HTTPS.
        </p>
        <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
          {qr
            ? <img src={qr} alt="QR code for the KynexOne mobile app" width={180} height={180} className="rounded-lg border border-gray-200" />
            : <div className="flex h-[180px] w-[180px] items-center justify-center rounded-lg border border-dashed border-gray-300 text-xs text-gray-400">QR code unavailable</div>}
          <div className="min-w-0 text-sm">
            <p className="text-xs font-medium text-gray-500">Server address</p>
            <p className="break-all font-mono text-gray-900">{origin || '—'}</p>
            {origin && !origin.startsWith('https://') && (
              <p className="mt-2 text-xs text-amber-700">This address is not HTTPS: the mobile app will refuse it. Set APP_URL to the HTTPS address.</p>
            )}
          </div>
        </div>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Maintenance mode</h2>
        <p className="mb-4 text-xs text-gray-500">
          While it is on, everyone except this screen sees a maintenance notice and background jobs pause. Upgrades switch
          it on and off by themselves.
        </p>
        {error && <p className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>}
        {maintenance && (
          <>
            <p className="mb-3 text-sm">
              Status:{' '}
              <span className={maintenance.on ? 'font-semibold text-amber-700' : 'font-semibold text-green-700'}>
                {maintenance.on ? 'On' : 'Off'}
              </span>
            </p>
            <label className="mb-1 block text-xs font-medium text-gray-500" htmlFor="maintenance-message">Message shown to people</label>
            <textarea
              id="maintenance-message"
              className="mb-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
              rows={2}
              maxLength={500}
              value={draft}
              placeholder="KynexOne is being updated. Please try again in a few minutes."
              onChange={e => setDraft(e.target.value)}
              disabled={busy}
            />
            <button
              type="button"
              disabled={busy}
              onClick={() => void toggle(!maintenance.on)}
              className={`rounded-lg px-4 py-2 text-sm font-medium text-white disabled:opacity-60 ${maintenance.on ? 'bg-green-600 hover:bg-green-700' : 'bg-amber-600 hover:bg-amber-700'}`}
            >
              {busy ? 'Saving…' : maintenance.on ? 'Switch maintenance off' : 'Switch maintenance on'}
            </button>
          </>
        )}
      </div>
    </div>
  );
}
