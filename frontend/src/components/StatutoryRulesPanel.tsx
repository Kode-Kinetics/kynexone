'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { Archive, CalendarClock, PencilLine, Plus, RefreshCw, Scale, Search, ShieldCheck } from 'lucide-react';
import { countryPacksApi, statutoryRulesApi } from '../api/countryPacks';
import type { CountryPackOption, CreateStatutoryRuleRequest, StatutoryRuleDto } from '../api/countryPacks';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import { msg } from '../i18n/translations';
import { Modal } from './Modal';

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

const isPlatformManaged = (key: string) =>
  ['gosi.', 'saned.', 'gpssa.', 'grsia.', 'dews.', 'eosb.', 'leave.', 'workhours.'].some((prefix) => key.toLowerCase().startsWith(prefix))
  || ['nitaqat.default_target_ratio', 'emiratisation.target_ratio', 'emiratization.target_ratio', 'qatarization.target_ratio'].includes(key.toLowerCase());

export function StatutoryRulesPanel() {
  const t = useT();
  const { locale } = useLocale();
  const format = useFormat();
  // Effective dates are calendar boundaries; preserve their UTC day in every viewer's zone.
  const dateLabel = (value?: string | null) => value ? format.date(value.slice(0, 10)) : t('Open-ended');
  const { hasPermission } = useAuth();
  const canManage = hasPermission('payroll.rates.statutory_override');
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

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError('');
    try {
      const [ruleRows, countryPacks] = await Promise.all([statutoryRulesApi.list(), countryPacksApi.available()]);
      setRows(ruleRows);
      setPacks(countryPacks);
    } catch (error) {
      setLoadError(errorMessage(error, t('Statutory rules could not be loaded.')));
    } finally {
      setLoading(false);
    }
  }, [t]);

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
    return !needle || `${row.ruleKey} ${row.description} ${row.ruleValue}`.toLowerCase().includes(needle);
  }), [country, jurisdiction, query, rows, scope]);

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
      ruleValue: rule.ruleValue, dataType: rule.dataType, description: '',
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
    if (form.effectiveTo && form.effectiveTo <= form.effectiveFrom.slice(0, 10)) {
      setFormError(t('Effective until must be later than Effective from.'));
      return;
    }
    setSaving(true);
    try {
      if (editing) {
        await statutoryRulesApi.update(editing.id, {
          ruleValue: form.ruleValue.trim(), description: form.description.trim(),
          effectiveFrom: new Date(form.effectiveFrom).toISOString(),
          effectiveTo: form.effectiveTo ? new Date(form.effectiveTo).toISOString() : null,
        });
      } else {
        await statutoryRulesApi.create({
          ...form, ruleValue: form.ruleValue.trim(), description: form.description.trim(),
          effectiveFrom: new Date(form.effectiveFrom).toISOString(),
          effectiveTo: form.effectiveTo ? new Date(form.effectiveTo).toISOString() : null,
        });
      }
      setFormOpen(false);
      await load();
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
    try {
      await statutoryRulesApi.remove(retiring.id);
      setRetiring(null);
      await load();
    } catch (error) {
      setFormError(errorMessage(error, t('The override could not be retired.')));
      setRetiring(null);
    } finally {
      setRetireBusy(false);
    }
  };

  const selectedFormRule = formCatalogue.find((row) => row.ruleKey === form.ruleKey);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto]">
        <div className="rounded-xl border border-blue-200 bg-blue-50/70 p-4 dark:border-blue-400/20 dark:bg-blue-400/10">
          <div className="flex items-start gap-3">
            <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
            <div>
              <h3 className="text-sm font-semibold text-slate-950 dark:text-white">{t('Tenant-wide statutory rules')}</h3>
              <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-300">
                {t('Review platform-maintained rules and governed tenant overrides used by payroll and compliance.')} {t('Company-specific rates are managed separately.')} <Link href="/setup?tab=glMapping" className="font-semibold text-sapphire hover:underline">{t('GL & rates')}</Link>
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
          { label: msg('Platform rules'), value: metrics.defaults, icon: Scale },
          { label: msg('Active overrides'), value: metrics.overrides, icon: ShieldCheck },
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
            <option value="all">{t('All scopes')}</option><option value="default">{t('Platform rules')}</option><option value="override">{t('Tenant overrides')}</option>
          </select>
        </div>
      </div>

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
                  return (
                    <tr key={rule.id} className="align-top hover:bg-slate-50/70 dark:hover:bg-white/[0.02]">
                      <td className="px-3 py-3"><span className={`rounded-full px-2 py-1 text-xs font-semibold ${rule.isTenantOverride ? 'bg-amber-100 text-amber-800 dark:bg-amber-400/15 dark:text-amber-300' : 'bg-blue-100 text-blue-800 dark:bg-blue-400/15 dark:text-blue-300'}`}>{t(rule.isTenantOverride ? 'Tenant override' : 'Platform rule')}</span></td>
                      <td className="px-3 py-3 text-xs text-slate-600 dark:text-slate-300"><strong className="block text-slate-800 dark:text-slate-100">{rule.countryCode}</strong>{rule.jurisdiction || t('All jurisdictions')}</td>
                      <td className="max-w-[260px] px-3 py-3"><code className="break-all text-xs font-semibold text-slate-800 dark:text-slate-100">{rule.ruleKey}</code><p className="mt-1 text-[11px] text-slate-500">{isPlatformManaged(rule.ruleKey) && !rule.isTenantOverride ? t('{type} · Platform maintained', { type: rule.dataType }) : rule.dataType}</p></td>
                      <td className="px-3 py-3 font-mono text-xs font-semibold text-sapphire dark:text-cyanAccent">{rule.ruleValue}</td>
                      <td className="px-3 py-3 text-xs text-slate-600 dark:text-slate-300"><span className="block">{dateLabel(rule.effectiveFrom)}</span><span className="text-slate-400">{t('Until {date}', { date: dateLabel(rule.effectiveTo) })}</span></td>
                      <td className="max-w-[260px] px-3 py-3 text-xs leading-5 text-slate-600 dark:text-slate-300">{rule.description || t('No source recorded')}</td>
                      <td className="px-3 py-3"><span className={`rounded-full px-2 py-1 text-xs font-semibold ${status === 'Active' ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-300' : status === 'Scheduled' ? 'bg-violet-100 text-violet-800 dark:bg-violet-400/15 dark:text-violet-300' : 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300'}`}>{t(status)}</span></td>
                      <td className="px-3 py-3">
                        {rule.isTenantOverride && canManage && status === 'Active' ? <div className="flex gap-2"><button type="button" onClick={() => openSupersede(rule)} className="text-xs font-semibold text-sapphire hover:underline"><PencilLine className="me-1 inline h-3.5 w-3.5" />{t('Supersede')}</button><button type="button" onClick={() => setRetiring(rule)} className="text-xs font-semibold text-red-600 hover:underline"><Archive className="me-1 inline h-3.5 w-3.5" />{t('Retire')}</button></div> : <span className="text-xs text-slate-400">{t(status === 'Scheduled' ? 'Scheduled' : 'Read only')}</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <Modal isOpen={formOpen} title={editing ? t('Supersede {rule}', { rule: editing.ruleKey }) : t('New statutory override')} onClose={() => setFormOpen(false)} size="lg" footer={<><button type="button" onClick={() => setFormOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={() => void save()} disabled={saving || (!editing && isPlatformManaged(form.ruleKey))} className="btn-primary disabled:opacity-50">{t(saving ? 'Saving…' : editing ? 'Create new version' : 'Create override')}</button></>}>
        <div className="space-y-4">
          <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm leading-6 text-amber-900 dark:border-amber-400/20 dark:bg-amber-400/10 dark:text-amber-200">
            {t('Changes are tenant-wide and effective-dated. Platform-maintained statutory families cannot be overridden. New versions start no earlier than tomorrow so completed payroll remains reproducible.')}
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
              <option value="">{t('Choose an approved rule')}</option>{formCatalogue.map((rule) => <option key={rule.ruleKey} value={rule.ruleKey} disabled={isPlatformManaged(rule.ruleKey)}>{isPlatformManaged(rule.ruleKey) ? t('{rule} — Platform maintained', { rule: rule.ruleKey }) : rule.ruleKey}</option>)}
            </select>
          </label>
          {selectedFormRule && <div className="rounded-lg bg-slate-50 p-3 text-xs leading-5 text-slate-600 dark:bg-white/[0.04] dark:text-slate-300"><strong className="text-slate-800 dark:text-white">{t('Current platform value: {value}', { value: selectedFormRule.ruleValue })}</strong><span className="ms-2">{t('Unit/type: {type}', { type: selectedFormRule.dataType })}</span><p>{selectedFormRule.description}</p></div>}
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Override value')}
              <input value={form.ruleValue} onChange={(event) => setForm((current) => ({ ...current, ruleValue: event.target.value }))} placeholder={selectedFormRule ? t('Current: {value}', { value: selectedFormRule.ruleValue }) : t('Enter value in the stated unit')} className="input-field mt-1" />
            </label>
            <label className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Data type')}
              <input value={form.dataType} readOnly className="input-field mt-1 bg-slate-50 dark:bg-white/[0.03]" />
            </label>
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

      <Modal isOpen={!!retiring} title={t('Retire statutory override')} onClose={() => setRetiring(null)} footer={<><button type="button" onClick={() => setRetiring(null)} className="btn-secondary">{t('Keep override')}</button><button type="button" onClick={() => void retire()} disabled={retireBusy} className="btn-danger disabled:opacity-50">{t(retireBusy ? 'Retiring…' : 'Retire tomorrow')}</button></>}>
        <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">{t('This closes {rule} prospectively and returns future calculations to the applicable platform value. Historical payroll keeps the version it used.', { rule: retiring?.ruleKey ?? '' })}</p>
      </Modal>
    </div>
  );
}
