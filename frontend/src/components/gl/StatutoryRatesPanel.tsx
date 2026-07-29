'use client';

import { useCallback, useEffect, useState } from 'react';
import { CheckCircle2, RotateCcw, ShieldAlert } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeRatesApi,
  type StatutoryOverride,
  type StatutoryOverrideRequest,
  type StatutoryRateRow,
} from '../../api/financeRates';
import { Modal } from '../Modal';
import { Badge, Field, FieldError, PanelState, fmtDate, todayIso } from './glUi';

interface Props {
  scope: string | null;
  scopeLabel: string;
  countryCode: string;
  jurisdiction: string;
  canOverride: boolean; // payroll.rates.statutory_override
  canApprove: boolean; // approvals.decide
}

interface OverrideDraft {
  ruleKey: string;
  overrideValue: string;
  effectiveFrom: string;
  effectiveTo: string;
  reviewBy: string;
  reason: string;
}

export function StatutoryRatesPanel({ scope, scopeLabel, countryCode, jurisdiction, canOverride, canApprove }: Props) {
  const [rows, setRows] = useState<StatutoryRateRow[]>([]);
  const [loading, setLoading] = useState(true);
  // PendingApproval overrides created this session (the list endpoint only surfaces Active ones).
  const [pending, setPending] = useState<Record<string, StatutoryOverride>>({});

  const [modalOpen, setModalOpen] = useState(false);
  const [baseRow, setBaseRow] = useState<StatutoryRateRow | null>(null);
  const [draft, setDraft] = useState<OverrideDraft | null>(null);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);

  const canLoad = scope !== null && !!countryCode;

  const load = useCallback(async () => {
    if (!canLoad) {
      setRows([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      setRows(await financeRatesApi.listStatutory(scope, countryCode, jurisdiction));
    } catch (e) {
      notifyApiError(e);
    } finally {
      setLoading(false);
    }
  }, [canLoad, scope, countryCode, jurisdiction]);

  useEffect(() => {
    setPending({});
    load();
  }, [load]);

  const openOverride = (row: StatutoryRateRow) => {
    setBaseRow(row);
    setDraft({
      ruleKey: row.ruleKey,
      overrideValue: row.overrideValue ?? (row.platformDefault != null ? String(row.platformDefault) : ''),
      effectiveFrom: todayIso(),
      effectiveTo: '',
      reviewBy: '',
      reason: '',
    });
    setError('');
    setModalOpen(true);
  };

  const save = async () => {
    if (!draft || scope === null) return;
    if (!draft.overrideValue.trim()) return setError('An override value is required.');
    if (!draft.effectiveFrom) return setError('An effective-from date is required.');
    if (!draft.reviewBy) return setError('A review/expiry date is required so the override cannot silently outlive its justification.');
    if (!draft.reason.trim()) return setError('A reason is required for a statutory override.');
    if (draft.effectiveTo && draft.effectiveTo < draft.effectiveFrom) return setError('Effective-to cannot be before effective-from.');
    setSaving(true);
    setError('');
    const body: StatutoryOverrideRequest = {
      companyId: scope,
      countryCode,
      jurisdiction,
      ruleKey: draft.ruleKey,
      overrideValue: draft.overrideValue.trim(),
      effectiveFrom: draft.effectiveFrom,
      effectiveTo: draft.effectiveTo || null,
      reviewBy: draft.reviewBy,
      reason: draft.reason.trim(),
    };
    try {
      const created = await financeRatesApi.createOverride(body);
      setPending((p) => ({ ...p, [created.ruleKey]: created }));
      setModalOpen(false);
    } catch (err) {
      setError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to create override.');
    } finally {
      setSaving(false);
    }
  };

  const approve = async (ov: StatutoryOverride) => {
    setBusyKey(ov.ruleKey);
    try {
      await financeRatesApi.approveOverride(ov.id);
      setPending((p) => {
        const next = { ...p };
        delete next[ov.ruleKey];
        return next;
      });
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setBusyKey(null);
    }
  };

  const revert = async (id: string, ruleKey: string) => {
    if (!window.confirm(`Revert the override for "${ruleKey}" back to the platform statutory default?`)) return;
    setBusyKey(ruleKey);
    try {
      await financeRatesApi.revertOverride(id);
      setPending((p) => {
        const next = { ...p };
        delete next[ruleKey];
        return next;
      });
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setBusyKey(null);
    }
  };

  if (scope === null) {
    return (
      <div className="surface p-6 text-center">
        <ShieldAlert className="mx-auto mb-2 h-6 w-6 text-slate-300 dark:text-slate-600" />
        <p className="text-sm font-medium text-slate-700 dark:text-slate-200">Select a company</p>
        <p className="mt-1 text-xs text-slate-400">Statutory overrides apply only to a specific legal entity — pick a company above to view and bound-override its statutory rates.</p>
      </div>
    );
  }

  if (!countryCode) {
    return (
      <div className="surface p-6 text-center">
        <p className="text-sm font-medium text-slate-700 dark:text-slate-200">No country on this company</p>
        <p className="mt-1 text-xs text-slate-400">Set the company&apos;s country before configuring statutory rates.</p>
      </div>
    );
  }

  return (
    <div className="surface p-4">
      <div className="mb-3">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Statutory Rates</h3>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Vendor-managed statutory rates for <span className="font-medium">{scopeLabel}</span> ({countryCode}
          {jurisdiction ? `/${jurisdiction}` : ''}). These are never a free edit — a bounded per-company override requires a reason, an effective date, a review date, and a second-person approval. The platform default remains the authoritative fallback.
        </p>
      </div>

      <PanelState loading={loading} empty={rows.length === 0} emptyLabel="No statutory rules found for this country/jurisdiction.">
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full min-w-[760px] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
              <tr>
                <th className="px-3 py-2">Statutory rule</th>
                <th className="px-3 py-2 text-right">Platform default</th>
                <th className="px-3 py-2 text-right">Resolved</th>
                <th className="px-3 py-2">Override</th>
                <th className="px-3 py-2 text-right">Manage</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const pend = pending[r.ruleKey];
                const busy = busyKey === r.ruleKey;
                return (
                  <tr key={r.ruleKey} className="border-t border-slate-100 dark:border-white/5">
                    <td className="px-3 py-2 font-mono text-xs">{r.ruleKey}</td>
                    <td className="px-3 py-2 text-right tabular-nums text-slate-500">{r.platformDefault ?? '—'}</td>
                    <td className="px-3 py-2 text-right font-medium tabular-nums">{r.resolvedValue ?? '—'}</td>
                    <td className="px-3 py-2">
                      {pend ? (
                        <div className="space-y-0.5">
                          <div className="flex items-center gap-1.5">
                            <Badge tone="amber">Pending approval</Badge>
                            <span className="text-xs tabular-nums text-slate-600 dark:text-slate-300">→ {pend.overrideValue}</span>
                          </div>
                          <p className="text-[11px] text-slate-400">{pend.reason}</p>
                        </div>
                      ) : r.isOverride ? (
                        <div className="space-y-0.5">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <Badge tone="violet" title="An active per-company override is in effect.">Override</Badge>
                            <span className="text-xs tabular-nums text-slate-600 dark:text-slate-300">→ {r.overrideValue}</span>
                            {r.defaultDriftedSinceOverride && (
                              <Badge tone="rose" title="The platform default changed after this override was set. Review it.">Default drifted</Badge>
                            )}
                          </div>
                          <p className="text-[11px] text-slate-400">
                            {r.reason}
                            {r.effectiveFrom && <span> · from {fmtDate(r.effectiveFrom)}</span>}
                            {r.reviewBy && <span> · review by {fmtDate(r.reviewBy)}</span>}
                          </p>
                        </div>
                      ) : (
                        <span className="text-[11px] text-slate-400">Uses platform default</span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1">
                        {pend ? (
                          <>
                            {canApprove && (
                              <button
                                type="button"
                                onClick={() => approve(pend)}
                                disabled={busy}
                                className="btn-secondary h-7 px-2 text-xs disabled:opacity-60"
                                title="Approve (must be a different person from the creator)"
                              >
                                <CheckCircle2 className="h-3 w-3" /> Approve
                              </button>
                            )}
                            {canOverride && (
                              <button
                                type="button"
                                onClick={() => revert(pend.id, r.ruleKey)}
                                disabled={busy}
                                aria-label="Discard pending override"
                                className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:text-rose-500 disabled:opacity-40 dark:border-white/10"
                              >
                                <RotateCcw className="h-3.5 w-3.5" />
                              </button>
                            )}
                          </>
                        ) : r.isOverride && r.overrideId ? (
                          canOverride && (
                            <button
                              type="button"
                              onClick={() => revert(r.overrideId!, r.ruleKey)}
                              disabled={busy}
                              className="btn-secondary h-7 px-2 text-xs disabled:opacity-60"
                              title="Revert to the platform statutory default"
                            >
                              <RotateCcw className="h-3 w-3" /> Revert
                            </button>
                          )
                        ) : (
                          canOverride && (
                            <button type="button" onClick={() => openOverride(r)} className="btn-secondary h-7 px-2 text-xs">
                              Override…
                            </button>
                          )
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </PanelState>

      {!canApprove && Object.keys(pending).length > 0 && (
        <p className="mt-3 rounded-lg bg-amber-50 px-3 py-2 text-[11px] text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
          Pending overrides need a second person with approval rights to activate them (maker ≠ checker).
        </p>
      )}

      {/* Override modal */}
      <Modal
        isOpen={modalOpen}
        title={`Override ${baseRow?.ruleKey ?? ''}`}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              Cancel
            </button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">
              {saving ? 'Submitting…' : 'Submit for approval'}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        <p className="mb-3 flex items-start gap-1.5 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
          <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" />
          This does not change the statutory rate itself. It records a bounded, reason-backed, effective-dated override for this company only, and it takes effect after a second person approves it.
        </p>
        {draft && (
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Platform default">
                <input value={baseRow?.platformDefault ?? '—'} disabled className="input w-full opacity-60" />
              </Field>
              <Field label="Override value" required>
                <input value={draft.overrideValue} onChange={(e) => setDraft({ ...draft, overrideValue: e.target.value })} className="input w-full" placeholder="0.00" />
              </Field>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Effective from" required>
                <input type="date" value={draft.effectiveFrom} onChange={(e) => setDraft({ ...draft, effectiveFrom: e.target.value })} className="input w-full" />
              </Field>
              <Field label="Effective to" hint="Optional end date.">
                <input type="date" value={draft.effectiveTo} onChange={(e) => setDraft({ ...draft, effectiveTo: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <Field label="Review by" required hint="When this override must be re-justified or expires.">
              <input type="date" value={draft.reviewBy} onChange={(e) => setDraft({ ...draft, reviewBy: e.target.value })} className="input w-full" />
            </Field>
            <Field label="Reason" required hint="Why the statutory default is being overridden for this entity.">
              <textarea value={draft.reason} onChange={(e) => setDraft({ ...draft, reason: e.target.value })} className="input w-full" rows={3} placeholder="e.g. Free-zone entity with a bilateral social-insurance exemption per ruling ref…" />
            </Field>
          </div>
        )}
      </Modal>
    </div>
  );
}
