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
import { bankSettingsBaseline, bankSettingsLoadProblem, resolveBankSettingsScope, type BankSettingsLoadProblem } from '../../lib/saudiBankSettingsScope';
import {
  AR, Alert, EMPTY_SETTINGS, EN, SaudiBankSettingsForm, btn, checkSettings, normaliseSettings, sameSettings,
} from './SaudiBankExportPanel';

type Load = { status: 'loading' } | { status: 'error'; message: string; problem: BankSettingsLoadProblem } | { status: 'ready' };

// This section's own words (the form and its field copy come from the shared EN/AR tables).
const COPY = {
  en: {
    noSaudi: 'No Saudi company is available here. These bank settings are for Saudi companies; for any other company, enter the WPS employer ID on the company itself (Setup > Companies).',
    scopeNotSaudi: (name: string) => `${name} is not a Saudi company, so it has no bank file settings. Enter its WPS employer ID on the company itself (Setup > Companies), or choose a Saudi company in the scope.`,
    choose: 'Choose a company to enter its bank settings.',
    forbidden: 'Your account cannot open these settings. Bank and WPS settings need the payroll export permission; ask an administrator to grant it.',
    notFound: 'This company could not be found. Choose another company, or reload the page.',
    discard: 'You have unsaved bank details. Switch company and discard them?',
  },
  ar: {
    noSaudi: 'لا توجد شركة سعودية متاحة هنا. هذه الإعدادات خاصة بالشركات السعودية؛ أما الشركات الأخرى فأدخل رقم صاحب العمل في حماية الأجور في بيانات الشركة (الإعداد > الشركات).',
    scopeNotSaudi: (name: string) => `${name} ليست شركة سعودية، فلا إعدادات ملف بنكي لها. أدخل رقم صاحب العمل في حماية الأجور في بيانات الشركة (الإعداد > الشركات)، أو اختر شركة سعودية في النطاق.`,
    choose: 'اختر شركة لإدخال إعدادات البنك الخاصة بها.',
    forbidden: 'لا يمكن لحسابك فتح هذه الإعدادات. تتطلب إعدادات البنك وحماية الأجور صلاحية تصدير الرواتب؛ اطلب من المسؤول منحها.',
    notFound: 'تعذّر العثور على هذه الشركة. اختر شركة أخرى أو أعد تحميل الصفحة.',
    discard: 'لديك بيانات بنكية غير محفوظة. هل تريد تغيير الشركة وتجاهلها؟',
  },
} as const;


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

  const words = COPY[locale === 'ar' ? 'ar' : 'en'];
  const [picked, setPicked] = useState('');
  const scope = useMemo(() => resolveBankSettingsScope({ companyId, companies, picked }), [companyId, companies, picked]);
  const selected = scope.kind === 'selected' ? scope.company : null;
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
    // A save still in flight for the company just left must not keep this one's Save button disabled (its result is
    // already ignored by activeRef).
    setSaved(null); setDraft(EMPTY_SETTINGS); setShowErrors(false); setError(''); setNotice(''); setSaving(false);
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
        const list = Array.isArray(fmts) ? fmts : [];
        // The only supported format is pre-selected as an explicit, visible choice while nothing is saved yet; that
        // default is the baseline, so an untouched form is not "unsaved".
        const base = bankSettingsBaseline(normaliseSettings(settings), list);
        setFormats(list);
        setSaved(base);
        setDraft(base);
        setLoad({ status: 'ready' });
      } catch (err) {
        if (ac.signal.aborted || isAbortError(err)) return;
        const { status, message } = await extractApiError(err, c.loadFailed);
        if (!ac.signal.aborted) setLoad({ status: 'error', message, problem: bankSettingsLoadProblem(status) });
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
      if (activeRef.current === startedFor) setSaving(false);
    }
  };

  if (scope.kind === 'noSaudi') {
    return <section className="surface p-4" aria-label={c.setupTitle}><Alert tone="info">{words.noSaudi}</Alert></section>;
  }
  if (scope.kind === 'scopeNotSaudi') {
    return <section className="surface p-4" aria-label={c.setupTitle}><Alert tone="info">{words.scopeNotSaudi(companyName(scope.scoped))}</Alert></section>;
  }

  return (
    <section className="surface overflow-hidden" aria-label={c.setupTitle}>
      <div className="space-y-4 p-4">
        {(scope.kind === 'choose' || (scope.kind === 'selected' && scope.showPicker)) && (
          <label className="flex flex-wrap items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
            <span className="font-medium">{c.company}</span>
            <select aria-label={c.company} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/10 dark:bg-white/5"
              value={selectedId} onChange={e => { if (dirty && !window.confirm(words.discard)) return; setPicked(e.target.value); }}>
              <option value="">{words.choose}</option>
              {scope.options.map(x => <option key={x.id} value={x.id}>{companyName(x)}</option>)}
            </select>
          </label>
        )}
        {selected && companyName(selected) && <p className="text-sm text-slate-500 dark:text-slate-400"><strong className="text-slate-800 dark:text-slate-100">{companyName(selected)}</strong></p>}

        {!selected && <Alert tone="info">{words.choose}</Alert>}

        {selected && load.status === 'loading' && (
          <div className="flex items-center gap-2 py-6 text-sm text-slate-400" role="status">
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> {c.loading}
          </div>
        )}

        {selected && load.status === 'error' && (
          <div className="space-y-3">
            <Alert tone="error"><span role="alert">
              {load.problem.kind === 'forbidden' ? words.forbidden : load.problem.kind === 'notFound' ? words.notFound : load.message}
            </span></Alert>
            {load.problem.retry && (
              <button type="button" className={btn.ghost} onClick={() => setReloadToken(n => n + 1)}>
                <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.retry}
              </button>
            )}
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
