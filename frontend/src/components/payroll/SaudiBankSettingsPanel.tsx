'use client';

// Setup > Bank and WPS: the employer's bank-instruction settings, saved once per Saudi company, before any payroll run exists.
//
// The same form the payment-batch panel shows (SaudiBankSettingsForm), minus everything per-batch. Reading and saving these
// settings needs no activation: they are the employer's own account and establishment facts. Only validating, generating and
// downloading a file stays behind the operator's activation switch (SaudiBankExportPanel).

import React, { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { Loader2, RefreshCw } from 'lucide-react';
import {
  saudiBankExportsApi, isAbortError, extractApiError,
  type SaudiBankExportFormat, type SaudiBankExportSettings,
} from '../../api/saudiBankExports';
import type { CompanyDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import {
  AR, Alert, EMPTY_SETTINGS, EN, SAUDI_CODES, SaudiBankSettingsForm, btn, checkSettings, normaliseSettings, sameSettings,
} from './SaudiBankExportPanel';

type Load = { status: 'loading' } | { status: 'error'; message: string } | { status: 'ready' };

export interface SaudiBankSettingsPanelProps {
  /** The company chosen in Setup's scope, when there is one. */
  companyId?: string | null;
  /** The companies Setup has listed for this reader. */
  companies?: CompanyDto[];
  onDirtyChange?: (dirty: boolean) => void;
}

const companyName = (c: CompanyDto) => c.tradeName || c.legalNameEn;

export function SaudiBankSettingsPanel({ companyId, companies, onDirtyChange }: SaudiBankSettingsPanelProps) {
  const { locale } = useLocale();
  const c = locale === 'ar' ? AR : EN;
  const { hasPermission } = useAuth();
  const uid = useId();

  const saudi = useMemo(() => (companies ?? []).filter(x => SAUDI_CODES.has((x.countryCode ?? '').toUpperCase())), [companies]);
  const [picked, setPicked] = useState('');
  const selected = saudi.find(x => x.id === companyId) ?? saudi.find(x => x.id === picked) ?? (saudi.length === 1 ? saudi[0] : null);
  const selectedId = selected?.id ?? '';

  const [load, setLoad] = useState<Load>({ status: 'loading' });
  const [reloadToken, setReloadToken] = useState(0);
  const [formats, setFormats] = useState<SaudiBankExportFormat[]>([]);
  const [saved, setSaved] = useState<SaudiBankExportSettings | null>(null);
  const [draft, setDraft] = useState<SaudiBankExportSettings>(EMPTY_SETTINGS);
  const [saving, setSaving] = useState(false);
  const [showErrors, setShowErrors] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const activeRef = useRef(selectedId);
  activeRef.current = selectedId;

  useEffect(() => {
    setSaved(null); setDraft(EMPTY_SETTINGS); setShowErrors(false); setError(''); setNotice('');
    if (!selectedId) { setLoad({ status: 'ready' }); return; }
    const ac = new AbortController();
    setLoad({ status: 'loading' });
    (async () => {
      try {
        const [fmts, settings] = await Promise.all([
          saudiBankExportsApi.listFormats({ signal: ac.signal }),
          saudiBankExportsApi.getSettings(selectedId, { signal: ac.signal }),
        ]);
        if (ac.signal.aborted) return;
        const norm = normaliseSettings(settings);
        const list = Array.isArray(fmts) ? fmts : [];
        setFormats(list);
        setSaved(norm);
        // The only supported format is pre-selected as an explicit, visible choice while nothing is saved yet.
        setDraft(!norm.formatId && list.length === 1 ? { ...norm, formatId: list[0].id } : norm);
        setLoad({ status: 'ready' });
      } catch (err) {
        if (ac.signal.aborted || isAbortError(err)) return;
        const { message } = await extractApiError(err, c.loadFailed);
        if (!ac.signal.aborted) setLoad({ status: 'error', message });
      }
    })();
    return () => ac.abort();
    // `c` intentionally excluded: a language switch must not refetch or wipe the form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, reloadToken]);

  const dirty = !!saved && !sameSettings(saved, draft);
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);
  useEffect(() => () => onDirtyChange?.(false), [onDirtyChange]);

  const errors = useMemo(() => checkSettings(draft, c), [draft, c]);
  const setField = useCallback((k: keyof SaudiBankExportSettings, v: string | boolean) => {
    setDraft(d => ({ ...d, [k]: v }));
    setNotice('');
  }, []);

  const onSave = async (e?: React.FormEvent) => {
    e?.preventDefault();
    if (saving || !selectedId) return;
    setShowErrors(true);
    const firstBad = (Object.keys(errors) as (keyof SaudiBankExportSettings)[])[0];
    if (firstBad) {
      setError(c.fixFields);
      document.getElementById(`${uid}-${firstBad}`)?.focus();
      return;
    }
    const startedFor = selectedId;
    const payload = { ...draft };
    setSaving(true); setError(''); setNotice('');
    try {
      const result = normaliseSettings(await saudiBankExportsApi.saveSettings(startedFor, payload));
      if (activeRef.current !== startedFor) return;
      setSaved(result);
      setDraft(d => (sameSettings(payload, d) ? result : d));
      setShowErrors(false);
      setNotice(c.saved);
    } catch (err) {
      if (activeRef.current !== startedFor) return;
      const { status, message } = await extractApiError(err, c.genericError);
      if (status === 401 || status === 402) return; // handled globally
      setError(status === 409 && message === c.genericError ? c.conflict : message);
    } finally {
      setSaving(false);
    }
  };

  if (saudi.length === 0) {
    return (
      <section className="surface p-4" aria-label={c.setupTitle}>
        <Alert tone="info">{locale === 'ar'
          ? 'إعدادات البنك لحماية الأجور تخص الشركات السعودية. للشركات الأخرى أدخل رقم صاحب العمل في حماية الأجور ضمن بيانات الشركة.'
          : 'These bank settings are for Saudi companies. For any other company, enter the WPS employer ID on the company itself (Setup > Companies).'}</Alert>
      </section>
    );
  }

  return (
    <section className="surface overflow-hidden" aria-label={c.setupTitle}>
      <div className="space-y-4 p-4">
        {saudi.length > 1 && !companyId && (
          <label className="flex flex-wrap items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
            <span className="font-medium">{c.company}</span>
            <select aria-label={c.company} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/10 dark:bg-white/5"
              value={selectedId} onChange={e => setPicked(e.target.value)}>
              <option value="">—</option>
              {saudi.map(x => <option key={x.id} value={x.id}>{companyName(x)}</option>)}
            </select>
          </label>
        )}
        {selected && <p className="text-sm text-slate-500 dark:text-slate-400"><strong className="text-slate-800 dark:text-slate-100">{companyName(selected)}</strong></p>}

        {!selected && <Alert tone="info">{locale === 'ar' ? 'اختر شركة لإدخال إعدادات البنك.' : 'Choose a company to enter its bank settings.'}</Alert>}

        {selected && load.status === 'loading' && (
          <div className="flex items-center gap-2 py-6 text-sm text-slate-400" role="status">
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> {c.loading}
          </div>
        )}

        {selected && load.status === 'error' && (
          <div className="space-y-3">
            <Alert tone="error"><span role="alert">{load.message}</span></Alert>
            <button type="button" className={btn.ghost} onClick={() => setReloadToken(n => n + 1)}>
              <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.retry}
            </button>
          </div>
        )}

        {selected && load.status === 'ready' && (
          <>
            <Alert tone="warn">
              <p className="font-semibold">{c.disclaimerTitle}</p>
              <p className="mt-1 text-xs">{c.disclaimer}</p>
            </Alert>
            {formats.length === 0 && <Alert tone="error">{c.noFormats}</Alert>}
            {error && <Alert tone="error"><span role="alert">{error}</span></Alert>}
            {notice && <Alert tone="ok"><span role="status">{notice}</span></Alert>}
            <SaudiBankSettingsForm c={c} idPrefix={uid} draft={draft} setField={setField}
              errors={showErrors ? errors : {}} formats={formats} canManage={hasPermission('payroll.structure_manage')}
              saving={saving} otherBusy={false} dirty={dirty} onSubmit={onSave} />
          </>
        )}
      </div>
    </section>
  );
}
