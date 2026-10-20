'use client';

import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { CheckCircle2 } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeRatesApi,
  type StatutoryDisplayUnit,
  type StatutoryOverrideRequest,
  type StatutoryPendingListItem,
  type StatutoryPendingOverride,
  type StatutoryRateRow,
} from '../../api/financeRates';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { useAppToast } from '../ui/AppToast';
import { Modal } from '../Modal';
import {
  Badge,
  errorStatus,
  Field,
  FieldError,
  PanelState,
  serverMessage,
  theadClass,
  thClass,
  todayIso,
  useConfirm,
  useScopedResource,
  WarningNote,
  type Translator,
} from './glUi';

export interface StatutoryCountry {
  code: string;
  jurisdiction: string;
}

interface Props {
  scope: string | null;
  scopeName: string;
  countryCode: string;
  jurisdiction: string;
  /** Countries the group's companies operate in, for the group-level (read-only) view. */
  countries: StatutoryCountry[];
  canOverride: boolean; // payroll.rates.statutory_override
  canApprove: boolean; // may approve in principle; the server says per request (never the requester)
}

interface OverrideDraft {
  ruleKey: string;
  overrideValue: string;
  effectiveFrom: string;
  effectiveTo: string;
  reviewBy: string;
  reason: string;
}

// ── Plain-language rule names (the server's own label wins when it sends one) ──
function knownRuleName(t: Translator, key: string): string | null {
  switch (key) {
    case 'gosi.saudi_employee_rate': return t('GOSI contribution, Saudi employee share');
    case 'gosi.saudi_employer_rate': return t('GOSI contribution, Saudi employer share');
    case 'gosi.new_entrant_employee_rate': return t('GOSI annuities, new entrant (registered from 3 July 2024), employee share');
    case 'gosi.new_entrant_employer_rate': return t('GOSI annuities, new entrant (registered from 3 July 2024), employer share');
    case 'gosi.covered_wage_min_annuities_sar': return t('GOSI minimum contributory wage, annuities (warning only)');
    case 'gosi.covered_wage_min_occupational_hazards_sar': return t('GOSI minimum contributory wage, occupational hazards (warning only)');
    case 'gosi.employee_rate': return t('GOSI contribution, employee share');
    case 'gosi.saned_rate': return t('SANED unemployment insurance contribution');
    case 'gosi.expat_occupational_hazard_rate': return t('GOSI occupational hazards, non-Saudi employees');
    case 'gosi.covered_wage_ceiling_sar': return t('GOSI maximum contributory wage');
    case 'gosi.covered_wage_floor_sar': return t('GOSI minimum contributory wage');
    case 'gosi.retirement_age_years': return t('GOSI retirement age');
    case 'ot.standard_multiplier': return t('Overtime pay multiplier');
    case 'ot.restday_multiplier': return t('Rest-day overtime multiplier');
    case 'ot.holiday_multiplier': return t('Public-holiday overtime multiplier');
    case 'ot.standard_monthly_hours': return t('Standard monthly working hours');
    case 'leave.annual_base_days': return t('Annual leave entitlement');
    case 'leave.annual_tiered_days': return t('Annual leave after the service threshold');
    case 'leave.annual_tier_threshold_years': return t('Years of service for the higher annual leave');
    case 'leave.maternity_days': return t('Maternity leave');
    case 'leave.paternity_days': return t('Paternity leave');
    case 'leave.marriage_days': return t('Marriage leave');
    case 'leave.bereavement_days': return t('Bereavement leave');
    case 'leave.hajj_max_days': return t('Hajj leave, maximum');
    case 'leave.sick_band1_days': return t('Sick leave at full pay');
    case 'leave.sick_band2_days': return t('Sick leave at reduced pay');
    case 'leave.sick_band3_days': return t('Sick leave without pay');
    case 'eosb.tier1_days_per_year': return t('End-of-service days per year, first five years');
    case 'eosb.tier2_days_per_year': return t('End-of-service days per year, after five years');
    case 'nitaqat.default_target_ratio': return t('Nitaqat Saudization target');
    case 'gpssa.national_employee_rate': return t('GPSSA pension contribution, national employee share');
    case 'gpssa.national_employer_rate': return t('GPSSA pension contribution, national employer share');
    case 'grsia.national_employee_rate': return t('GRSIA pension contribution, national employee share');
    case 'grsia.national_employer_rate': return t('GRSIA pension contribution, national employer share');
    case 'dews.tier1_monthly_rate': return t('DEWS monthly contribution, first five years');
    case 'dews.tier2_monthly_rate': return t('DEWS monthly contribution, after five years');
    default: return null;
  }
}

