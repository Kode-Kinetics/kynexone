'use client';

import { useCallback, useEffect, useState } from 'react';
import { ShieldCheck } from 'lucide-react';
import { apiErrorBody, approvalWorkflowsApi } from '../api/approvals';
import type { ApprovalGovernanceSettings } from '../api/approvals';

/**
 * W2-E — the per-tenant "different person at each approval step" rule. Shown on the workflow
 * configuration screen and under Tenant Admin, backed by GET/PUT /api/approval-workflows/settings
 * (approvals.manage). Off by default.
 */
export function ApprovalGovernanceToggle({ onChanged }: { onChanged?: (settings: ApprovalGovernanceSettings) => void } = {}) {
  const [settings, setSettings] = useState<ApprovalGovernanceSettings | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setSettings(await approvalWorkflowsApi.getSettings());
    } catch (err) {
      const status = (err as { response?: { status?: number } })?.response?.status;
      setError(status === 403 ? 'You need the approvals.manage permission to change this setting.' : 'Could not load approval settings.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const save = async (next: boolean) => {
    if (!settings) return;
    const previous = settings;
    setSettings({ requireDistinctApproverPerStep: next });
    setSaving(true);
    setError('');
    setSaved('');
    try {
      const result = await approvalWorkflowsApi.saveSettings({ requireDistinctApproverPerStep: next });
      setSettings(result);
      setSaved(next
        ? 'On. From now on nobody can decide two steps of the same submission — override holders included.'
        : 'Off. One person may decide every step they are routed to.');
      onChanged?.(result);
    } catch (err) {
      setSettings(previous);
      setError(apiErrorBody(err).message ?? 'Could not save the setting. Please try again.');
    } finally {
      setSaving(false);
    }
  };

  const on = settings?.requireDistinctApproverPerStep ?? false;

  return (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div className="min-w-0 max-w-3xl">
        <h2 className="inline-flex items-center gap-2 text-sm font-bold text-slate-950 dark:text-white"><ShieldCheck className="h-4 w-4" /> Different person at each approval step</h2>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          When on, someone who decided an earlier step of a request cannot decide a later step of the same submission — for example a line manager who also holds the HR Manager role. The request stays in the queue for another eligible approver. This applies to <strong>override holders too</strong>: override exists to unblock routing when the routed person is absent, not to let one person be two approvers. Off by default; when off, behaviour is unchanged.
        </p>
        {loading && <p className="mt-2 text-xs text-slate-400">Loading…</p>}
        {error && <p role="alert" className="mt-2 text-xs font-medium text-rose-600 dark:text-rose-300">{error}</p>}
        {saved && !error && <p role="status" className="mt-2 text-xs font-medium text-emerald-600 dark:text-emerald-300">{saved}</p>}
      </div>
      <div className="flex items-center gap-3">
        <span className="text-xs font-semibold text-slate-500 dark:text-slate-400">{on ? 'On' : 'Off'}</span>
        <button
          id="distinct-approver-toggle"
          type="button"
          role="switch"
          aria-checked={on ? 'true' : 'false'}
          aria-label="Require a different person at each approval step"
          disabled={!settings || saving}
          onClick={() => save(!on)}
          className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-sapphire disabled:opacity-60 ${on ? 'bg-sapphire' : 'bg-slate-300 dark:bg-white/20'}`}
        >
          <span className={`inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform ${on ? 'translate-x-6' : 'translate-x-1'}`} />
        </button>
      </div>
    </div>
  );
}
