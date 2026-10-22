'use client';

import { useCallback, useMemo, useState } from 'react';
import { CalendarX2, Pencil, Plus } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import { financeRatesApi, type CompanyRate, type CompanyRateRequest, type RateRegistryEntry } from '../../api/financeRates';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { useAppToast } from '../ui/AppToast';
import { Modal } from '../Modal';
import {
  Badge,
  Field,
  FieldError,
  PanelState,
  rateCategoryLabel,
  serverMessage,
  StatusBadge,
  theadClass,
  thClass,
  todayIso,
  useConfirm,
  useScopedResource,
  type Translator,
} from './glUi';

interface Props {
  scope: string | null;
  scopeName: string;
  canManage: boolean; // payroll.rates.manage
  /** The company's operating currency, for amount rates the server does not label itself. */
  currency: string | null;
}

interface RateDraft {
  rateKey: string;
  rateValue: string;
  effectiveFrom: string;
  effectiveTo: string;
  notes: string;
}

const emptyDraft = (): RateDraft => ({ rateKey: '', rateValue: '', effectiveFrom: todayIso(), effectiveTo: '', notes: '' });

const day = (d: string | null | undefined) => (d ? d.slice(0, 10) : '');

function unitLabel(t: Translator, unit: string): string {
  switch (unit) {
    case 'percent': return t('Percent');
    case 'days': return t('Days');
    case 'hours': return t('Hours');
    case 'multiplier': return t('Multiplier');
    case 'amount': return t('Amount');
    default: return unit;
  }
}

interface Loaded {
  rates: CompanyRate[];
  registry: RateRegistryEntry[];
}

interface KeyGroup {
  rateKey: string;
  current: CompanyRate | null;
  scheduled: CompanyRate[];
  history: CompanyRate[];
}