/** The unit a value is shown in: the server's displayUnit, else inferred from the key's family. */
function unitFor(row: StatutoryRateRow): StatutoryDisplayUnit {
  if (row.displayUnit) return row.displayUnit;
  const k = row.ruleKey.toLowerCase();
  if (k.endsWith('_rate') || k.endsWith('_ratio')) return 'percent';
  if (k.endsWith('_multiplier')) return 'multiplier';
  if (k.endsWith('_days') || k.endsWith('days_per_year')) return 'days';
  if (k.endsWith('_hours')) return 'hours';
  if (k.endsWith('_sar') || k.endsWith('_aed') || k.endsWith('_amount')) return 'amount';
  return 'text';
}

const currencyFromKey = (key: string) => {
  const m = /_(sar|aed|kwd|bhd|omr|qar)$/i.exec(key);
  return m ? m[1].toUpperCase() : null;
};

// The server is the authority on a statutory value's unit — StatutoryValueUnits.cs refuses a rate
// written as a percentage and says so. This mirrors the rate families so the operator sees the
// expected form BEFORE typing, rather than after a refusal. A key this does not recognise stays a
// plain text field, because inventing bounds for it would be worse than having none.
function unitHintFor(t: Translator, ruleKey: string): { hint: string; min?: number; max?: number; step?: string } | null {
  const k = ruleKey.toLowerCase();
  if (k.startsWith('nitaqat.curve.')) return null;
  if (k.endsWith('_rate') || k.endsWith('_ratio')) {
    const contribution = ['gosi.', 'saned.', 'gpssa.', 'grsia.', 'dews.'].some((p) => k.startsWith(p));
    return {
      hint: contribution
        ? t('Enter a decimal fraction of the contributory wage, not a percentage: 9% is 0.09, 9.75% is 0.0975. Values above 0.3 are refused.')
        : t('Enter a decimal fraction, not a percentage: 35% is 0.35, 100% is 1.0.'),
      min: 0,
      max: contribution ? 0.3 : 1,
      step: '0.0001',
    };
  }
  if (k.endsWith('_sar')) return { hint: t('An amount in whole currency units, for example 45000 for SAR 45,000.'), min: 0, step: '0.01' };
  if (k.endsWith('_multiplier')) return { hint: t('A multiplier, for example 1.5 for time-and-a-half.'), min: 0, max: 10, step: '0.01' };
  return null;
}

