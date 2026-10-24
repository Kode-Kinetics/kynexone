'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { Archive, CalendarClock, Hourglass, PencilLine, Plus, RefreshCw, Scale, Search, ShieldCheck, X } from 'lucide-react';
import { countryPacksApi, statutoryRulesApi } from '../api/countryPacks';
import type { CountryPackOption, CreateStatutoryRuleRequest, StatutoryPendingRequest, StatutoryRuleDto, StatutoryRuleWriteResult } from '../api/countryPacks';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import { msg } from '../i18n/translations';
import {
  choiceOptions, describeStatutoryChange, formatStatutoryValue, inputHint, inputSuffix, parseInputValue,
  statutoryRuleName, statutoryUnitFor, statutoryUnitName, toInputValue,
  type StatutoryValueDisplay,
} from '../lib/statutoryRuleLabels';
import { Modal } from './Modal';
import { useSetupEditorState } from './setup/SetupEditorState';

type ScopeFilter = 'all' | 'default' | 'override';
type RuleStatus = 'Active' | 'Scheduled' | 'Retired';

const today = () => new Date().toISOString().slice(0, 10);
const tomorrow = () => {
  const value = new Date();
  value.setUTCDate(value.getUTCDate() + 1);
  return value.toISOString().slice(0, 10);
};

const emptyForm = (): CreateStatutoryRuleRequest => ({
  countryCode: '', jurisdiction: '', ruleKey: '', ruleValue: '', dataType: 'decimal',
  description: '', effectiveFrom: tomorrow(), effectiveTo: null,
});

const errorMessage = (error: unknown, fallback: string) => {
  const body = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof body === 'string' && body.trim()) return body;
  if (body && typeof body === 'object' && 'message' in body && typeof body.message === 'string') return body.message;
  return fallback;
};

const statusFor = (rule: StatutoryRuleDto): RuleStatus => {
  const now = today();
  if (rule.effectiveFrom.slice(0, 10) > now) return msg('Scheduled');
  if (rule.effectiveTo && rule.effectiveTo.slice(0, 10) <= now) return msg('Retired');
  return msg('Active');
};

const ruleIdentity = (row: { countryCode: string; jurisdiction: string; ruleKey: string }) =>
  `${row.countryCode}|${row.jurisdiction}|${row.ruleKey}`;

/** The row that decides a rule's value on `day`: the company's value when one is in force, else the published rate. */
const ruleInForce = (rows: StatutoryRuleDto[], identity: string, day: string, publishedOnly = false) => {
  const live = rows
    .filter((row) => ruleIdentity(row) === identity && row.effectiveFrom.slice(0, 10) <= day && (!row.effectiveTo || row.effectiveTo.slice(0, 10) > day))
    .sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom));
  return (publishedOnly ? undefined : live.find((row) => row.isTenantOverride)) ?? live.find((row) => !row.isTenantOverride) ?? null;
};

/** A value with its unit, plus the warning that must travel with it when the unit is not known. */
function UnitValue({ display, className = '' }: { display: StatutoryValueDisplay; className?: string }) {
  return (
    <span className={`inline-flex flex-wrap items-center gap-1.5 ${className}`}>
      <bdi className="tabular-nums">{display.text}</bdi>
      {display.flag && <span className="whitespace-nowrap rounded-full bg-amber-100 px-2 py-0.5 text-[11px] rtl:text-[13px] font-semibold text-amber-800 dark:bg-amber-400/15 dark:text-amber-300">{display.flag}</span>}
    </span>
  );
}

const isPlatformManaged = (key: string) =>
  ['gosi.', 'saned.', 'gpssa.', 'grsia.', 'dews.', 'eosb.', 'leave.', 'workhours.'].some((prefix) => key.toLowerCase().startsWith(prefix))
  || ['nitaqat.default_target_ratio', 'emiratisation.target_ratio', 'emiratization.target_ratio', 'qatarization.target_ratio'].includes(key.toLowerCase());

