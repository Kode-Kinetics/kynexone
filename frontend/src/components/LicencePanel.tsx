'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { licenceApi, type LicenceView } from '../api/licence';

const STATE_TEXT: Record<LicenceView['state'], { label: string; tone: string; help: string }> = {
  Valid: { label: 'Valid', tone: 'bg-green-50 text-green-800 border-green-200', help: 'The licence is valid.' },
  Grace: {
    label: 'Expired — in grace period',
    tone: 'bg-amber-50 text-amber-900 border-amber-300',
    help: 'Everything still works during the grace days. Import the renewed licence from your supplier before they run out.',
  },
  Expired: {
    label: 'Expired',
    tone: 'bg-red-50 text-red-800 border-red-200',
    help: 'The grace period is over, so the product is locked until a renewed licence is imported.',
  },
  Missing: {
    label: 'No licence installed',
    tone: 'bg-red-50 text-red-800 border-red-200',
    help: 'Send the installation id below to your KynexOne supplier, then import the licence file they send back.',
  },
};

function limit(value: number, unlimitedWhenZero = true) {
  return value === 0 && unlimitedWhenZero ? 'Unlimited' : value.toLocaleString();
}

function date(value: string | null) {
  return value ? new Date(value).toLocaleDateString() : '—';
}

/**
 * Organisation Admin → Licence: what the installation's licence grants, what is used against it, the
 * installation id to send the supplier, and importing a renewed licence file.
 */
export function LicencePanel() {
  const [licence, setLicence] = useState<LicenceView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    try {
      setLicence(await licenceApi.get());
    } catch {
      setError('Could not load the licence. Refresh to try again.');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  async function onImport(file: File | undefined) {
    if (!file) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const result = await licenceApi.import(file);
      setMessage(result.outcome === 'Imported'
        ? `Licence ${result.licenceId} for ${result.licensee} is now installed.`
        : `Licence ${result.licenceId} is already the installed licence; nothing changed.`);
      await load();
    } catch (err: unknown) {
      const detail = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(detail ?? 'The licence could not be imported.');
    } finally {
      setBusy(false);
      if (fileInput.current) fileInput.current.value = '';
    }
  }

  if (!licence) {
    return (
      <div className="rounded-xl border border-gray-200 bg-white p-6 text-sm text-gray-500">
        {error ?? 'Loading licence…'}
      </div>
    );
  }

  const state = STATE_TEXT[licence.state];
  const rows: { label: string; value: string }[] = [
    { label: 'Licensee', value: licence.licensee ?? '—' },
    { label: 'Edition', value: licence.edition ?? '—' },
    { label: 'Account type', value: licence.accountType === 'Group' ? 'Group (several companies)' : 'Single company' },
    { label: 'Licence id', value: licence.licenceId ?? '—' },
    { label: 'Expires', value: licence.expiresAtUtc ? `${date(licence.expiresAtUtc)} (+${licence.graceDays ?? 0} grace days)` : licence.licenceId ? 'Never (perpetual)' : '—' },
    { label: 'Support and upgrades until', value: licence.supportExpiresAtUtc ? date(licence.supportExpiresAtUtc) : licence.licenceId ? 'No limit' : '—' },
    { label: 'Product version', value: licence.version },
  ];
  const usage: { label: string; used: number; max: number }[] = [
    { label: 'Active employees', used: licence.usage.activeEmployees, max: licence.limits.maxEmployees },
    { label: 'Active users', used: licence.usage.activeUsers, max: licence.limits.maxUsers },
    { label: 'Companies', used: licence.usage.companies, max: licence.limits.maxCompanies },
  ];

  return (
    <div className="space-y-4">
      <div className={`rounded-xl border px-4 py-3 ${state.tone}`} role={licence.state === 'Valid' ? 'status' : 'alert'}>
        <p className="text-sm font-semibold">{state.label}</p>
        <p className="mt-0.5 text-xs">{state.help}</p>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">Licence</h2>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
          {rows.map(row => (
            <div key={row.label}>
              <dt className="text-xs font-medium text-gray-500">{row.label}</dt>
              <dd className="mt-0.5 text-sm font-semibold text-gray-900">{row.value}</dd>
            </div>
          ))}
          <div className="sm:col-span-2">
            <dt className="text-xs font-medium text-gray-500">Installation id (send this to your supplier for a licence bound to this installation)</dt>
            <dd className="mt-0.5 font-mono text-sm text-gray-900 break-all">{licence.installationId}</dd>
          </div>
        </dl>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">Use against the licence</h2>
        <div className="space-y-3">
          {usage.map(u => (
            <div key={u.label} className="flex items-center justify-between text-sm">
              <span className="font-medium text-gray-700">{u.label}</span>
              <span className={u.max > 0 && u.used >= u.max ? 'font-semibold text-red-600' : 'text-gray-600'}>
                {u.used.toLocaleString()} / {limit(u.max)}
              </span>
            </div>
          ))}
          <div className="flex items-center justify-between text-sm">
            <span className="font-medium text-gray-700">Administrator users</span>
            <span className="text-gray-600">up to {limit(licence.limits.maxAdminUsers)}</span>
          </div>
          <div className="flex items-center justify-between text-sm">
            <span className="font-medium text-gray-700">AI tokens per month</span>
            <span className="text-gray-600">{licence.limits.aiMonthlyTokenLimit === 0 ? 'AI not licensed' : licence.limits.aiMonthlyTokenLimit.toLocaleString()}</span>
          </div>
        </div>
        {(licence.modules.length > 0 || licence.optInFeatures.length > 0) && (
          <div className="mt-4 border-t border-gray-100 pt-4">
            <p className="mb-2 text-xs font-medium text-gray-500">Licensed modules and features (core and statutory modules are always included)</p>
            <div className="flex flex-wrap gap-1.5">
              {[...licence.modules, ...licence.optInFeatures].map(key => (
                <span key={key} className="rounded-full border border-green-200 bg-green-50 px-2 py-0.5 text-xs text-green-700">{key}</span>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Import a licence</h2>
        <p className="mb-4 text-xs text-gray-500">Choose the <code>.licence</code> file your KynexOne supplier sent. A newer licence replaces the current one immediately.</p>
        {message && <p className="mb-3 rounded-lg border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800" role="status">{message}</p>}
        {error && <p className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>}
        <label className="inline-flex cursor-pointer items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700">
          {busy ? 'Importing…' : 'Choose licence file'}
          <input
            ref={fileInput}
            type="file"
            accept=".licence,application/json"
            className="sr-only"
            disabled={busy}
            onChange={e => void onImport(e.target.files?.[0])}
          />
        </label>
      </div>
    </div>
  );
}