export function StatutoryRatesPanel({ scope, scopeName, countryCode, jurisdiction, countries, canOverride, canApprove }: Props) {
  const t = useT();
  const f = useFormat();
  const toast = useAppToast();
  const { confirm, dialog } = useConfirm();
  const isGroupView = scope === null;

  // Group view: the platform values for one country the group operates in.
  const [groupCountry, setGroupCountry] = useState<string>(() => countries[0]?.code ?? '');
  const groupCountryEntry = countries.find((c) => c.code === groupCountry) ?? countries[0];
  const country = isGroupView ? groupCountryEntry?.code ?? '' : countryCode;
  const jur = isGroupView ? groupCountryEntry?.jurisdiction ?? '' : jurisdiction;

  const rowsRes = useScopedResource<StatutoryRateRow[]>(
    `statutory:${scope ?? 'group'}:${country}:${jur}`,
    () => financeRatesApi.listStatutory(scope, country, jur),
    !!country,
  );
  const rows = useMemo(() => rowsRes.data ?? [], [rowsRes.data]);
  const writable = canOverride && rowsRes.ready && !isGroupView;

  // Requests waiting for a checker, across the companies the viewer can see. `null` = this server
  // has no pending endpoint (older API); the section is then left out rather than shown as an error.
  const pendingFetcher = useCallback(async (): Promise<StatutoryPendingListItem[] | null> => {
    try {
      return await financeRatesApi.listPendingOverrides();
    } catch (err) {
      if (errorStatus(err) === 404 || errorStatus(err) === 405) return null;
      throw err;
    }
  }, []);
  const pendingRes = useScopedResource<StatutoryPendingListItem[] | null>('statutory-pending', pendingFetcher, canApprove || canOverride);
  const pendingList = pendingRes.data ?? [];

  // Older servers do not return `pendingOverride`; remember this session's requests so the row
  // still says "pending" until reload. Newer servers make this redundant (it survives reload).
  const [localPending, setLocalPending] = useState<Record<string, StatutoryPendingOverride>>({});
  const pendingFor = (r: StatutoryRateRow): StatutoryPendingOverride | null => {
    if ('pendingOverride' in r) return r.pendingOverride ?? null;
    return localPending[r.ruleKey] ?? null;
  };

  const ruleName = (r: StatutoryRateRow) => r.label || knownRuleName(t, r.ruleKey) || r.ruleKey;

  // What a pending request DOES, in words a checker signs off on. A retirement carries the live value
  // in `value`, so showing that value as "requested" would invert its meaning.
  const isRetire = (p: StatutoryPendingOverride) => p.kind === 'retire';
  const kindLabel = (p: StatutoryPendingOverride) =>
    isRetire(p) ? t('Retirement') : p.isNew ? t('New override') : t('Value change');
  const requestedText = (row: StatutoryRateRow, p: StatutoryPendingOverride) =>
    isRetire(p)
      ? p.effectiveFrom
        ? t('Return to the statutory value from {date}', { date: f.date(p.effectiveFrom) })
        : t('Return to the statutory value')
      : formatValue(row, p.value);

  const formatValue = (r: StatutoryRateRow, value: number | string | null | undefined): string => {
    if (value == null || value === '') return '—';
    const n = typeof value === 'number' ? value : Number(value);
    if (Number.isNaN(n)) return String(value);
    switch (unitFor(r)) {
      case 'percent': return t('{value}%', { value: f.number(n * 100, { maximumFractionDigits: 4 }) });
      case 'multiplier': return t('{value}×', { value: f.number(n, { maximumFractionDigits: 4 }) });
      case 'days': return t('{count, plural, one {# day} other {# days}}', { count: n });
      case 'hours': return t('{count, plural, one {# hour} other {# hours}}', { count: n });
      case 'amount': return f.money(n, currencyFromKey(r.ruleKey));
      default: return f.number(n, { maximumFractionDigits: 4 });
    }
  };

  const directionHint = (r: StatutoryRateRow): string | null => {
    switch (r.direction) {
      case 'floor': return t('Can only be increased above the statutory minimum.');
      case 'ceiling': return t('Can only be lowered below the statutory maximum.');
      case 'either': return t('Can be raised or lowered with a reason.');
      default: return null;
    }
  };

  const inForce = (r: StatutoryRateRow) =>
    r.ruleEffectiveFrom
      ? r.ruleEffectiveTo
        ? t('{from} to {to}', { from: f.date(r.ruleEffectiveFrom), to: f.date(r.ruleEffectiveTo) })
        : t('Since {from}', { from: f.date(r.ruleEffectiveFrom) })
      : null;

  const ruleCell = (r: StatutoryRateRow): ReactNode => {
    const since = inForce(r);
    const hint = directionHint(r);
    return (
      <div className="space-y-0.5">
        <p className="text-slate-800 dark:text-slate-100">{ruleName(r)}</p>
        <p className="text-[11px] text-slate-500"><span className="font-mono">{r.ruleKey}</span></p>
        <div className="flex flex-wrap items-center gap-1">
          {r.verified === false && (
            <Badge tone="amber" title={t('The platform value has not yet been checked against the published law.')}>
              {t('Pending legal verification')}
            </Badge>
          )}
          {r.appliedByPayroll === false && (
            <Badge tone="slate" title={t('Shown for reference. Payroll does not read this value.')}>
              {t('Reference only')}
            </Badge>
          )}
          {r.belowFloorIgnored && (
            <Badge tone="rose" title={r.notice ? t(r.notice) : undefined}>
              {t('Group value below the legal minimum — not applied')}
            </Badge>
          )}
        </div>
        {(since || r.source) && (
          <p className="text-[11px] text-slate-500">
            {since && <span>{since}</span>}
            {since && r.source && <span aria-hidden> · </span>}
            {r.source && <cite className="not-italic">{t('Source {source}', { source: r.source })}</cite>}
          </p>
        )}
        {hint && !isGroupView && <p className="text-[11px] text-slate-500">{hint}</p>}
      </div>
    );
  };

  // ── Override request ───────────────────────────────────────────────────────
  const [modalOpen, setModalOpen] = useState(false);
  const [baseRow, setBaseRow] = useState<StatutoryRateRow | null>(null);
  const [draft, setDraft] = useState<OverrideDraft | null>(null);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);

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

  const refreshAll = async () => {
    await Promise.all([rowsRes.reload(), pendingRes.reload()]);
  };

  const save = async () => {
    if (!draft || scope === null) return;
    if (!draft.overrideValue.trim()) return setError(t('Enter the override value.'));
    if (!draft.effectiveFrom) return setError(t('Enter the date the override takes effect.'));
    if (!draft.reviewBy) return setError(t('Enter a review date, so the override cannot silently outlive its justification.'));
    if (!draft.reason.trim()) return setError(t('Enter the reason for overriding the statutory value.'));
    if (draft.effectiveTo && draft.effectiveTo < draft.effectiveFrom) return setError(t('The end date cannot be before the start date.'));
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
      setLocalPending((p) => ({
        ...p,
        [created.ruleKey]: {
          id: created.id,
          value: created.overrideValue,
          effectiveFrom: created.effectiveFrom,
          effectiveTo: created.effectiveTo,
          reason: created.reason,
          reviewBy: created.reviewBy,
          canApprove: false,
        },
      }));
      setModalOpen(false);
      toast.success(t('Override submitted. A second person with approval rights must approve it before payroll uses it.'));
      await refreshAll();
    } catch (err) {
      // 422 STATUTORY_FLOOR / STATUTORY_OVERRIDE_NOT_APPLIED / GOSI_RATE_IS_STATUTORY: the server's sentence, verbatim.
      setError(serverMessage(err, t));
    } finally {
      setSaving(false);
    }
  };

  const forget = (ruleKey: string) =>
    setLocalPending((p) => {
      const next = { ...p };
      delete next[ruleKey];
      return next;
    });

  const approve = async (id: string, ruleKey: string, request?: StatutoryPendingOverride) => {
    setBusyKey(ruleKey);
    try {
      await financeRatesApi.approveOverride(id);
      forget(ruleKey);
      toast.success(
        request && isRetire(request)
          ? t('Retirement approved. Payroll returns to the statutory value from its effective date.')
          : t('Override approved. Payroll uses it from its effective date.'),
      );
      await refreshAll();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setBusyKey(null);
    }
  };

  const revert = async (id: string, ruleKey: string, name: string, pending: boolean) => {
    const ok = await confirm({
      title: pending ? t('Withdraw this override request?') : t('Revert to the statutory value?'),
      message: pending
        ? t('The request for {rule} will be withdrawn. Nothing changes in payroll.', { rule: name })
        : t('This asks for {rule} to go back to the platform statutory value for this company. A second person must approve it; payroll keeps the override until then, and the change applies from tomorrow at the earliest.', { rule: name }),
      confirmLabel: pending ? t('Withdraw request') : t('Request retirement'),
      tone: 'danger',
    });
    if (!ok) return;
    setBusyKey(ruleKey);
    try {
      const result = await financeRatesApi.revertOverride(id);
      forget(ruleKey);
      if (result.retirementRequested) {
        toast.info(t('Retirement requested — awaiting approval. Payroll keeps the override until a second person approves it.'));
      } else {
        toast.success(pending ? t('Request withdrawn.') : t('This override had already ended. Nothing changed.'));
      }
      await refreshAll();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setBusyKey(null);
    }
  };

  const draftHint = draft ? unitHintFor(t, draft.ruleKey) : null;
  const draftPreview = baseRow && draft && draft.overrideValue.trim() && !Number.isNaN(Number(draft.overrideValue)) ? formatValue(baseRow, draft.overrideValue) : null;

  if (!isGroupView && !countryCode) {
    return (
      <div className="surface p-6 text-center">
        <p className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('This company has no country set')}</p>
        <p className="mt-1 text-xs text-slate-500">{t("Set the company's country before configuring statutory rates.")}</p>
      </div>
    );
  }

  const pendingUnsupported = pendingRes.ready && pendingRes.data === null;
  const pendingSection = !pendingUnsupported && (canApprove || canOverride) && (!!pendingRes.error || pendingList.length > 0) && (
    <section className="surface p-4" aria-labelledby="stat-pending-heading">
      <h3 id="stat-pending-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
        {t('Pending approvals')}
      </h3>
      <p className="mb-3 text-xs text-slate-500 dark:text-slate-400">
        {t('Statutory overrides waiting for a second person. The person who requested an override cannot approve it.')}
      </p>
      <PanelState loading={pendingRes.loading && !pendingRes.data} error={pendingRes.error} onRetry={() => void pendingRes.reload()} empty={false} emptyLabel="">
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full min-w-[900px] text-sm">
            <thead className={theadClass}>
              <tr>
                <th scope="col" className={thClass}>{t('Company')}</th>
                <th scope="col" className={thClass}>{t('Rule')}</th>
                <th scope="col" className={thClass}>{t('Request')}</th>
                <th scope="col" className={thClass}>{t('Requested value')}</th>
                <th scope="col" className={thClass}>{t('Effective')}</th>
                <th scope="col" className={thClass}>{t('Requested by')}</th>
                <th scope="col" className={thClass}>{t('Reason')}</th>
                <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {pendingList.map((p) => {
                const asRow: StatutoryRateRow = rows.find((r) => r.ruleKey === p.ruleKey) ?? ({ ruleKey: p.ruleKey } as StatutoryRateRow);
                return (
                  <tr key={p.id} className="border-t border-slate-100 dark:border-white/5">
                    <td className="px-3 py-2 text-xs">{p.companyName || '—'}</td>
                    <td className="px-3 py-2">
                      <p className="text-slate-800 dark:text-slate-100">{ruleName(asRow)}</p>
                      <p className="text-[11px] text-slate-500"><span className="font-mono">{p.ruleKey}</span></p>
                    </td>
                    <td className="px-3 py-2 text-xs">
                      <Badge tone={isRetire(p) ? 'rose' : p.isNew ? 'violet' : 'amber'}>{kindLabel(p)}</Badge>
                    </td>
                    <td className="px-3 py-2 tabular-nums">{requestedText(asRow, p)}</td>
                    <td className="px-3 py-2 text-xs">{p.effectiveFrom ? f.date(p.effectiveFrom) : '—'}</td>
                    <td className="px-3 py-2 text-xs">
                      {p.requestedByName || '—'}
                      {p.requestedAt && <span className="block text-[11px] text-slate-500">{f.dateTime(p.requestedAt)}</span>}
                    </td>
                    <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{p.reason || '—'}</td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1.5">
                        {canApprove && p.canApprove === true ? (
                          <button type="button" onClick={() => approve(p.id, p.ruleKey, p)} disabled={busyKey === p.ruleKey} className="btn-primary h-7 px-2 text-xs disabled:opacity-60">
                            <CheckCircle2 className="h-3 w-3" aria-hidden /> {t('Approve')}
                          </button>
                        ) : (
                          <span className="text-[11px] text-slate-500">{t('Waiting for another approver')}</span>
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
    </section>
  );

  return (
    <div className="space-y-4">
      {pendingSection}

      <section className="surface p-4" aria-labelledby="stat-rates-heading">
        <div className="mb-3 flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 id="stat-rates-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Statutory Rates')}</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {isGroupView
                ? t('Platform statutory values for {country}. They apply to every company in that country unless an approved override exists. Choose a company above to request an override.', { country: country || '—' })
                : t('Statutory values for {scope} ({country}). An override is never a free edit: it needs a reason, an effective date, a review date and approval by a second person. The platform value stays the fallback.', { scope: scopeName, country: jur ? `${countryCode}/${jur}` : countryCode })}
            </p>
          </div>
          {isGroupView && countries.length > 1 && (
            <div className="min-w-[160px]">
              <Field label={t('Country')}>
                <select data-setup-filter value={groupCountryEntry?.code ?? ''} onChange={(e) => setGroupCountry(e.target.value)} className="select h-8 w-full text-xs">
                  {countries.map((c) => (
                    <option key={c.code} value={c.code}>
                      {c.code}
                    </option>
                  ))}
                </select>
              </Field>
            </div>
          )}
        </div>

        {isGroupView && countries.length === 0 ? (
          <p className="py-10 text-center text-sm text-slate-500">{t('None of your companies has a country set, so there are no statutory rules to show.')}</p>
        ) : (
          <PanelState
            loading={rowsRes.loading && !rowsRes.data}
            error={rowsRes.error}
            onRetry={() => void rowsRes.reload()}
            empty={rows.length === 0}
            emptyLabel={t('No statutory rules are published for this country yet.')}
          >
            <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
              {isGroupView ? (
                <table className="w-full min-w-[640px] text-sm">
                  <thead className={theadClass}>
                    <tr>
                      <th scope="col" className={thClass}>{t('Rule')}</th>
                      <th scope="col" className={`${thClass} text-end`}>{t('Platform value')}</th>
                      <th scope="col" className={thClass}>{t('Companies with an override')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.ruleKey} className="border-t border-slate-100 align-top dark:border-white/5">
                        <td className="px-3 py-2">{ruleCell(r)}</td>
                        <td className="px-3 py-2 text-end font-medium tabular-nums">{formatValue(r, r.platformDefault)}</td>
                        <td className="px-3 py-2 text-xs">
                          {r.overriddenInCompanies ? (
                            <Badge tone="violet">{t('{count, plural, one {Overridden in # company} other {Overridden in # companies}}', { count: r.overriddenInCompanies })}</Badge>
                          ) : r.overriddenInCompanies === 0 ? (
                            <span className="text-slate-500">{t('None')}</span>
                          ) : (
                            <span className="text-slate-400">—</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              ) : (
                <table className="w-full min-w-[860px] text-sm">
                  <thead className={theadClass}>
                    <tr>
                      <th scope="col" className={thClass}>{t('Rule')}</th>
                      <th scope="col" className={`${thClass} text-end`}>{t('Platform value')}</th>
                      <th scope="col" className={`${thClass} text-end`}>{t('Used for this company')}</th>
                      <th scope="col" className={thClass}>{t('Override')}</th>
                      <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => {
                      const pend = pendingFor(r);
                      const busy = busyKey === r.ruleKey;
                      const name = ruleName(r);
                      return (
                        <tr key={r.ruleKey} className="border-t border-slate-100 align-top dark:border-white/5">
                          <td className="px-3 py-2">{ruleCell(r)}</td>
                          <td className="px-3 py-2 text-end tabular-nums text-slate-600 dark:text-slate-300">{formatValue(r, r.platformDefault)}</td>
                          <td className="px-3 py-2 text-end font-medium tabular-nums">{formatValue(r, r.resolvedValue)}</td>
                          <td className="px-3 py-2">
                            <div className="space-y-1.5">
                              {r.isOverride && (
                                <div className="space-y-0.5">
                                  <div className="flex flex-wrap items-center gap-1.5">
                                    <Badge tone="violet" title={t('An approved override is in effect for this company.')}>{t('Override in effect')}</Badge>
                                    <span className="text-xs tabular-nums">{formatValue(r, r.overrideValue)}</span>
                                    {r.defaultDriftedSinceOverride && (
                                      <Badge tone="rose" title={t('The platform value changed after this override was set. Review it.')}>{t('Platform value changed')}</Badge>
                                    )}
                                    {r.neverApplied && (
                                      <Badge tone="rose" title={r.notice ? t(r.notice) : t('Payroll uses the GOSI-published rate.')}>{t('Saved, never applied')}</Badge>
                                    )}
                                  </div>
                                  {r.notice && !r.neverApplied && <p className="text-[11px] text-amber-700 dark:text-amber-300">{t(r.notice)}</p>}
                                  <p className="text-[11px] text-slate-500">
                                    {r.reason}
                                    {r.effectiveFrom && <span> · {t('From {date}', { date: f.date(r.effectiveFrom) })}</span>}
                                    {r.reviewBy && <span> · {t('Review by {date}', { date: f.date(r.reviewBy) })}</span>}
                                  </p>
                                </div>
                              )}
                              {pend && (
                                <div className="space-y-0.5">
                                  <div className="flex flex-wrap items-center gap-1.5">
                                    <Badge tone="amber">{t('Pending approval')}</Badge>
                                    <Badge tone={isRetire(pend) ? 'rose' : pend.isNew ? 'violet' : 'slate'}>{kindLabel(pend)}</Badge>
                                    <span className="text-xs tabular-nums">{requestedText(r, pend)}</span>
                                  </div>
                                  <p className="text-[11px] text-slate-500">
                                    {pend.reason}
                                    {pend.requestedByName && <span> · {t('Requested by {name}', { name: pend.requestedByName })}</span>}
                                    {pend.effectiveFrom && <span> · {t('From {date}', { date: f.date(pend.effectiveFrom) })}</span>}
                                  </p>
                                </div>
                              )}
                              {!r.isOverride && !pend && (
                                <span className="text-[11px] text-slate-500">
                                  {r.statutoryLocked ? t('GOSI-published rate — cannot be overridden') : t('Uses the platform value')}
                                </span>
                              )}
                            </div>
                          </td>
                          <td className="px-3 py-2">
                            <div className="flex flex-wrap items-center justify-end gap-1.5">
                              {pend ? (
                                <>
                                  {canApprove && pend.canApprove === true && (
                                    <button type="button" onClick={() => approve(pend.id, r.ruleKey, pend)} disabled={busy} className="btn-primary h-7 px-2 text-xs disabled:opacity-60">
                                      <CheckCircle2 className="h-3 w-3" aria-hidden /> {t('Approve')}
                                    </button>
                                  )}
                                  {canOverride && (
                                    <button type="button" onClick={() => revert(pend.id, r.ruleKey, name, true)} disabled={busy || !writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                                      {t('Withdraw request')}
                                    </button>
                                  )}
                                </>
                              ) : r.isOverride && r.overrideId ? (
                                canOverride && (
                                  <button type="button" onClick={() => revert(r.overrideId!, r.ruleKey, name, false)} disabled={busy || !writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                                    {t('Revert to statutory value')}
                                  </button>
                                )
                              ) : (
                                canOverride &&
                                !r.statutoryLocked && (
                                  <button type="button" onClick={() => openOverride(r)} disabled={!writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                                    {t('Request override')}
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
              )}
            </div>
          </PanelState>
        )}
      </section>

      {/* Override modal */}
      <Modal
        isOpen={modalOpen}
        title={t('Request override: {rule}', { rule: baseRow ? ruleName(baseRow) : '' })}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button type="button" onClick={save} disabled={saving || !writable} className="btn-primary disabled:opacity-60">
              {saving ? t('Submitting…') : t('Submit for approval')}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        <WarningNote>
          {t('This does not change the statutory rate itself. It records a reasoned, dated override for this company only, and it takes effect after a second person approves it.')}
        </WarningNote>
        {baseRow && directionHint(baseRow) && <p className="mb-3 text-xs font-medium text-slate-700 dark:text-slate-200">{directionHint(baseRow)}</p>}
        {draft && (
          <div className="space-y-3">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Field label={t('Platform value')}>
                <input value={baseRow ? formatValue(baseRow, baseRow.platformDefault) : '—'} readOnly className="input w-full opacity-70" />
              </Field>
              <Field
                label={t('Override value')}
                required
                hint={
                  <>
                    {draftHint?.hint ?? t('Enter it in the same form as the platform value.')}
                    {draftPreview && <span className="block font-medium text-slate-700 dark:text-slate-200">{t('Shown as {value}', { value: draftPreview })}</span>}
                  </>
                }
              >
                <input
                  type={draftHint ? 'number' : 'text'}
                  inputMode="decimal"
                  min={draftHint?.min}
                  max={draftHint?.max}
                  step={draftHint?.step}
                  value={draft.overrideValue}
                  onChange={(e) => setDraft({ ...draft, overrideValue: e.target.value })}
                  className="input w-full"
                  placeholder={baseRow?.platformDefault != null ? String(baseRow.platformDefault) : '0.0'}
                />
              </Field>
            </div>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Field label={t('Effective from')} required>
                <input type="date" value={draft.effectiveFrom} onChange={(e) => setDraft({ ...draft, effectiveFrom: e.target.value })} className="input w-full" />
              </Field>
              <Field label={t('Effective to')} hint={t('Optional end date.')}>
                <input type="date" value={draft.effectiveTo} onChange={(e) => setDraft({ ...draft, effectiveTo: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <Field label={t('Review by')} required hint={t('When this override must be justified again or lapse.')}>
              <input type="date" value={draft.reviewBy} onChange={(e) => setDraft({ ...draft, reviewBy: e.target.value })} className="input w-full" />
            </Field>
            <Field label={t('Reason')} required hint={t('Why the statutory value is being overridden for this entity.')}>
              <textarea
                value={draft.reason}
                onChange={(e) => setDraft({ ...draft, reason: e.target.value })}
                className="input w-full"
                rows={3}
                placeholder={t('For example: free-zone entity with a social-insurance exemption, ruling reference…')}
              />
            </Field>
          </div>
        )}
      </Modal>
      {dialog}
    </div>
  );
}