export function StatutoryRulesPanel() {
  const t = useT();
  const { locale } = useLocale();
  const format = useFormat();
  // Effective dates are calendar boundaries; preserve their UTC day in every viewer's zone.
  const dateLabel = (value?: string | null) => value ? format.date(value.slice(0, 10)) : t('Open-ended');
  const { hasPermission, user } = useAuth();
  const canManage = hasPermission('payroll.rates.statutory_override');
  // GET /api/finance/rates/statutory/pending is readable with any of these (RatesController.CanReadStatutory).
  const canSeePending = canManage || hasPermission('payroll.rates.read') || hasPermission('payroll.rates.manage');
  const [rows, setRows] = useState<StatutoryRuleDto[]>([]);
  const [packs, setPacks] = useState<CountryPackOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [country, setCountry] = useState('');
  const [jurisdiction, setJurisdiction] = useState('');
  const [scope, setScope] = useState<ScopeFilter>('all');
  const [query, setQuery] = useState('');
  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<StatutoryRuleDto | null>(null);
  const [form, setForm] = useState<CreateStatutoryRuleRequest>(emptyForm);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState('');
  const [retiring, setRetiring] = useState<StatutoryRuleDto | null>(null);
  const [retireBusy, setRetireBusy] = useState(false);
  // Every write is a maker-checker REQUEST (202): the table only ever shows what the server says
  // payroll uses, and requests waiting for a second administrator are listed separately.
  const [pending, setPending] = useState<StatutoryPendingRequest[]>([]);
  const [pendingError, setPendingError] = useState('');
  const [notice, setNotice] = useState('');
  const [deciding, setDeciding] = useState<{ request: StatutoryPendingRequest; action: 'approve' | 'reject' } | null>(null);
  const [rejectReason, setRejectReason] = useState('');
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [decisionError, setDecisionError] = useState('');
  const [withdrawingId, setWithdrawingId] = useState<string | null>(null);
  useSetupEditorState(formOpen || (deciding?.action === 'reject' && rejectReason.length > 0));

  const loadPending = useCallback(async () => {
    if (!canSeePending) return;
    setPendingError('');
    try {
      setPending(await statutoryRulesApi.pending());
    } catch (error) {
      setPendingError(errorMessage(error, t('Pending changes could not be loaded.')));
    }
  }, [canSeePending, t]);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError('');
    try {
      const [ruleRows, countryPacks] = await Promise.all([statutoryRulesApi.list(), countryPacksApi.available(), loadPending()]);
      setRows(ruleRows);
      setPacks(countryPacks);
    } catch (error) {
      setLoadError(errorMessage(error, t('Statutory rules could not be loaded.')));
    } finally {
      setLoading(false);
    }
  }, [loadPending, t]);

  const pendingByRule = useMemo(() => new Map(pending.map((request) => [ruleIdentity(request), request])), [pending]);

  // A pending request carries no data type; the published row for the same rule does.
  const dataTypeByRule = useMemo(() => {
    const types = new Map<string, string>();
    rows.forEach((row) => { if (!row.isTenantOverride || !types.has(ruleIdentity(row))) types.set(ruleIdentity(row), row.dataType); });
    return types;
  }, [rows]);
  const unitOf = (row: { countryCode: string; jurisdiction: string; ruleKey: string; dataType?: string }) =>
    statutoryUnitFor(row.ruleKey, row.dataType ?? dataTypeByRule.get(ruleIdentity(row)));
  const show = (row: { countryCode: string; jurisdiction: string; ruleKey: string; dataType?: string }, raw: string | null | undefined) =>
    formatStatutoryValue(t, format, unitOf(row), raw);
  const nameOf = (ruleKey: string) => statutoryRuleName(t, ruleKey);

  /** A 202 means "filed, not applied": say so, and leave the table exactly as the server reports it. */
  const afterWrite = async (result: StatutoryRuleWriteResult) => {
    if (result.outcome === 'pending') setNotice(t('Sent for approval — a second administrator must approve this before payroll uses it.'));
    await load();
  };

  useEffect(() => { void load(); }, [load]);

  const jurisdictions = useMemo(
    () => packs.find((pack) => pack.countryCode === country)?.jurisdictions ?? [],
    [country, packs],
  );

  const catalogue = useMemo(() => {
    const latest = new Map<string, StatutoryRuleDto>();
    rows.filter((row) => !row.isTenantOverride).forEach((row) => {
      const key = `${row.countryCode}|${row.jurisdiction}|${row.ruleKey}`;
      const previous = latest.get(key);
      if (!previous || previous.effectiveFrom < row.effectiveFrom) latest.set(key, row);
    });
    return [...latest.values()];
  }, [rows]);

  const formCatalogue = useMemo(
    () => catalogue
      .filter((row) => row.countryCode === form.countryCode && row.jurisdiction === form.jurisdiction)
      .sort((a, b) => a.ruleKey.localeCompare(b.ruleKey)),
    [catalogue, form.countryCode, form.jurisdiction],
  );

  const visibleRows = useMemo(() => rows.filter((row) => {
    if (country && row.countryCode !== country) return false;
    if (jurisdiction && row.jurisdiction !== jurisdiction) return false;
    if (scope === 'default' && row.isTenantOverride) return false;
    if (scope === 'override' && !row.isTenantOverride) return false;
    const needle = query.trim().toLowerCase();
    return !needle || `${statutoryRuleName(t, row.ruleKey)} ${row.ruleKey} ${row.description} ${row.ruleValue}`.toLowerCase().includes(needle);
  }), [country, jurisdiction, query, rows, scope, t]);

  const metrics = useMemo(() => ({
    defaults: rows.filter((row) => !row.isTenantOverride && statusFor(row) === 'Active').length,
    overrides: rows.filter((row) => row.isTenantOverride && statusFor(row) === 'Active').length,
    scheduled: rows.filter((row) => statusFor(row) === 'Scheduled').length,
  }), [rows]);

  const openCreate = () => {
    const firstPack = packs.find((pack) => !country || pack.countryCode === country) ?? packs[0];
    const firstJurisdiction = firstPack?.jurisdictions.find((item) => !jurisdiction || item.code === jurisdiction)
      ?? firstPack?.jurisdictions[0];
    setEditing(null);
    setForm({ ...emptyForm(), countryCode: firstPack?.countryCode ?? '', jurisdiction: firstJurisdiction?.code ?? '' });
    setFormError('');
    setFormOpen(true);
  };

  const openSupersede = (rule: StatutoryRuleDto) => {
    setEditing(rule);
    setForm({
      countryCode: rule.countryCode, jurisdiction: rule.jurisdiction, ruleKey: rule.ruleKey,
      // The form holds what the person types (a rate as a percentage); save() converts it back.
      ruleValue: toInputValue(statutoryUnitFor(rule.ruleKey, rule.dataType), rule.ruleValue), dataType: rule.dataType, description: '',
      effectiveFrom: tomorrow(), effectiveTo: null,
    });
    setFormError('');
    setFormOpen(true);
  };

  const selectRule = (ruleKey: string) => {
    const selected = formCatalogue.find((row) => row.ruleKey === ruleKey);
    setForm((current) => ({ ...current, ruleKey, dataType: selected?.dataType ?? current.dataType, ruleValue: '' }));
  };

  const save = async () => {
    setFormError('');
    if (!form.ruleKey || !form.ruleValue.trim() || !form.description.trim()) {
      setFormError(t('Choose a rule, enter its value, and provide the approved reason or source.'));
      return;
    }
    const parsed = parseInputValue(t, statutoryUnitFor(form.ruleKey, form.dataType), form.ruleValue);
    if (!parsed.ok) {
      setFormError(parsed.error);
      return;
    }
    if (form.effectiveTo && form.effectiveTo <= form.effectiveFrom.slice(0, 10)) {
      setFormError(t('Effective until must be later than Effective from.'));
      return;
    }
    setSaving(true);
    setNotice('');
    try {
      let result: StatutoryRuleWriteResult;
      if (editing) {
        result = await statutoryRulesApi.update(editing.id, {
          ruleValue: parsed.stored, description: form.description.trim(),
          effectiveFrom: new Date(form.effectiveFrom).toISOString(),
          effectiveTo: form.effectiveTo ? new Date(form.effectiveTo).toISOString() : null,
        });
      } else {
        result = await statutoryRulesApi.create({
          ...form, ruleValue: parsed.stored, description: form.description.trim(),
          effectiveFrom: new Date(form.effectiveFrom).toISOString(),
          effectiveTo: form.effectiveTo ? new Date(form.effectiveTo).toISOString() : null,
        });
      }
      setFormOpen(false);
      await afterWrite(result);
    } catch (error) {
      setFormError(errorMessage(error, t('The statutory override could not be saved.')));
    } finally {
      setSaving(false);
    }
  };

  const retire = async () => {
    if (!retiring) return;
    setRetireBusy(true);
    setFormError('');
    setNotice('');
    try {
      const result = await statutoryRulesApi.remove(retiring.id);
      setRetiring(null);
      await afterWrite(result);
    } catch (error) {
      setFormError(errorMessage(error, t('The override could not be retired.')));
      setRetiring(null);
    } finally {
      setRetireBusy(false);
    }
  };

  const openDecision = (request: StatutoryPendingRequest, action: 'approve' | 'reject') => {
    setDeciding({ request, action });
    setRejectReason('');
    setDecisionError('');
  };

  const decide = async () => {
    if (!deciding) return;
    setDecisionBusy(true);
    setDecisionError('');
    try {
      if (deciding.action === 'approve') await statutoryRulesApi.approve(deciding.request.id);
      else await statutoryRulesApi.reject(deciding.request.id, rejectReason.trim());
      setNotice(deciding.action === 'approve'
        ? t('Change approved. Payroll uses it from its start date.')
        : t('Change rejected. Nothing changed in payroll.'));
      setDeciding(null);
      await load();
    } catch (error) {
      setDecisionError(errorMessage(error, t('The decision could not be saved.')));
    } finally {
      setDecisionBusy(false);
    }
  };

  const withdraw = async (request: StatutoryPendingRequest) => {
    setWithdrawingId(request.id);
    setFormError('');
    try {
      await statutoryRulesApi.withdraw(request.id);
      setNotice(t('Request withdrawn. Nothing changed in payroll.'));
      await load();
    } catch (error) {
      setFormError(errorMessage(error, t('The decision could not be saved.')));
    } finally {
      setWithdrawingId(null);
    }
  };

  // What the approver signs: the value payroll uses on the start date, and the value it will use after.
  // A retirement carries the live company value in `value`; payroll then returns to the published rate.
  const requestValues = (request: StatutoryPendingRequest) => {
    const identity = ruleIdentity(request);
    const day = request.effectiveFrom.slice(0, 10);
    if (request.kind === 'retire') {
      const published = ruleInForce(rows, identity, day, true)?.ruleValue ?? request.platformDefault;
      return { old: show(request, request.value), next: published ? show(request, published) : null };
    }
    const prior = ruleInForce(rows, identity, day)?.ruleValue ?? request.platformDefault;
    return { old: prior ? show(request, prior) : null, next: show(request, request.value) };
  };
  const describeRequest = (request: StatutoryPendingRequest) => {
    const { old, next } = requestValues(request);
    return describeStatutoryChange(t, {
      kind: request.kind, label: nameOf(request.ruleKey), oldText: old?.text ?? null, newText: next?.text ?? null, date: dateLabel(request.effectiveFrom),
    });
  };
  const requestFlags = (request: StatutoryPendingRequest) => {
    const { old, next } = requestValues(request);
    return [...new Set([old?.flag, next?.flag].filter((flag): flag is string => !!flag))];
  };
  const flagBadges = (flags: string[]) => flags.map((flag) => (
    <span key={flag} className="ms-2 inline-block whitespace-nowrap rounded-full bg-amber-100 px-2 py-0.5 text-[11px] rtl:text-[13px] font-semibold text-amber-800 dark:bg-amber-400/15 dark:text-amber-300">{flag}</span>
  ));

  const selectedFormRule = formCatalogue.find((row) => row.ruleKey === form.ruleKey);
  const formUnit = statutoryUnitFor(form.ruleKey, form.dataType);
  const formSuffix = inputSuffix(t, formUnit);
  const formChoices = choiceOptions(t, formUnit);
  const typedPreview = form.ruleKey && form.ruleValue.trim() ? parseInputValue(t, formUnit, form.ruleValue) : null;
  const editingCurrent = editing ? show(editing, editing.ruleValue) : null;
  const publishedDisplay = selectedFormRule ? show(selectedFormRule, selectedFormRule.ruleValue) : null;

  return (
    <div className="space-y-4">
      <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto]">
        <div className="rounded-xl border border-blue-200 bg-blue-50/70 p-4 dark:border-blue-400/20 dark:bg-blue-400/10">
          <div className="flex items-start gap-3">
            <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
            <div>
              <h3 className="text-sm font-semibold text-slate-950 dark:text-white">{t('Tenant-wide statutory rules')}</h3>
              <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-300">
                {t('Review platform-maintained rules and governed tenant overrides used by payroll and compliance.')} {t('Company-specific rates are managed separately.')} <Link href="/setup?tab=glMapping" className="font-semibold text-sapphire hover:underline">{t('Payroll accounting')}</Link>
              </p>
            </div>
          </div>
        </div>
        {canManage && (
          <button type="button" onClick={openCreate} className="btn-primary self-start">
            <Plus className="h-4 w-4" />{t('New override')}
          </button>
        )}
      </div>

      <div className="grid grid-cols-3 gap-2 sm:gap-3">
        {[
          { label: msg('Published rates'), value: metrics.defaults, icon: Scale },
          { label: msg("Your company's values in force"), value: metrics.overrides, icon: ShieldCheck },
          { label: msg('Scheduled changes'), value: metrics.scheduled, icon: CalendarClock },
        ].map(({ label, value, icon: Icon }) => (
          <div key={label} className="surface flex min-w-0 items-center gap-2 p-3">
            <Icon className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
            <div className="min-w-0"><p className="truncate text-xs text-slate-500 dark:text-slate-400">{t(label)}</p><p className="text-lg font-bold text-slate-950 dark:text-white">{value}</p></div>
          </div>
        ))}
      </div>

      <div className="surface p-3">
        <div className="grid gap-2 md:grid-cols-[1fr_1fr_1.4fr_auto]">
          <select aria-label={t('Filter by country pack')} value={country} onChange={(event) => { setCountry(event.target.value); setJurisdiction(''); }} className="input-field">
            <option value="">{t('All country packs')}</option>
            {packs.map((pack) => <option key={pack.countryCode} value={pack.countryCode}>{locale === 'ar' ? pack.nameAr || pack.nameEn : pack.nameEn}</option>)}
          </select>
          <select aria-label={t('Filter by jurisdiction')} value={jurisdiction} onChange={(event) => setJurisdiction(event.target.value)} className="input-field" disabled={!country}>
            <option value="">{t('All jurisdictions')}</option>
            {jurisdictions.map((item) => <option key={item.code} value={item.code}>{t(item.label)}</option>)}
          </select>
          <label className="relative block">
            <Search className="pointer-events-none absolute start-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input aria-label={t('Search statutory rules')} value={query} onChange={(event) => setQuery(event.target.value)} placeholder={t('Search rule, source or value')} className="input-field ps-9" />
          </label>
          <select aria-label={t('Filter by rule scope')} value={scope} onChange={(event) => setScope(event.target.value as ScopeFilter)} className="input-field">
            <option value="all">{t('All scopes')}</option><option value="default">{t('Published rates')}</option><option value="override">{t("Your company's values")}</option>
          </select>
        </div>
      </div>

      {notice && (
        <div role="status" className="flex items-start justify-between gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:border-emerald-400/20 dark:bg-emerald-400/10 dark:text-emerald-200">
          <span>{notice}</span>
          <button type="button" onClick={() => setNotice('')} className="shrink-0 text-xs font-semibold hover:underline" aria-label={t('Close')}><X className="h-3.5 w-3.5" aria-hidden /></button>
        </div>
      )}

      {canSeePending && (pending.length > 0 || pendingError) && (
        <section aria-labelledby="statutory-pending-heading" className="rounded-xl border border-amber-200 bg-amber-50/60 p-3 dark:border-amber-400/20 dark:bg-amber-400/[0.06]">
          <div className="flex items-start gap-2">
            <Hourglass className="mt-0.5 h-4 w-4 shrink-0 text-amber-700 dark:text-amber-300" aria-hidden />
            <div className="min-w-0">
              <h3 id="statutory-pending-heading" className="text-sm font-semibold text-slate-950 dark:text-white">{t('Changes waiting for a second administrator')}</h3>
              <p className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-300">{t('Payroll keeps using the current values until a second administrator approves. The person who asked cannot approve their own request.')}</p>
            </div>
          </div>
          {pendingError && <p role="alert" className="mt-2 text-xs text-red-700 dark:text-red-300">{pendingError}</p>}
          <ul className="mt-2 divide-y divide-amber-200/70 dark:divide-amber-400/10">
            {pending.map((request) => {
              const mine = !!user?.id && request.requestedById === user.id;
              return (
                <li key={request.id} className="flex flex-col gap-2 py-2 sm:flex-row sm:items-start sm:justify-between">
                  <div className="min-w-0 text-xs leading-5 text-slate-700 dark:text-slate-200">
                    <p className="font-semibold text-slate-900 dark:text-white"><span className="me-2 rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-semibold text-amber-800 dark:bg-amber-400/15 dark:text-amber-300">{t('Pending approval')}</span>{describeRequest(request)}{flagBadges(requestFlags(request))}</p>
                    <p className="text-slate-500 dark:text-slate-400">{request.countryCode} · {request.jurisdiction || t('All jurisdictions')}{request.kind === 'change' && request.effectiveTo ? ` · ${t('Until {date}', { date: dateLabel(request.effectiveTo) })}` : ''}</p>
                    {request.reason && <p>{t('Reason: {reason}', { reason: request.reason })}</p>}
                    <p className="text-slate-500 dark:text-slate-400">{t('Requested by {name} on {date}', { name: request.requestedByName || t('Unknown user'), date: format.date(request.requestedAt) })}</p>
                  </div>
                  <div className="flex shrink-0 gap-2">
                    {request.canApprove ? (
                      <>
                        <button type="button" onClick={() => openDecision(request, 'approve')} className="btn-primary h-8 px-3 text-xs">{t('Approve')}</button>
                        <button type="button" onClick={() => openDecision(request, 'reject')} className="btn-secondary h-8 px-3 text-xs">{t('Reject')}</button>
                      </>
                    ) : mine && canManage ? (
                      <button type="button" onClick={() => void withdraw(request)} disabled={withdrawingId === request.id} className="btn-secondary h-8 px-3 text-xs disabled:opacity-50">{t('Withdraw my request')}</button>
                    ) : (
                      <span className="text-xs text-slate-500 dark:text-slate-400">{t('Waiting for another administrator')}</span>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {formError && !formOpen && <div role="alert" className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 dark:border-red-400/20 dark:bg-red-400/10 dark:text-red-300">{formError}</div>}

      <div className="surface overflow-hidden">
        {loading ? (
          <div className="p-8 text-center text-sm text-slate-500">{t('Loading statutory rules…')}</div>
        ) : loadError ? (
          <div role="alert" className="p-8 text-center"><p className="text-sm text-red-700 dark:text-red-300">{loadError}</p><button type="button" onClick={() => void load()} className="btn-secondary mt-3"><RefreshCw className="h-4 w-4" />{t('Retry')}</button></div>
        ) : visibleRows.length === 0 ? (
          <div className="p-8 text-center"><p className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('No rules match these filters.')}</p><p className="mt-1 text-xs text-slate-500">{t('Clear the filters or confirm the selected country pack is configured.')}</p></div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[980px] text-sm">
              <thead className="bg-slate-50 dark:bg-white/[0.03]"><tr>{[msg('Scope'), msg('Country / jurisdiction'), msg('Rule'), msg('Value'), msg('Effective period'), msg('Source / reason'), msg('Status'), msg('Actions')].map((label) => <th key={label} className="px-3 py-2.5 text-start text-xs font-semibold text-slate-500 dark:text-slate-400">{t(label)}</th>)}</tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {visibleRows.map((rule) => {
                  const status = statusFor(rule);
                  const waiting = pendingByRule.get(ruleIdentity(rule));
                  const unit = unitOf(rule);
                  return (
                    <tr key={rule.id} className="align-top hover:bg-slate-50/70 dark:hover:bg-white/[0.02]">
                      <td className="px-3 py-3"><span className={`inline-block whitespace-nowrap rounded-full px-2 py-1 text-xs font-semibold ${rule.isTenantOverride ? 'bg-amber-100 text-amber-800 dark:bg-amber-400/15 dark:text-amber-300' : 'bg-blue-100 text-blue-800 dark:bg-blue-400/15 dark:text-blue-300'}`}>{rule.isTenantOverride ? t("Your company's value") : t('Published rate')}</span></td>
                      <td className="px-3 py-3 text-xs text-slate-600 dark:text-slate-300"><strong className="block text-slate-800 dark:text-slate-100">{rule.countryCode}</strong>{rule.jurisdiction || t('All jurisdictions')}</td>
                      <td className="max-w-[280px] px-3 py-3">
                        <p className="text-sm font-medium text-slate-900 dark:text-slate-100">{nameOf(rule.ruleKey)}</p>
                        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{isPlatformManaged(rule.ruleKey) && !rule.isTenantOverride ? t('{unit} · Set by the platform', { unit: statutoryUnitName(t, unit) }) : statutoryUnitName(t, unit)}</p>
                        <details className="group mt-1 text-xs text-slate-500 rtl:text-[13px] dark:text-slate-400">
                          <summary className="w-fit cursor-pointer select-none font-semibold text-sapphire hover:underline dark:text-cyanAccent">{t('Details')}</summary>
                          <dl className="mt-1 grid grid-cols-[auto_minmax(0,1fr)] gap-x-2 gap-y-0.5">
                            <dt>{t('Rule key')}</dt><dd><code dir="ltr" className="break-all font-mono text-slate-700 dark:text-slate-200">{rule.ruleKey}</code></dd>
                            <dt>{t('Stored value')}</dt><dd><code dir="ltr" className="font-mono text-slate-700 dark:text-slate-200">{rule.ruleValue}</code></dd>
                            <dt>{t('Data type')}</dt><dd><code dir="ltr" className="font-mono text-slate-700 dark:text-slate-200">{rule.dataType}</code></dd>
                          </dl>
                        </details>
                      </td>
                      <td className="px-3 py-3 text-sm font-semibold text-slate-900 dark:text-white"><UnitValue display={show(rule, rule.ruleValue)} /></td>
                      <td className="px-3 py-3 text-xs text-slate-600 dark:text-slate-300"><span className="block">{dateLabel(rule.effectiveFrom)}</span><span className="text-slate-400">{t('Until {date}', { date: dateLabel(rule.effectiveTo) })}</span></td>
                      <td className="max-w-[260px] px-3 py-3 text-xs leading-5 text-slate-600 dark:text-slate-300">{rule.description || t('No source recorded')}</td>
                      <td className="px-3 py-3"><span className={`rounded-full px-2 py-1 text-xs font-semibold ${status === 'Active' ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-300' : status === 'Scheduled' ? 'bg-violet-100 text-violet-800 dark:bg-violet-400/15 dark:text-violet-300' : 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300'}`}>{t(status)}</span>{waiting && status !== 'Retired' && <span className="mt-1.5 block w-fit rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-semibold text-amber-800 dark:bg-amber-400/15 dark:text-amber-300" title={describeRequest(waiting)}>{t('Pending approval')}</span>}</td>
                      <td className="px-3 py-3">
                        {rule.isTenantOverride && canManage && status === 'Active' && !waiting ? <div className="flex flex-wrap gap-x-3 gap-y-1"><button type="button" onClick={() => openSupersede(rule)} className="whitespace-nowrap text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent"><PencilLine className="me-1 inline h-3.5 w-3.5" aria-hidden />{t('Replace from a date')}</button><button type="button" onClick={() => setRetiring(rule)} className="text-xs font-semibold text-red-600 hover:underline"><Archive className="me-1 inline h-3.5 w-3.5" aria-hidden />{t('Retire')}</button></div> : <span className="text-xs text-slate-400">{waiting && rule.isTenantOverride && status === 'Active' ? t('Pending approval') : t(status === 'Scheduled' ? 'Scheduled' : 'Read only')}</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <Modal isOpen={formOpen} title={editing ? t('Replace {rule} from a date', { rule: nameOf(editing.ruleKey) }) : t('New statutory override')} onClose={() => setFormOpen(false)} size="lg" footer={<><button type="button" onClick={() => setFormOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={() => void save()} disabled={saving || (!editing && isPlatformManaged(form.ruleKey))} className="btn-primary disabled:opacity-50">{saving ? t('Sending…') : t('Send for approval')}</button></>}>
        <div className="space-y-4">
          <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm leading-6 text-amber-900 dark:border-amber-400/20 dark:bg-amber-400/10 dark:text-amber-200">
            {t('Changes are tenant-wide and effective-dated. Platform-maintained statutory families cannot be overridden. New versions start no earlier than tomorrow so completed payroll remains reproducible.')} {t('A second administrator must approve each change before payroll uses it.')}
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Country pack')}
              <select value={form.countryCode} disabled={!!editing} onChange={(event) => { const nextPack = packs.find((pack) => pack.countryCode === event.target.value); setForm((current) => ({ ...current, countryCode: event.target.value, jurisdiction: nextPack?.jurisdictions[0]?.code ?? '', ruleKey: '', ruleValue: '' })); }} className="input-field mt-1">
                <option value="">{t('Choose country')}</option>{packs.map((pack) => <option key={pack.countryCode} value={pack.countryCode}>{locale === 'ar' ? pack.nameAr || pack.nameEn : pack.nameEn}</option>)}
              </select>
            </label>
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Jurisdiction')}
              <select value={form.jurisdiction} disabled={!!editing || !form.countryCode} onChange={(event) => setForm((current) => ({ ...current, jurisdiction: event.target.value, ruleKey: '', ruleValue: '' }))} className="input-field mt-1">
                <option value="">{t('Choose jurisdiction')}</option>{(packs.find((pack) => pack.countryCode === form.countryCode)?.jurisdictions ?? []).map((item) => <option key={item.code} value={item.code}>{t(item.label)}</option>)}
              </select>
            </label>
          </div>
          <label className="block text-sm font-medium text-slate-700 dark:text-slate-200">{t('Rule')}
            <select value={form.ruleKey} disabled={!!editing || !form.jurisdiction} onChange={(event) => selectRule(event.target.value)} className="input-field mt-1">
              <option value="">{t('Choose an approved rule')}</option>{formCatalogue.map((rule) => <option key={rule.ruleKey} value={rule.ruleKey} disabled={isPlatformManaged(rule.ruleKey)}>{isPlatformManaged(rule.ruleKey) ? t('{rule} — Set by the platform', { rule: nameOf(rule.ruleKey) }) : nameOf(rule.ruleKey)}</option>)}
            </select>
          </label>
          {selectedFormRule && (
            <div className="rounded-lg bg-slate-50 p-3 text-xs leading-5 text-slate-600 dark:bg-white/[0.04] dark:text-slate-300">
              <p className="font-semibold text-slate-800 dark:text-white">{t('Published rate: {value}', { value: publishedDisplay?.text ?? '—' })}{flagBadges(publishedDisplay?.flag ? [publishedDisplay.flag] : [])}</p>
              {editingCurrent && <p className="font-semibold text-slate-800 dark:text-white">{t("Your company's value now: {value}", { value: editingCurrent.text })}</p>}
              <p>{selectedFormRule.description}</p>
              <details className="mt-1 text-slate-500 dark:text-slate-400">
                <summary className="w-fit cursor-pointer select-none font-semibold text-sapphire hover:underline dark:text-cyanAccent">{t('Details')}</summary>
                <p className="mt-0.5">{t('Rule key')}: <code dir="ltr" className="font-mono">{selectedFormRule.ruleKey}</code> · {t('Stored value')}: <code dir="ltr" className="font-mono">{selectedFormRule.ruleValue}</code></p>
              </details>
            </div>
          )}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="sm:col-span-2">
              <label htmlFor="statutory-rule-value" className="block text-sm font-medium text-slate-700 dark:text-slate-200">{t("Your company's value")}</label>
              {formChoices.length > 0 ? (
                <select id="statutory-rule-value" value={form.ruleValue} onChange={(event) => setForm((current) => ({ ...current, ruleValue: event.target.value }))} aria-describedby="statutory-rule-value-hint" className="input-field mt-1">
                  <option value="">{t('Choose a value')}</option>
                  {formChoices.map(([stored, label]) => <option key={stored} value={stored}>{label}</option>)}
                </select>
              ) : formUnit.kind === 'date' ? (
                <input id="statutory-rule-value" type="date" value={form.ruleValue} onChange={(event) => setForm((current) => ({ ...current, ruleValue: event.target.value }))} aria-describedby="statutory-rule-value-hint" className="input-field mt-1" />
              ) : (
                <div className="relative mt-1">
                  <input
                    id="statutory-rule-value"
                    value={form.ruleValue}
                    inputMode={formUnit.kind === 'text' || formUnit.kind === 'unknown' ? undefined : 'decimal'}
                    onChange={(event) => setForm((current) => ({ ...current, ruleValue: event.target.value }))}
                    placeholder={selectedFormRule ? t('Published: {value}', { value: toInputValue(formUnit, selectedFormRule.ruleValue) }) : t('Choose a rule first')}
                    aria-describedby="statutory-rule-value-hint"
                    className={`input-field block w-full ${formSuffix ? 'pe-24' : ''}`}
                  />
                  {formSuffix && <span aria-hidden className="pointer-events-none absolute inset-y-0 end-3 flex items-center text-sm font-semibold text-slate-500 dark:text-slate-400">{formSuffix}</span>}
                </div>
              )}
              <div id="statutory-rule-value-hint" className="mt-1 space-y-0.5 text-xs leading-5 text-slate-500 dark:text-slate-400">
                {form.ruleKey && <p>{t('Unit: {unit}', { unit: statutoryUnitName(t, formUnit) })}{formUnit.kind === 'unknown' ? flagBadges([t('Unit not specified')]) : null}</p>}
                {form.ruleKey && <p>{inputHint(t, formUnit)}</p>}
                {typedPreview && (typedPreview.ok
                  ? <p className="font-semibold text-slate-700 dark:text-slate-200">{t('Payroll will use {value} (stored as {stored}).', { value: show({ ...form }, typedPreview.stored).text, stored: typedPreview.stored })}</p>
                  : <p className="font-semibold text-red-700 dark:text-red-300">{typedPreview.error}</p>)}
              </div>
            </div>
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Effective from')}
              <input type="date" min={tomorrow()} value={form.effectiveFrom.slice(0, 10)} onChange={(event) => setForm((current) => ({ ...current, effectiveFrom: event.target.value }))} className="input-field mt-1" />
            </label>
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Effective until')} <span className="font-normal text-slate-400">{t('(optional)')}</span>
              <input type="date" min={form.effectiveFrom.slice(0, 10)} value={form.effectiveTo?.slice(0, 10) ?? ''} onChange={(event) => setForm((current) => ({ ...current, effectiveTo: event.target.value || null }))} className="input-field mt-1" />
            </label>
          </div>
          <label className="block text-sm font-medium text-slate-700 dark:text-slate-200">{t('Approved reason and source')} <span className="text-red-600">*</span>
            <textarea value={form.description} onChange={(event) => setForm((current) => ({ ...current, description: event.target.value }))} rows={3} placeholder={t('Policy decision, authority or verified source; include approval reference.')} className="input-field mt-1 h-auto resize-y" />
          </label>
          {formError && <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-400/10 dark:text-red-300">{formError}</p>}
        </div>
      </Modal>

      <Modal isOpen={!!retiring} title={t('Retire statutory override')} onClose={() => setRetiring(null)} footer={<><button type="button" onClick={() => setRetiring(null)} className="btn-secondary">{t('Keep override')}</button><button type="button" onClick={() => void retire()} disabled={retireBusy} className="btn-danger disabled:opacity-50">{retireBusy ? t('Sending…') : t('Request retirement')}</button></>}>
        <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">{t("This stops your company's value for {rule} ({value}) and returns future payroll to the published rate. Payroll already run keeps the value it used.", { rule: retiring ? nameOf(retiring.ruleKey) : '', value: retiring ? show(retiring, retiring.ruleValue).text : '' })} {t('Retiring takes effect only after a second administrator approves it.')}</p>
      </Modal>

      <Modal
        isOpen={!!deciding}
        title={deciding?.action === 'approve' ? t('Approve this change?') : t('Reject this change?')}
        onClose={() => setDeciding(null)}
        footer={<><button type="button" onClick={() => setDeciding(null)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={() => void decide()} disabled={decisionBusy} className={`${deciding?.action === 'approve' ? 'btn-primary' : 'btn-danger'} disabled:opacity-50`}>{decisionBusy ? t(deciding?.action === 'approve' ? 'Approving…' : 'Rejecting…') : t(deciding?.action === 'approve' ? 'Approve' : 'Reject')}</button></>}
      >
        {deciding && (
          <div className="space-y-3 text-sm leading-6 text-slate-600 dark:text-slate-300">
            <p className="font-semibold text-slate-900 dark:text-white">{describeRequest(deciding.request)}{flagBadges(requestFlags(deciding.request))}</p>
            {(() => {
              const { old, next } = requestValues(deciding.request);
              const retire = deciding.request.kind === 'retire';
              return (
                <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 rounded-lg bg-slate-50 p-3 text-xs dark:bg-white/[0.04]">
                  <dt className="text-slate-500 dark:text-slate-400">{t('Rule')}</dt><dd className="font-medium text-slate-800 dark:text-slate-100">{nameOf(deciding.request.ruleKey)}</dd>
                  <dt className="text-slate-500 dark:text-slate-400">{retire ? t("Your company's value now") : t('Value now')}</dt><dd className="font-medium text-slate-800 dark:text-slate-100">{old ? <UnitValue display={old} /> : t('No previous value')}</dd>
                  <dt className="text-slate-500 dark:text-slate-400">{retire ? t('Published rate after') : t('Value after approval')}</dt><dd className="font-medium text-slate-800 dark:text-slate-100">{next ? <UnitValue display={next} /> : '—'}</dd>
                  <dt className="text-slate-500 dark:text-slate-400">{t('Starting')}</dt><dd className="font-medium text-slate-800 dark:text-slate-100">{dateLabel(deciding.request.effectiveFrom)}</dd>
                  <dt className="col-span-2">
                    <details className="text-slate-500 dark:text-slate-400">
                      <summary className="w-fit cursor-pointer select-none font-semibold text-sapphire hover:underline dark:text-cyanAccent">{t('Details')}</summary>
                      <p className="mt-0.5">{t('Rule key')}: <code dir="ltr" className="font-mono">{deciding.request.ruleKey}</code> · {t('Stored value')}: <code dir="ltr" className="font-mono">{deciding.request.value}</code></p>
                    </details>
                  </dt>
                </dl>
              );
            })()}
            {deciding.request.reason && <p>{t('Reason: {reason}', { reason: deciding.request.reason })}</p>}
            <p>{deciding.action === 'approve'
              ? t('Payroll will use the new value from its start date. This is recorded in the audit log with your name.')
              : t('Nothing changes in payroll. The request is closed and recorded in the audit log.')}</p>
            {deciding.action === 'reject' && (
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-200">{t('Reason for rejecting (optional)')}
                <textarea value={rejectReason} onChange={(event) => setRejectReason(event.target.value)} rows={2} className="input-field mt-1 h-auto resize-y" />
              </label>
            )}
            {decisionError && <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-400/10 dark:text-red-300">{decisionError}</p>}
          </div>
        )}
      </Modal>
    </div>
  );
}
