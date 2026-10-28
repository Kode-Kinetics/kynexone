'use client';

import { hostingRegion, noticeVersionNumber } from '../../../lib/essPrivacy';
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Loader2, Send } from 'lucide-react';
import { essCompletionApi, isStepUpRequired, problemOf, type EssCompletion, type EssDocument, type EssField, type EssSection } from '../../../api/essCompletion';
import { useLocale } from '../../../contexts/LocaleContext';
import { hijriLine } from '../../../lib/hire/dates';
import { submittableDocumentIds } from '../../../lib/scanState';
import { EssNotice, essInput, essPrimaryButton } from '../../../components/ess/EssParts';
import { DocumentUploader } from './DocumentUploader';
import { StepUpDialog } from './StepUpDialog';

const LTR_KINDS = new Set(['Phone', 'Iban', 'NationalId', 'Iqama', 'Border', 'Email', 'GccId', 'Passport', 'Date']);
const isEditable = (s: EssSection) => s.state === 'Missing' || s.state === 'Confirm' || s.state === 'ChangesRequested';

/** One section: purpose and privacy notice first, then the fields (prefilled where HR already holds a value). */
export function SectionForm({ section, meta, canWrite, onChanged, onBack }: {
  section: EssSection; meta: Pick<EssCompletion, 'noticeVersion' | 'hosting'>; canWrite: boolean; onChanged: () => void; onBack: () => void;
}) {
  const { t, locale } = useLocale();
  const [values, setValues] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  // A ref, not state: the retry runs from a closure made before the dialog returned, and must see the proof the dialog just produced.
  const stepUp = useRef<{ token: string; at: number } | null>(null);
  const [dialog, setDialog] = useState(false);
  const retry = useRef<(() => void) | null>(null);
  const [docs, setDocs] = useState<EssDocument[]>(section.documents);
  // Files the employee detached. Reset whenever the server's list is reloaded: a blocked file listed again is shown (removable) but never sent.
  const [removed, setRemoved] = useState<Set<string>>(new Set());
  const summaryRef = useRef<HTMLDivElement>(null);
  const formId = useId();
  const editable = canWrite && isEditable(section);
  const hasValue = section.fields.some(f => f.value !== '');
  const confirmOnly = section.state === 'Confirm' && hasValue;

  // A different section starts clean. A change of state (after sending, or HR's reply) clears what was typed and the errors, but keeps the confirmation.
  useEffect(() => { setNotice(null); }, [section.key]);
  useEffect(() => { setValues({}); setErrors({}); setSummary(null); setDocs(section.documents); setRemoved(new Set()); }, [section.key, section.state, section.requestId, section.documents]);

  const shownDocs = useMemo(() => docs.filter(d => !removed.has(d.id)), [docs, removed]);
  const sendableIds = useMemo(() => submittableDocumentIds(shownDocs), [shownDocs]);

  const freshToken = () => (stepUp.current && Date.now() - stepUp.current.at < 9 * 60_000 ? stepUp.current.token : undefined);
  const requireStepUp = (run: () => void) => { retry.current = run; setDialog(true); };

  const send = async (confirm: boolean) => {
    const token = freshToken();
    if (section.requiresStepUp && !token) { requireStepUp(() => void send(confirm)); return; }
    setBusy(true); setNotice(null); setErrors({}); setSummary(null);
    try {
      const typed = Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim() !== ''));
      await essCompletionApi.submit(section.key, { noticeVersion: meta.noticeVersion, confirm, values: confirm ? {} : typed, documentIds: sendableIds }, token);
      setNotice({ tone: 'ok', text: t('Sent to HR. You will see the result here.') });
      onChanged();
    } catch (e) {
      if (isStepUpRequired(e)) { stepUp.current = null; requireStepUp(() => void send(confirm)); }
      else {
        const p = problemOf(e);
        const fieldErrors: Record<string, string> = {};
        for (const [field, codes] of Object.entries(p.errors ?? {})) if (codes[0]) fieldErrors[field] = t(`ess.completion.valid.${codes[0]}`);
        setErrors(fieldErrors);
        const text = t(p.titleKey ?? `ess.completion.error.${p.code ?? 'validation_failed'}`);
        setSummary(Object.keys(fieldErrors).length > 0 ? t('Fix the highlighted fields to continue.') : text);
        if (Object.keys(fieldErrors).length === 0) setNotice({ tone: 'error', text });
        requestAnimationFrame(() => summaryRef.current?.focus());
      }
    } finally { setBusy(false); }
  };

  const fieldInput = (f: EssField) => {
    const id = `${formId}-${f.key}`; const errId = `${id}-err`; const hintId = `${id}-hint`;
    const ltr = LTR_KINDS.has(f.kind);
    const common = {
      id, 'aria-invalid': errors[f.key] ? true : undefined, 'aria-describedby': [errors[f.key] ? errId : '', f.sensitive && f.value ? hintId : '', f.kind === 'Date' && hijriLine(values[f.key] ?? f.value, locale) ? `${id}-hijri` : ''].filter(Boolean).join(' ') || undefined,
      className: essInput, disabled: !editable || busy, maxLength: f.maxLength, dir: ltr ? ('ltr' as const) : undefined,
    };
    // A sensitive value is never sent to the browser in full: the field starts empty and the masked value is shown beside it.
    const current = f.sensitive ? '' : f.value;
    const value = values[f.key] ?? current;
    const onChange = (v: string) => setValues(prev => ({ ...prev, [f.key]: v }));
    return (
      <div key={f.key} className="space-y-1">
        <label htmlFor={id} className="block text-xs font-semibold text-slate-600 dark:text-slate-300">
          {t(f.labelKey)} {f.required && <span className="font-normal text-slate-500">{t('(required)')}</span>}
        </label>
        {f.kind === 'Enum' ? (
          <select {...common} value={value} onChange={e => onChange(e.target.value)}>
            <option value="">{t('ess.completion.ui.select')}</option>
            {(f.options ?? []).map(o => <option key={o} value={o}>{t(o)}</option>)}
          </select>
        ) : (
          <input {...common} value={value} onChange={e => onChange(e.target.value)} autoComplete="off"
            type={f.kind === 'Email' ? 'email' : f.kind === 'Phone' ? 'tel' : f.kind === 'Date' ? 'date' : 'text'} inputMode={f.kind === 'NationalId' || f.kind === 'Iqama' || f.kind === 'Border' ? 'numeric' : undefined} />
        )}
        {f.kind === 'Date' && hijriLine(value, locale) && <p id={`${id}-hijri`} data-testid={`hijri-${f.key}`} dir="auto" className="text-xs text-slate-500 dark:text-slate-400">{t('Hijri: {date}', { date: hijriLine(value, locale) })}</p>}
        {f.sensitive && f.value && <p id={hintId} className="text-xs text-slate-500 dark:text-slate-400">{t('Current value: {value}', { value: '' })}<bdi dir="ltr">{f.value}</bdi><br />{t('Type a new value only if it is wrong')}</p>}
        <p id={errId} role="alert" className="text-xs font-medium text-rose-700 dark:text-rose-300">{errors[f.key]}</p>
      </div>
    );
  };

  const hosting = hostingRegion(meta.hosting);
  const withDocs = section.key === 'documents' || section.key === 'bank' || section.key === 'id';
  const docsRequired = section.key === 'documents';
  const stateHint = useMemo(() => {
    if (section.state === 'Pending') return t('Pending HR review. You cannot edit this section until HR replies.');
    if (section.state === 'AwaitingFinance') return t('Waiting for Finance to approve your new bank details.');
    if (section.state === 'Accepted') return t('HR accepted this section.');
    return null;
  }, [section.state, t]);

  return (
    <section aria-labelledby={`${formId}-title`} className="space-y-4 rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]" data-testid="section-form">
      <button type="button" onClick={onBack} className="text-xs font-semibold text-sapphire underline dark:text-cyanAccent lg:hidden">{t('Back to the list')}</button>
      <div>
        <h2 id={`${formId}-title`} className="text-base font-bold text-slate-800 dark:text-slate-100">{t(`ess.completion.section.${section.key}.title`)}</h2>
        <p className="mt-1 text-sm text-slate-600 dark:text-slate-300"><span className="font-semibold">{t('Why HR asks for this')}: </span>{t(`ess.completion.section.${section.key}.purpose`)}</p>
        <p className="mt-2 rounded-lg bg-slate-50 p-2 text-xs text-slate-600 dark:bg-white/[0.04] dark:text-slate-300">
          <span className="font-semibold" title={noticeVersionNumber(meta.noticeVersion) ? t('Notice version {version}', { version: noticeVersionNumber(meta.noticeVersion) as string }) : undefined}>{t('Privacy notice')}. </span>
          {!hosting.labelKey
            ? t('Your details are stored in the hosting region set by your company and are visible only to you and authorised HR staff. HR reviews every change before it is applied to your record.')
            : hosting.outsideKingdom
            ? t('Your details are stored on servers in {region}, outside the Kingdom, and are visible only to you and authorised HR staff. HR reviews every change before it is applied to your record.', { region: t(hosting.labelKey) })
            : t('Your details are stored in {region} and are visible only to you and authorised HR staff. HR reviews every change before it is applied to your record.', { region: t(hosting.labelKey) })}
        </p>
        {section.key === 'bank' && <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('A bank change is checked by HR, approved by Finance, and takes effect from the next day.')}</p>}
      </div>

      <div aria-live="polite">
        {stateHint && <EssNotice tone="ok">{stateHint}</EssNotice>}
        {section.state === 'ChangesRequested' && <EssNotice tone="error"><strong>{t('HR asked for a change')}</strong>{section.reviewNote && <span className="mt-1 block"><bdi>{section.reviewNote}</bdi></span>}</EssNotice>}
        {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}
      </div>
      <div ref={summaryRef} tabIndex={-1} role={summary ? 'alert' : undefined} className="text-sm font-semibold text-rose-700 outline-none dark:text-rose-300">{summary}</div>

      {!canWrite && <EssNotice tone="ok">{t('Your account can view this page but cannot send details. Ask HR if you need to.')}</EssNotice>}

      {section.fields.length > 0 && (
        <form id={formId} onSubmit={e => { e.preventDefault(); void send(false); }} className="space-y-4" noValidate>
          {confirmOnly && <p className="text-sm text-slate-600 dark:text-slate-300">{t('Check your current details below, then confirm them or correct them.')}</p>}
          <div className="grid gap-4 sm:grid-cols-2">{section.fields.map(fieldInput)}</div>
          {editable && (
            <div className="flex flex-wrap gap-2">
              {confirmOnly && <button type="button" className="rounded-xl border border-sapphire px-4 py-2 text-sm font-semibold text-sapphire dark:border-cyanAccent dark:text-cyanAccent" disabled={busy || Object.values(values).some(v => v.trim())} onClick={() => void send(true)}>{t('Confirm these details')}</button>}
              <button type="submit" className={essPrimaryButton} disabled={busy || (docsRequired && sendableIds.length === 0)}>
                {busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <Send className="h-4 w-4" aria-hidden="true" />}{busy ? t('Sending…') : t('Send to HR')}
              </button>
            </div>
          )}
        </form>
      )}

      {withDocs && (
        <>
          <DocumentUploader section={section.key} documents={shownDocs} onRemove={d => setRemoved(prev => new Set(prev).add(d.id))} disabled={!canWrite || section.state === 'Pending' || section.state === 'Accepted'} getStepUp={freshToken}
            onNeedStepUp={requireStepUp} onUploaded={d => setDocs(prev => [...prev, d])} />
          {section.fields.length === 0 && editable && (
            <button type="button" className={essPrimaryButton} disabled={busy || sendableIds.length === 0} onClick={() => void send(false)}>
              {busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <Send className="h-4 w-4" aria-hidden="true" />}{busy ? t('Sending…') : t('Send to HR')}
            </button>
          )}
          {docsRequired && sendableIds.length === 0 && editable && <p className="text-xs text-slate-500">{t('Attach at least one file before sending.')}</p>}
        </>
      )}

      <StepUpDialog open={dialog} onClose={() => { setDialog(false); retry.current = null; }}
        onVerified={token => { stepUp.current = { token, at: Date.now() }; setDialog(false); const run = retry.current; retry.current = null; setTimeout(() => run?.(), 0); }} />
    </section>
  );
}