export function CompanyRatesPanel({ scope, scopeName, canManage, currency }: Props) {
  const t = useT();
  const f = useFormat();
  const toast = useAppToast();
  const { confirm, dialog } = useConfirm();
  const inCompanyView = scope !== null;
  const today = todayIso();

  const fetcher = useCallback(async (): Promise<Loaded> => {
    const [rates, registry] = await Promise.all([financeRatesApi.listCompanyRates(scope), financeRatesApi.registry()]);
    return { rates, registry };
  }, [scope]);
  const res = useScopedResource<Loaded>(`rates:${scope ?? 'group'}`, fetcher);
  const rates = useMemo(() => res.data?.rates ?? [], [res.data]);
  const registry = useMemo(() => res.data?.registry ?? [], [res.data]);
  const writable = canManage && res.ready;

  const registryFor = useCallback((key: string) => registry.find((d) => d.rateKey === key), [registry]);
  // Recorded-only keys: saved by an earlier version, never read by payroll. The server refuses new
  // values for them (422 COMPANY_RATE_NOT_APPLIED); an older server that sends no flag counts as applied.
  const appliedRegistry = useMemo(() => registry.filter((d) => d.appliedByPayroll !== false), [registry]);
  const recordedOnlyRegistry = useMemo(() => registry.filter((d) => d.appliedByPayroll === false), [registry]);
  const isRecordedOnly = (r: { rateKey: string; appliedByPayroll?: boolean | null }) =>
    r.appliedByPayroll === false || (r.appliedByPayroll == null && registryFor(r.rateKey)?.appliedByPayroll === false);
  const nameFor = (r: { rateKey: string; label?: string | null }) => r.label || registryFor(r.rateKey)?.description || r.rateKey;

  const formatRate = (r: CompanyRate) => {
    const n = Number(r.rateValue);
    if (r.rateValue === '' || Number.isNaN(n)) return r.rateValue || '—';
    switch (r.unit) {
      case 'amount': return f.money(n, r.currency ?? currency ?? null);
      case 'percent': return t('{value}%', { value: f.number(n, { maximumFractionDigits: 4 }) });
      case 'multiplier': return t('{value}×', { value: f.number(n, { maximumFractionDigits: 4 }) });
      case 'days': return t('{count, plural, one {# day} other {# days}}', { count: n });
      case 'hours': return t('{count, plural, one {# hour} other {# hours}}', { count: n });
      default: return f.number(n, { maximumFractionDigits: 4 });
    }
  };

  const isOpenStatus = (r: CompanyRate) => r.status === 'Active' || r.status === 'PendingApproval';
  const ownedHere = (r: CompanyRate) => (inCompanyView ? r.companyId === scope : r.companyId === null);
  const coversToday = (r: CompanyRate) => day(r.effectiveFrom) <= today && (!r.effectiveTo || day(r.effectiveTo) >= today);
  const isFuture = (r: CompanyRate) => day(r.effectiveFrom) > today;

  // One group per key: the row payroll uses today (company beats inherited), future-dated rows,
  // and a history of superseded / archived / expired rows.
  const groups = useMemo<KeyGroup[]>(() => {
    const byKey = new Map<string, CompanyRate[]>();
    for (const r of rates) byKey.set(r.rateKey, [...(byKey.get(r.rateKey) ?? []), r]);
    const out: KeyGroup[] = [];
    for (const [rateKey, list] of byKey) {
      const sorted = [...list].sort((a, b) => (day(a.effectiveFrom) < day(b.effectiveFrom) ? 1 : -1));
      const flagged = sorted.filter((r) => r.inEffect === true);
      const candidates = flagged.length > 0 ? flagged : sorted.filter((r) => r.status === 'Active' && coversToday(r));
      const current = candidates.find((r) => r.companyId === scope) ?? candidates[0] ?? null;
      const scheduled = sorted.filter((r) => r !== current && isOpenStatus(r) && isFuture(r));
      const history = sorted.filter((r) => r !== current && !scheduled.includes(r));
      out.push({ rateKey, current, scheduled, history });
    }
    return out.sort((a, b) => nameFor({ rateKey: a.rateKey }).localeCompare(nameFor({ rateKey: b.rateKey })));
    // nameFor depends on the registry only, which is in the dependency list.
  }, [rates, registry, scope, today]);

  // ── Create / change ────────────────────────────────────────────────────────
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CompanyRate | null>(null);
  const [draft, setDraft] = useState<RateDraft>(emptyDraft());
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const selectedDef = registryFor(draft.rateKey);

  const openCreate = () => {
    setEditing(null);
    setDraft({ ...emptyDraft(), rateKey: appliedRegistry[0]?.rateKey ?? '' });
    setError('');
    setModalOpen(true);
  };

  const openEdit = (r: CompanyRate) => {
    setEditing(r);
    setDraft({ rateKey: r.rateKey, rateValue: r.rateValue, effectiveFrom: day(r.effectiveFrom), effectiveTo: day(r.effectiveTo), notes: r.notes ?? '' });
    setError('');
    setModalOpen(true);
  };

  const validate = (): string | null => {
    if (!draft.rateKey) return t('Choose a rate.');
    if (!draft.rateValue.trim()) return t('Enter a value.');
    if (!draft.effectiveFrom) return t('Enter the date the rate takes effect.');
    const def = registryFor(draft.rateKey);
    if (def && def.dataType === 'decimal') {
      const v = Number(draft.rateValue);
      if (Number.isNaN(v)) return t('The value must be a number.');
      if (def.minValue != null && v < def.minValue) return t('The value must be at least {min}.', { min: def.minValue });
      if (def.maxValue != null && v > def.maxValue) return t('The value must be at most {max}.', { max: def.maxValue });
    }
    if (draft.effectiveTo && draft.effectiveTo < draft.effectiveFrom) return t('The end date cannot be before the start date.');
    return null;
  };

  const save = async () => {
    const v = validate();
    if (v) {
      setError(v);
      return;
    }
    setSaving(true);
    setError('');
    const body: CompanyRateRequest = {
      // The key is immutable on edit: always send the row's own key.
      rateKey: editing ? editing.rateKey : draft.rateKey,
      rateValue: draft.rateValue.trim(),
      effectiveFrom: draft.effectiveFrom,
      effectiveTo: draft.effectiveTo || null,
      notes: draft.notes || null,
    };
    try {
      if (editing) await financeRatesApi.updateCompanyRate(editing.id, body);
      else await financeRatesApi.createCompanyRate(body, scope);
      setModalOpen(false);
      toast.success(editing ? t('Rate changed. The previous value is kept in its history.') : t('Rate added.'));
      await res.reload();
    } catch (err) {
      setError(serverMessage(err, t));
    } finally {
      setSaving(false);
    }
  };

  // ── End-date (rows already in effect) ──────────────────────────────────────
  const [ending, setEnding] = useState<CompanyRate | null>(null);
  const [endDate, setEndDate] = useState('');
  const [endError, setEndError] = useState('');
  const [endSaving, setEndSaving] = useState(false);

  const openEnd = (r: CompanyRate) => {
    setEnding(r);
    setEndDate(todayIso());
    setEndError('');
  };

  const saveEnd = async () => {
    if (!ending) return;
    if (!endDate) return setEndError(t('Enter the last day the rate applies.'));
    if (endDate < day(ending.effectiveFrom)) return setEndError(t('The end date cannot be before the start date.'));
    setEndSaving(true);
    setEndError('');
    try {
      await financeRatesApi.updateCompanyRate(ending.id, {
        rateKey: ending.rateKey,
        rateValue: ending.rateValue,
        effectiveFrom: day(ending.effectiveFrom),
        effectiveTo: endDate,
        notes: ending.notes || null,
      });
      setEnding(null);
      toast.success(t('Rate end-dated.'));
      await res.reload();
    } catch (err) {
      setEndError(serverMessage(err, t));
    } finally {
      setEndSaving(false);
    }
  };

  // ── Delete (future-dated rows only; the server refuses past-dated rows with 409) ──
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const remove = async (r: CompanyRate) => {
    const ok = await confirm({
      title: t('Delete scheduled rate?'),
      message: t('{name} at {value} from {date} has not taken effect yet. Deleting it removes it completely.', {
        name: nameFor(r),
        value: formatRate(r),
        date: f.date(r.effectiveFrom),
      }),
      confirmLabel: t('Delete'),
      tone: 'danger',
    });
    if (!ok) return;
    setDeletingId(r.id);
    try {
      await financeRatesApi.deleteCompanyRate(r.id);
      toast.success(t('Scheduled rate deleted.'));
      await res.reload();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setDeletingId(null);
    }
  };

  const sourceBadge = (r: CompanyRate) =>
    inCompanyView ? (
      r.companyId === null ? <Badge tone="slate">{t('Inherited from group')}</Badge> : <Badge tone="violet">{t('Company')}</Badge>
    ) : (
      <Badge tone="sapphire">{t('Group')}</Badge>
    );

  const period = (r: CompanyRate) =>
    r.effectiveTo
      ? t('{from} to {to}', { from: f.date(r.effectiveFrom), to: f.date(r.effectiveTo) })
      : t('From {from}, open-ended', { from: f.date(r.effectiveFrom) });

  const actions = (r: CompanyRate) => {
    if (!canManage || !ownedHere(r) || !isOpenStatus(r)) return <div className="text-end text-[11px] text-slate-400" aria-hidden>—</div>;
    return (
      <div className="flex flex-wrap items-center justify-end gap-1.5">
        {/* A recorded-only rate can only be cleaned up: a new value would change nobody's pay. */}
        {!isRecordedOnly(r) && (
          <button type="button" onClick={() => openEdit(r)} disabled={!writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
            <Pencil className="h-3 w-3" aria-hidden /> {t('Change value')}
          </button>
        )}
        {isFuture(r) ? (
          <button
            type="button"
            onClick={() => remove(r)}
            disabled={!writable || deletingId === r.id}
            className="h-7 rounded-md border border-slate-200 px-2 text-xs font-medium text-rose-600 hover:border-rose-300 hover:bg-rose-50 disabled:opacity-40 dark:border-white/10 dark:text-rose-400"
          >
            {t('Delete')}
          </button>
        ) : (
          <button type="button" onClick={() => openEnd(r)} disabled={!writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
            <CalendarX2 className="h-3 w-3" aria-hidden /> {t('End-date')}
          </button>
        )}
      </div>
    );
  };

  return (
    <div className="surface p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 id="gl-company-rates-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Company Rates')}</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Client-set, non-statutory rates for {scope}: custom allowances, deductions, pay parameters and end-of-service above the legal minimum. A change starts a new dated row; the old value stays in its history.', { scope: scopeName })}
          </p>
        </div>
        {canManage && (
          <button type="button" onClick={openCreate} disabled={!writable || appliedRegistry.length === 0} className="btn-primary text-xs disabled:opacity-60">
            <Plus className="h-3.5 w-3.5" aria-hidden /> {t('Add rate')}
          </button>
        )}
      </div>

      <PanelState
        loading={res.loading && !res.data}
        error={res.error}
        onRetry={() => void res.reload()}
        empty={groups.length === 0}
        emptyLabel={t('No company rates are set yet.')}
      >
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10 outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="gl-company-rates-heading">
          <table className="w-full min-w-[760px] text-sm">
            <thead className={theadClass}>
              <tr>
                <th scope="col" className={thClass}>{t('Rate')}</th>
                <th scope="col" className={thClass}>{t('Category')}</th>
                <th scope="col" className={`${thClass} text-end`}>{t('In effect today')}</th>
                <th scope="col" className={thClass}>{t('Effective')}</th>
                <th scope="col" className={thClass}>{t('Source')}</th>
                <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
              </tr>
            </thead>
            {groups.map((g) => {
              const head = g.current ?? g.scheduled[g.scheduled.length - 1] ?? g.history[0];
              const def = registryFor(g.rateKey);
              const recordedOnly = head ? isRecordedOnly(head) : def?.appliedByPayroll === false;
              return (
                <tbody key={g.rateKey} className="border-t border-slate-200 dark:border-white/10">
                  <tr data-rate-key={g.rateKey}>
                    <td className="px-3 py-2 align-top">
                      <p className="text-slate-800 dark:text-slate-100">{nameFor({ rateKey: g.rateKey, label: head?.label })}</p>
                      <p className="text-[11px] text-slate-500"><span className="font-mono">{g.rateKey}</span></p>
                      {g.current?.notes && <p className="text-[11px] text-slate-500">{g.current.notes}</p>}
                      {recordedOnly && (
                        <div className="mt-1 space-y-0.5">
                          <Badge tone="amber">{t('Recorded only — payroll does not use this')}</Badge>
                          <p className="max-w-md text-[11px] text-amber-800 dark:text-amber-300">
                            {t('Payroll does not read this value, so it changes nobody’s pay. End-date it to tidy up. Set allowances and deductions per grade under Benefits → Grade benefits, or on the employee’s pay package.')}
                          </p>
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2 align-top text-xs text-slate-600 dark:text-slate-300">
                      {rateCategoryLabel(t, head?.rateCategory ?? def?.rateCategory ?? '')}
                    </td>
                    <td className="px-3 py-2 text-end align-top font-medium tabular-nums">
                      {g.current ? formatRate(g.current) : <span className="text-xs font-normal text-slate-500">{t('Not in effect today')}</span>}
                    </td>
                    <td className="px-3 py-2 align-top text-xs text-slate-600 dark:text-slate-300">{g.current ? period(g.current) : '—'}</td>
                    <td className="px-3 py-2 align-top">
                      {g.current && (
                        <div className="flex flex-wrap gap-1">
                          {sourceBadge(g.current)}
                          {g.current.status !== 'Active' && <StatusBadge status={g.current.status} />}
                        </div>
                      )}
                    </td>
                    <td className="px-3 py-2 align-top">{g.current ? actions(g.current) : null}</td>
                  </tr>
                  {g.scheduled.map((r) => (
                    <tr key={r.id} className="bg-sapphire/[0.03]">
                      <td className="px-3 py-1.5 ps-6 text-xs text-slate-600 dark:text-slate-300">{t('Scheduled')}</td>
                      <td />
                      <td className="px-3 py-1.5 text-end text-xs tabular-nums">{formatRate(r)}</td>
                      <td className="px-3 py-1.5 text-xs text-slate-600 dark:text-slate-300">{period(r)}</td>
                      <td className="px-3 py-1.5">
                        <div className="flex flex-wrap gap-1">
                          {sourceBadge(r)}
                          {r.status !== 'Active' && <StatusBadge status={r.status} />}
                        </div>
                      </td>
                      <td className="px-3 py-1.5">{actions(r)}</td>
                    </tr>
                  ))}
                  {g.history.length > 0 && (
                    <tr>
                      <td colSpan={6} className="px-3 pb-2">
                        <details className="group text-xs">
                          <summary className="cursor-pointer select-none text-slate-500 hover:text-slate-700 dark:hover:text-slate-200">
                            {t('{count, plural, one {History (# earlier value)} other {History (# earlier values)}}', { count: g.history.length })}
                          </summary>
                          <table className="mt-1.5 w-full">
                            <caption className="sr-only">{t('Earlier values of {name}', { name: nameFor({ rateKey: g.rateKey }) })}</caption>
                            <thead className="text-[11px] text-slate-500">
                              <tr>
                                <th scope="col" className="px-2 py-1 text-start font-medium">{t('Value')}</th>
                                <th scope="col" className="px-2 py-1 text-start font-medium">{t('Effective')}</th>
                                <th scope="col" className="px-2 py-1 text-start font-medium">{t('Status')}</th>
                                <th scope="col" className="px-2 py-1 text-start font-medium">{t('Notes')}</th>
                              </tr>
                            </thead>
                            <tbody>
                              {g.history.map((r) => (
                                <tr key={r.id} className="border-t border-slate-100 text-slate-600 dark:border-white/5 dark:text-slate-300">
                                  <td className="px-2 py-1 tabular-nums">{formatRate(r)}</td>
                                  <td className="px-2 py-1">{period(r)}</td>
                                  <td className="px-2 py-1">
                                    <StatusBadge status={r.status === 'Active' && r.effectiveTo && day(r.effectiveTo) < today ? 'Archived' : r.status} />
                                  </td>
                                  <td className="px-2 py-1">{r.notes || '—'}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </details>
                      </td>
                    </tr>
                  )}
                </tbody>
              );
            })}
          </table>
        </div>
      </PanelState>

      {/* Create / change modal */}
      <Modal
        isOpen={modalOpen}
        title={editing ? t('Change {name}', { name: nameFor(editing) }) : t('Add company rate')}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button type="button" onClick={save} disabled={saving || !writable} className="btn-primary disabled:opacity-60">
              {saving ? t('Saving…') : editing ? t('Save new value') : t('Save')}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        {editing && (
          <p className="mb-3 rounded-lg bg-sapphire/10 px-3 py-2 text-xs text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            {t('A new value starts a new dated row. The current row closes the day before, so payroll already run keeps its original value.')}
          </p>
        )}
        <div className="space-y-3">
          <Field label={t('Rate')} required hint={editing ? t('The rate itself cannot be changed. Add a new rate instead.') : selectedDef?.description || undefined}>
            <select
              value={draft.rateKey}
              disabled={!!editing}
              onChange={(e) => setDraft((d) => ({ ...d, rateKey: e.target.value }))}
              className="select w-full disabled:opacity-60"
            >
              <option value="">{t('Choose a rate…')}</option>
              {(editing ? registry : appliedRegistry).map((d) => (
                <option key={d.rateKey} value={d.rateKey}>
                  {d.description ? `${d.description} (${d.rateKey})` : d.rateKey}
                </option>
              ))}
              {!editing && recordedOnlyRegistry.length > 0 && (
                <optgroup label={t('Recorded only — payroll does not use these')}>
                  {recordedOnlyRegistry.map((d) => (
                    <option key={d.rateKey} value={d.rateKey} disabled>
                      {d.description ? `${d.description} (${d.rateKey})` : d.rateKey}
                    </option>
                  ))}
                </optgroup>
              )}
            </select>
          </Field>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Field
              label={t('Value')}
              required
              hint={
                selectedDef
                  ? selectedDef.minValue != null && selectedDef.maxValue != null
                    ? t('Unit {unit}, between {min} and {max}', { unit: unitLabel(t, selectedDef.unit), min: selectedDef.minValue, max: selectedDef.maxValue })
                    : t('Unit {unit}', { unit: unitLabel(t, selectedDef.unit) })
                  : undefined
              }
            >
              <input
                type={selectedDef?.dataType === 'decimal' ? 'number' : 'text'}
                step="any"
                inputMode="decimal"
                min={selectedDef?.minValue ?? undefined}
                max={selectedDef?.maxValue ?? undefined}
                value={draft.rateValue}
                onChange={(e) => setDraft((d) => ({ ...d, rateValue: e.target.value }))}
                className="input w-full"
                placeholder="0"
              />
            </Field>
            <Field label={t('Category')}>
              <input value={rateCategoryLabel(t, selectedDef?.rateCategory ?? '')} readOnly className="input w-full opacity-70" />
            </Field>
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Field label={t('Effective from')} required>
              <input type="date" value={draft.effectiveFrom} onChange={(e) => setDraft((d) => ({ ...d, effectiveFrom: e.target.value }))} className="input w-full" />
            </Field>
            <Field label={t('Effective to')} hint={t('Leave blank for open-ended.')}>
              <input type="date" value={draft.effectiveTo} onChange={(e) => setDraft((d) => ({ ...d, effectiveTo: e.target.value }))} className="input w-full" />
            </Field>
          </div>

          <Field label={t('Notes')}>
            <textarea value={draft.notes} onChange={(e) => setDraft((d) => ({ ...d, notes: e.target.value }))} className="input w-full" rows={2} placeholder={t('Optional context for the record')} />
          </Field>
        </div>
      </Modal>

      {/* End-date modal */}
      <Modal
        isOpen={!!ending}
        title={t('End-date {name}', { name: ending ? nameFor(ending) : '' })}
        onClose={() => setEnding(null)}
        size="sm"
        footer={
          <>
            <button type="button" onClick={() => setEnding(null)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button type="button" onClick={saveEnd} disabled={endSaving || !writable} className="btn-primary disabled:opacity-60">
              {endSaving ? t('Saving…') : t('End-date')}
            </button>
          </>
        }
      >
        <FieldError error={endError} />
        <p className="mb-3 text-xs text-slate-600 dark:text-slate-300">
          {t('This rate has already been in effect, so it is kept for payroll history. Choose the last day it applies.')}
        </p>
        <Field label={t('Last day in effect')} required>
          <input type="date" value={endDate} min={ending ? day(ending.effectiveFrom) : undefined} onChange={(e) => setEndDate(e.target.value)} className="input w-full" />
        </Field>
      </Modal>

      {!canManage && <p className="mt-3 text-[11px] text-slate-500">{t('Read-only. You do not have permission to edit company rates.')}</p>}
      {canManage && res.ready && registry.length > 0 && appliedRegistry.length === 0 && (
        <p className="mt-3 text-[11px] text-slate-500">{t('None of the registered rates is used by payroll, so there is nothing to add here.')}</p>
      )}
      {canManage && res.ready && registry.length === 0 && (
        <p className="mt-3 text-[11px] text-slate-500">{t('No client-configurable rates are registered. Load the standard payroll chart from the Accounts & Mapping tab.')}</p>
      )}
      {dialog}
    </div>
  );
}
