'use client';

import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { ArrowDown, ArrowLeft, ArrowUp, Check, Copy, Download, FileText, Plus, Save, Trash2 } from 'lucide-react';
import { downloadOfferFile, newOfferBlock, offerTemplatesApi, type OfferDesign, type OfferDesignBlock, type OfferTemplate, type OfferTemplateSummary } from '../api/offerTemplates';
import { requestFailureReason } from '../lib/requestFailure';
import { useNavigationGuard } from '../contexts/NavigationGuardContext';
import { useNavigationBlocker } from '../hooks/useNavigationBlocker';
import { useCompany } from '../contexts/CompanyContext';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useOfferLetterT } from '../i18n/offerLetter';

const input = 'w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:border-slate-600 dark:bg-slate-900 dark:text-slate-100';
const button = 'inline-flex min-h-10 items-center justify-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50 dark:border-slate-600 dark:bg-slate-900 dark:text-slate-100 dark:hover:bg-slate-800';
const primary = `${button} !border-sapphire !bg-sapphire !text-white hover:!bg-blue-700`;
const blockLabels: Record<OfferDesignBlock['kind'], string> = { heading: 'Heading', text: 'Paragraph / clause', table: 'Details / compensation table', compensation: 'Authoritative compensation', image: 'Image', signature: 'Signatures & acceptance', divider: 'Divider', spacer: 'Spacing', pageBreak: 'Page break' };
type Section = 'Content' | 'Branding' | 'Typography' | 'Layout' | 'Signatures' | 'Merge fields';

function Field({ label, value, onChange, multiline = false, type = 'text', min, max, step, help }: {
  label: string; value: string | number; onChange: (value: string) => void; multiline?: boolean;
  type?: string; min?: number; max?: number; step?: number; help?: string;
}) {
  const id = useId();
  const t = useOfferLetterT();
  return <div className="space-y-1.5"><label htmlFor={id} className="block text-sm font-medium">{t(label)}</label>
    {multiline ? <textarea id={id} className={`${input} min-h-32 resize-y`} value={value} onChange={e => onChange(e.target.value)} />
      : <input id={id} className={`${input} ${type === 'color' ? 'h-11 cursor-pointer p-1' : ''}`} type={type} value={value} min={min} max={max} step={step} onChange={e => onChange(e.target.value)} />}
    {help && <p className="text-xs leading-relaxed text-slate-500 dark:text-slate-400">{t(help)}</p>}
  </div>;
}
function Select({ label, value, options, onChange }: { label: string; value: string; options: (string | [string, string])[]; onChange: (value: string) => void }) {
  const id = useId();
  const t = useOfferLetterT();
  return <div className="space-y-1.5"><label htmlFor={id} className="block text-sm font-medium">{t(label)}</label><select id={id} className={input} value={value} onChange={e => onChange(e.target.value)}>{options.map(o => { const [v, name] = typeof o === 'string' ? [o, o] : o; return <option key={v} value={v}>{t(name)}</option>; })}</select></div>;
}
function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  const t = useOfferLetterT();
  return <label className="flex min-h-10 items-center gap-3 text-sm"><input className="h-4 w-4 accent-blue-600" type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)} />{t(label)}</label>;
}
// Only an inline PNG/JPEG is ever rendered: the value comes from a file or a saved template, never a URL.
const imageDataUrl = (value: string | null) => value && /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$/.test(value) ? value : null;
function Asset({ label, value, onChange, onError }: { label: string; value: string | null; onChange: (v: string | null) => void; onError: (e: string) => void }) {
  const id = useId();
  const t = useOfferLetterT();
  const src = imageDataUrl(value);
  const upload = async (file?: File) => {
    if (!file) return;
    if (!['image/png', 'image/jpeg'].includes(file.type) || file.size > 512000) { onError(t("Use a PNG or JPEG image smaller than 500 KB.")); return; }
    const reader = new FileReader(); reader.onerror = () => onError(t("The image could not be read."));
    reader.onload = () => onChange(String(reader.result)); reader.readAsDataURL(file);
  };
  return <div className="space-y-2"><label htmlFor={id} className="block text-sm font-medium">{t(label)}</label>
    {value && <div className="flex items-center justify-between gap-3 rounded-lg border border-slate-200 bg-white p-3">{src && <img src={src} alt={t('{name} preview', { name: t(label) })} className="h-14 max-w-48 object-contain" />}<button type="button" className={button} onClick={() => onChange(null)}>{t("Remove")}</button></div>}
    <input id={id} className="block w-full text-xs file:me-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-slate-800" type="file" accept="image/png,image/jpeg" onChange={e => { void upload(e.target.files?.[0]); e.target.value = ''; }} />
    <p className="text-xs text-slate-500">{t("PNG or JPEG · max 500 KB · max 4000 px / 8 MP. Use transparent PNG for logos.")}</p>
  </div>;
}

export interface OfferDraftDesignAdapter { offerId: string; expectedVersion: number; design: OfferDesign; onSave: (design: OfferDesign) => Promise<void> }
export function OfferLetterDesigner({ onClose, draftAdapter }: { onClose: () => void; draftAdapter?: OfferDraftDesignAdapter }) {
  const t = useOfferLetterT();
  const { dir } = useLocale();
  const { hasRole } = useAuth();
  const canWrite = ['Admin', 'HR Manager', 'Recruiter'].some(hasRole);
  const { selectedCompanyId, isGroupScope, companies } = useCompany();
  const companyName = companies.find(c => c.id === selectedCompanyId)?.name;
  const guard = useNavigationGuard();
  const [templates, setTemplates] = useState<OfferTemplateSummary[]>([]);
  const [current, setCurrent] = useState<OfferTemplate | null>(null);
  const [design, setDesign] = useState<OfferDesign | null>(null);
  const [name, setName] = useState('Standard offer');
  const [baseline, setBaseline] = useState('');
  const [tokens, setTokens] = useState<string[]>([]);
  const [section, setSection] = useState<Section>('Content');
  const [selectedBlock, setSelectedBlock] = useState(0);
  const [blockKind, setBlockKind] = useState<OfferDesignBlock['kind']>('text');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState<{ url: string; blob: Blob } | null>(null);
  const [previewError, setPreviewError] = useState('');
  const [previewBusy, setPreviewBusy] = useState(false);
  const [mobileView, setMobileView] = useState<'edit' | 'preview'>('edit');
  const [archiveConfirm, setArchiveConfirm] = useState(false);
  const [fieldKey, setFieldKey] = useState('');
  const [fieldValue, setFieldValue] = useState('');
  const textRef = useRef<HTMLTextAreaElement>(null);
  const dirty = !!design && JSON.stringify({ name, design }) !== baseline;
  const editable = canWrite && (!current || current.companyId !== null || (isGroupScope && !selectedCompanyId));
  useNavigationBlocker({ id: 'offer-letter-designer', when: dirty, message: t('Your letter design has unsaved changes. Discard them and leave?'), onDiscard: () => setBaseline(JSON.stringify({ name, design })) });
  const change = (patch: Partial<OfferDesign>) => { setDesign(d => d && ({ ...d, ...patch })); setNotice(''); };
  const updateBlock = (patch: Partial<OfferDesignBlock>) => { if (!design) return; change({ blocks: design.blocks.map((b, i) => i === selectedBlock ? { ...b, ...patch } : b) }); };
  const apply = (d: OfferDesign, n: string, t: OfferTemplate | null) => { setDesign(d); setName(n); setCurrent(t); setBaseline(JSON.stringify({ name: n, design: d })); setSelectedBlock(0); setError(''); setNotice(''); setArchiveConfirm(false); };
  const refresh = async () => setTemplates(await offerTemplatesApi.list());
  useEffect(() => {
    let active = true;
    Promise.all([offerTemplatesApi.defaults(), draftAdapter ? Promise.resolve([]) : offerTemplatesApi.list()]).then(([defaults, items]) => { if (active) { apply(draftAdapter?.design ?? defaults.design, t('Standard offer'), null); setTokens(defaults.tokens); setTemplates(items); } }).catch(e => { if (active) setError(requestFailureReason(e)); });
    return () => { active = false; };
  }, []);
  useEffect(() => {
    if (!design) return;
    const controller = new AbortController();
    setPreviewBusy(true); setPreviewError('');
    const timer = window.setTimeout(async () => {
      try {
        const blob = draftAdapter ? await offerTemplatesApi.candidatePreview(draftAdapter.offerId, draftAdapter.expectedVersion, design, controller.signal) : await offerTemplatesApi.preview(design, controller.signal);
        if (!controller.signal.aborted) setPreview({ blob, url: URL.createObjectURL(blob) });
      } catch (e) {
        if (controller.signal.aborted) return;
        const body = (e as { response?: { data?: Blob } }).response?.data;
        let message = requestFailureReason(e);
        if (body instanceof Blob) { try { message = JSON.parse(await body.text()).message ?? message; } catch { /* retain HTTP failure */ } }
        if (!controller.signal.aborted) setPreviewError(message);
      } finally { if (!controller.signal.aborted) setPreviewBusy(false); }
    }, 650);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [design]);
  useEffect(() => () => { if (preview) URL.revokeObjectURL(preview.url); }, [preview]);

  const openTemplate = (id: string) => guard.run(`offer-template:${id || 'new'}`, async () => {
    setBusy(true);
    try { if (id) { const t = await offerTemplatesApi.get(id); apply(t.design, t.name, t); } else { const defaults = await offerTemplatesApi.defaults(); apply(defaults.design, t('Untitled offer'), null); } }
    catch (e) { setError(requestFailureReason(e)); } finally { setBusy(false); }
  });
  const save = async (copy = false) => {
    if (!design) return;
    setBusy(true); setError(''); setNotice('');
    try {
      if (draftAdapter) { await draftAdapter.onSave(design); setBaseline(JSON.stringify({ name, design })); setNotice(t('Candidate draft design saved.')); return; }
      const saved = current && !copy ? await offerTemplatesApi.update(current.id, name, design, current.version) : await offerTemplatesApi.create(copy ? `${name.slice(0, 110)} (${t('copy')})` : name, design);
      apply(saved.design, saved.name, saved); setSelectedBlock(Math.min(selectedBlock, saved.design.blocks.length - 1)); setNotice(t('Saved · version {version}. Available when creating an offer.', { version: saved.version }));
      await refresh();
    } catch (e) { setError(requestFailureReason(e)); } finally { setBusy(false); }
  };
  const archive = async () => {
    if (!current) return;
    setBusy(true); setError('');
    try { await offerTemplatesApi.archive(current.id, current.version); await refresh(); const defaults = await offerTemplatesApi.defaults(); apply(defaults.design, t('Untitled offer'), null); setNotice(t("Template archived. Existing offer letters are unchanged.")); }
    catch (e) { setError(requestFailureReason(e)); } finally { setBusy(false); }
  };
  const importDesign = async (file?: File) => {
    if (!file) return;
    if (file.size > 6_000_000) { setError(t("Template file exceeds 6 MB.")); return; }
    try {
      const parsed = JSON.parse(await file.text());
      if (![1, 2].includes(parsed.schemaVersion) || !Array.isArray(parsed.blocks) || !parsed.blocks.length || parsed.blocks.length > 50) throw new Error('Use a template JSON file exported by this designer.');
      // The API validates all fields/assets and renderability before accepting an import.
      await offerTemplatesApi.preview(parsed);
      guard.run('offer-template:import', () => { setDesign(parsed); setCurrent(null); setName(file.name.replace(/\.json$/i, '').slice(0, 120)); setSelectedBlock(0); setNotice(t("Imported locally. Save to add it to your template library.")); });
    } catch (e) { setError(requestFailureReason(e)); }
  };
  const insertToken = (token: string) => {
    if (!design) return;
    const b = design.blocks[selectedBlock];
    if (!b || !['heading', 'text'].includes(b.kind)) { setNotice(t('Use {token} in any text or table field.', { token: `{{${token}}}` })); return; }
    const field = textRef.current; const start = field?.selectionStart ?? b.text.length; const end = field?.selectionEnd ?? start;
    const tokenText = `{{${token}}}`;
    updateBlock({ text: b.text.slice(0, start) + tokenText + b.text.slice(end) });
    requestAnimationFrame(() => { field?.focus(); field?.setSelectionRange(start + tokenText.length, start + tokenText.length); });
  };
  const moveBlock = (delta: number) => {
    if (!design) return;
    const next = selectedBlock + delta;
    if (next < 0 || next >= design.blocks.length) return;
    const blocks = [...design.blocks]; [blocks[selectedBlock], blocks[next]] = [blocks[next], blocks[selectedBlock]];
    change({ blocks }); setSelectedBlock(next);
  };
  const fonts = ['Lato', 'Noto Sans Arabic', ...(design?.customFont ? ['Custom'] : [])];
  const b = design?.blocks[selectedBlock];
  const numeric = (key: keyof OfferDesign, label: string, min: number, max: number, step = 1) => design && <Field label={label} type="number" value={design[key] as number} min={min} max={max} step={step} onChange={v => change({ [key]: Number(v) })} />;
  const controls: Partial<Record<Section, ReactNode>> = design ? {
    Branding: <>
      <Asset label="Company logo" value={design.logo} onChange={logo => change({ logo })} onError={setError} />
      <div className="grid grid-cols-2 gap-4">{numeric('logoWidth', 'Logo width (pt)', 24, 240)}<Select label="Logo alignment" value={design.logoAlign} options={['left', 'center', 'right']} onChange={logoAlign => change({ logoAlign })} /></div>
      <div className="grid grid-cols-3 gap-3">{(['accentColor', 'textColor', 'backgroundColor'] as const).map((key, i) => <Field key={key} label={['Accent', 'Body text', 'Paper'][i]} type="color" value={design[key]} onChange={v => change({ [key]: v })} />)}</div>
      <Field label="Letterhead text" value={design.header} onChange={header => change({ header })} help="Repeats on every page. Merge fields are supported." />
      <Field label="Footer text" value={design.footer} onChange={footer => change({ footer })} />
      <Toggle label="Show page border" checked={design.pageBorder} onChange={pageBorder => change({ pageBorder })} />
    </>,
    Typography: <>
      <Select label="Body font" value={design.fontFamily} options={fonts} onChange={fontFamily => change({ fontFamily })} />
      <Select label="Heading font" value={design.headingFontFamily} options={fonts} onChange={headingFontFamily => change({ headingFontFamily })} />
      <div className="grid grid-cols-2 gap-4">{numeric('fontSize', 'Body size (pt)', 8, 24)}{numeric('headingSize', 'Heading size (pt)', 12, 48)}</div>
      {numeric('lineHeight', 'Line spacing', 1, 2.5, 0.05)}
      <label className="block space-y-2 text-sm font-medium"><span>{t("Upload your font")}</span><input type="file" accept=".ttf,.otf" className="block w-full text-xs" onChange={e => {
        const file = e.target.files?.[0]; e.target.value = ''; if (!file) return;
        if (!/\.(ttf|otf)$/i.test(file.name) || file.size > 1500000) { setError(t("Upload a TTF/OTF font smaller than 1.5 MB.")); return; }
        const reader = new FileReader(); reader.onerror = () => setError(t("Font could not be read.")); reader.onload = () => change({ customFont: String(reader.result).split(',')[1], customFontName: file.name, fontFamily: 'Custom', headingFontFamily: 'Custom' }); reader.readAsDataURL(file);
      }} /></label>
      <p className="text-xs leading-relaxed text-slate-500">{t("TTF / OTF · max 1.5 MB. Upload a font you have permission to embed. Your font travels with the template and PDF; no device font dependency.")}</p>
      {design.customFont && <div className="flex flex-wrap items-center gap-2 text-sm"><span className="break-all">{design.customFontName}</span><button type="button" className={button} onClick={() => change({ customFont: null, fontFamily: 'Lato', headingFontFamily: 'Lato' })}>{t("Remove font")}</button></div>}
    </>,
    Layout: <>
      <Select label="Paper size" value={design.pageSize} options={['A4', 'Letter', 'Legal']} onChange={pageSize => change({ pageSize })} />
      <Select label="Orientation" value={design.landscape ? 'landscape' : 'portrait'} options={['portrait', 'landscape']} onChange={v => change({ landscape: v === 'landscape' })} />
      {numeric('margin', 'Page margins (pt)', 18, 90)}
      <Select label="Reading direction" value={design.direction} options={[[ 'ltr', 'Left to right' ], ['rtl', 'Right to left / Arabic']]} onChange={direction => change({ direction })} />
      <Toggle label="Show page numbers" checked={design.pageNumbers} onChange={pageNumbers => change({ pageNumbers })} />
      <p className="text-xs leading-relaxed text-slate-500">{t("Long letters flow onto additional pages. Insert a Page break block to control where a new page starts. Header and footer repeat automatically.")}</p>
    </>,
    Signatures: <>
      <Field label="Signatory name" value={design.signatory} onChange={signatory => change({ signatory })} />
      <Field label="Signatory title" value={design.signatoryTitle} onChange={signatoryTitle => change({ signatoryTitle })} />
      <Asset label="Signature image" value={design.signature} onChange={signature => change({ signature })} onError={setError} />
      <Asset label="Company stamp" value={design.stamp} onChange={stamp => change({ stamp })} onError={setError} />
      <Field label="Candidate acceptance wording" multiline value={design.candidateAcceptance} onChange={candidateAcceptance => change({ candidateAcceptance })} />
      <p className="text-xs leading-relaxed text-slate-500">{t("Shown where you place the Signatures & acceptance block. Uploaded signatures are visual assets, not a legally verified electronic-signature service.")}</p>
    </>,
    'Merge fields': <>
      <p className="text-sm leading-relaxed">{t("Built-in fields come from the actual offer. Their values cannot be overwritten by a template.")}</p>
      <div className="flex flex-wrap gap-2">{tokens.map(token => <code key={token} className="rounded bg-slate-100 px-2 py-1 text-xs dark:bg-slate-800">{`{{${token}}}`}</code>)}</div>
      <h3 className="pt-3 font-semibold">{t("Your custom fields")}</h3>
      {Object.entries(design.customFields).map(([key, value]) => <div key={key} className="flex items-end gap-2"><div className="min-w-0 flex-1"><Field label={`{{${key}}}`} value={value} onChange={v => change({ customFields: { ...design.customFields, [key]: v } })} /></div><button type="button" className={button} aria-label={t('Remove {name}', { name: key })} onClick={() => { const fields = { ...design.customFields }; delete fields[key]; change({ customFields: fields }); }}><Trash2 size={16} /></button></div>)}
      <Field label="New field key" value={fieldKey} onChange={setFieldKey} help="Lowercase letters, numbers and underscores. Example: annual_leave" />
      <Field label="Field value" value={fieldValue} onChange={setFieldValue} />
      <button type="button" className={button} disabled={!fieldKey} onClick={() => {
        if (!/^[a-z][a-z0-9_]{0,49}$/.test(fieldKey) || tokens.includes(fieldKey) || fieldKey in design.customFields) { setError(t("Use a unique lowercase field key. Built-in fields are protected.")); return; }
        change({ customFields: { ...design.customFields, [fieldKey]: fieldValue } }); setFieldKey(''); setFieldValue('');
      }}><Plus size={16} />{t("Add field")}</button>
    </>,
  } : {};

  return <section aria-labelledby="offer-designer-title" className="space-y-4 text-slate-800 dark:text-slate-100">
    <div className="flex flex-wrap items-center justify-between gap-3 bg-slate-50 py-3 dark:bg-slate-950">
      <div><button type="button" className="mb-2 inline-flex min-h-8 items-center gap-1 text-sm text-blue-600 dark:text-blue-300" onClick={() => guard.run('recruitment:offers', onClose)}><ArrowLeft size={16} className="rtl:rotate-180" />{t("Back to offers")}</button><h2 id="offer-designer-title" className="text-2xl font-semibold tracking-tight">{t("Offer letter designer")}</h2><p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t("Your wording. Your brand. One consistent letter.")}</p></div>
      <div className="flex flex-wrap gap-2">{!draftAdapter && <button type="button" className={button} disabled={busy} onClick={() => openTemplate('')}><Plus size={16} />{t("New template")}</button>}<button type="button" className={primary} disabled={!design || busy || !editable || !name.trim() || !!previewError || previewBusy} onClick={() => void save()}><Save size={16} />{t(busy ? 'Working…' : draftAdapter ? 'Save candidate draft' : 'Save template')}</button></div>
    </div>
    {error && <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800">{t(error)}<button type="button" className="ms-3 underline" onClick={() => setError('')}>{t("Dismiss")}</button></div>}
    {notice && <p role="status" className="flex items-start gap-2 rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-800"><Check size={18} className="shrink-0" />{notice}</p>}
    {!design ? <div className="surface p-8"><p>{t(error ? 'The designer could not load. Check the API connection and reopen it.' : 'Loading letter designer…')}</p></div> : <fieldset disabled={busy} aria-busy={busy} className="min-w-0 space-y-4 border-0 p-0">
      {!draftAdapter && <div className="surface grid gap-4 p-4 md:grid-cols-2 xl:grid-cols-[1fr_1fr_auto]">
        <Select label="Template library" value={current?.id ?? ''} options={[[ '', 'New / unsaved template' ], ...templates.map(t => [t.id, `${t.name} · v${t.version}`] as [string, string])]} onChange={openTemplate} />
        <Field label="Template name" value={name} onChange={v => { setName(v.slice(0, 120)); setNotice(''); }} />
        <div className="flex flex-wrap items-end gap-2"><button type="button" className={button} disabled={busy || !canWrite || previewBusy || !!previewError} onClick={() => void save(true)}><Copy size={16} />{t("Save as copy")}</button>{current && editable && <button type="button" className={button} onClick={() => setArchiveConfirm(v => !v)}>{t("Archive")}</button>}</div>
        <p className="text-xs text-slate-500 md:col-span-2 xl:col-span-3">{dirty ? t('Unsaved changes') : current ? t('Saved version {version}', { version: current.version }) : t('Starter design — save to reuse')}{' · '}{current?.companyId ? t('Company template') : current ? t('Shared template') : companyName ?? t(selectedCompanyId ? 'Selected company' : 'Shared across authorized companies')}{t(". Existing issued offers never change when you edit a template.")}</p>
      </div>}
      {archiveConfirm && <div role="alert" className="flex flex-wrap items-center gap-3 rounded-lg border border-amber-300 p-4 text-sm"><p className="flex-1">{t("Archive this template? It will no longer be available for new offers. Existing letters are retained.")}</p><button type="button" className={button} onClick={() => setArchiveConfirm(false)}>{t("Cancel")}</button><button type="button" className={button} disabled={busy} onClick={() => void archive()}>{t("Confirm archive")}</button></div>}
      {!editable && <p role="status" className="rounded-lg bg-amber-50 p-3 text-sm text-amber-800">{t(canWrite ? 'This shared template is read-only in your company scope. Save a copy to customize it for your company.' : 'You can preview templates. Saving requires Admin, HR Manager or Recruiter access.')}</p>}
      <div className="flex gap-2 lg:hidden"><button type="button" className={mobileView === 'edit' ? primary : button} aria-pressed={mobileView === 'edit'} onClick={() => setMobileView('edit')}>{t("Edit design")}</button><button type="button" className={mobileView === 'preview' ? primary : button} aria-pressed={mobileView === 'preview'} onClick={() => setMobileView('preview')}>{t("PDF preview")}</button></div>
      <div className="grid items-start gap-5 lg:grid-cols-[minmax(320px,420px)_minmax(0,1fr)]">
        <div className={`surface min-w-0 ${mobileView !== 'edit' ? 'hidden lg:block' : ''}`}>
          <div role="tablist" aria-label={t("Letter design controls")} className="grid grid-cols-3 gap-1 border-b border-slate-200 p-2 dark:border-slate-700">{(['Content', 'Branding', 'Typography', 'Layout', 'Signatures', 'Merge fields'] as Section[]).map((s, index, sections) => <button key={s} type="button" role="tab" id={`offer-design-tab-${s.replaceAll(' ', '-')}`} aria-controls="offer-design-panel" aria-selected={section === s} tabIndex={section === s ? 0 : -1} onKeyDown={event => {
            let next = index;
            if (event.key === (dir === 'rtl' ? 'ArrowLeft' : 'ArrowRight')) next = (index + 1) % sections.length;
            else if (event.key === (dir === 'rtl' ? 'ArrowRight' : 'ArrowLeft')) next = (index + sections.length - 1) % sections.length;
            else if (event.key === 'Home') next = 0;
            else if (event.key === 'End') next = sections.length - 1;
            else return;
            event.preventDefault(); setSection(sections[next]);
            (event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next])?.focus();
          }} className={`min-h-10 rounded-lg px-2 py-2 text-xs font-medium ${section === s ? 'bg-blue-50 text-blue-700 dark:bg-blue-500/15 dark:text-blue-300' : 'hover:bg-slate-50 dark:hover:bg-slate-800'}`} onClick={() => setSection(s)}>{t(s)}</button>)}</div>
          <div id="offer-design-panel" role="tabpanel" aria-labelledby={`offer-design-tab-${section.replaceAll(' ', '-')}`} className="space-y-5 p-4">
            {section === 'Content' ? <>
              <Select label="Sections in this letter" value={String(selectedBlock)} options={design.blocks.map((block, i) => [String(i), `${i + 1}. ${t(blockLabels[block.kind])}${!block.enabled ? ` (${t('hidden')})` : ''}`])} onChange={v => setSelectedBlock(Number(v))} />
              <div className="flex flex-wrap gap-2"><button type="button" className={button} aria-label={t("Move section up")} disabled={selectedBlock === 0} onClick={() => moveBlock(-1)}><ArrowUp size={16} /></button><button type="button" className={button} aria-label={t("Move section down")} disabled={selectedBlock === design.blocks.length - 1} onClick={() => moveBlock(1)}><ArrowDown size={16} /></button><button type="button" className={button} disabled={design.blocks.length >= 50} onClick={() => { if (!b) return; const blocks = [...design.blocks]; blocks.splice(selectedBlock + 1, 0, { ...structuredClone(b), id: crypto.randomUUID() }); change({ blocks }); setSelectedBlock(selectedBlock + 1); }}><Copy size={16} />{t("Duplicate")}</button><button type="button" className={button} aria-label={t("Delete section")} disabled={design.blocks.length === 1} onClick={() => { change({ blocks: design.blocks.filter((_, i) => i !== selectedBlock) }); setSelectedBlock(Math.max(0, selectedBlock - 1)); }}><Trash2 size={16} /></button></div>
              {b && <>
                <Toggle label="Include this section" checked={b.enabled} onChange={enabled => updateBlock({ enabled })} />
                {b.kind === 'compensation' && <p className="text-sm text-slate-600 dark:text-slate-300">{t('Rows and totals come from the saved offer. Hiding this section changes presentation only; it never removes compensation.')}</p>}
                {['heading', 'text'].includes(b.kind) && <>
                  <label className="block space-y-2 text-sm font-medium"><span>{t(b.kind === 'heading' ? 'Heading text' : 'Wording')}</span><textarea ref={textRef} className={`${input} min-h-44 resize-y`} value={b.text} dir={design.direction} onChange={e => updateBlock({ text: e.target.value })} /></label>
                  <Select label="Insert offer field at cursor" value="" options={[[ '', 'Choose a merge field…' ], ...[...tokens, ...Object.keys(design.customFields)].map(token => [token, t(token.replaceAll('_', ' '))] as [string, string])]} onChange={insertToken} />
                </>}
                {b.kind === 'table' && <div className="space-y-3">{b.rows.map((row, i) => <div key={i} className="rounded-lg border border-slate-200 p-3 dark:border-slate-700"><div className="grid gap-2"><Field label={t('Row {n} label', { n: i + 1 })} value={row.label} onChange={label => updateBlock({ rows: b.rows.map((r, n) => n === i ? { ...r, label } : r) })} /><Field label={t('Row {n} value', { n: i + 1 })} value={row.value} onChange={value => updateBlock({ rows: b.rows.map((r, n) => n === i ? { ...r, value } : r) })} /></div><button type="button" className="mt-2 text-xs text-rose-600 underline" onClick={() => updateBlock({ rows: b.rows.filter((_, n) => n !== i) })}>{t('Remove row {n}', { n: i + 1 })}</button></div>)}<button type="button" className={button} disabled={b.rows.length >= 30} onClick={() => updateBlock({ rows: [...b.rows, { label: '', value: '' }] })}><Plus size={16} />{t("Add table row")}</button><p className="text-xs text-slate-500">{t('Use merge fields such as {token}. See Merge fields for all available values.', { token: '{{gross_salary}}' })}</p></div>}
                {b.kind === 'image' && <><Asset label="Section image" value={b.image} onChange={image => updateBlock({ image })} onError={setError} /><Field label="Image width (pt)" type="number" min={24} max={300} value={b.imageWidth} onChange={v => updateBlock({ imageWidth: Number(v) })} /></>}
                {b.kind === 'signature' && <p className="text-sm text-slate-500">{t("Edit the signer, signature image, stamp and acceptance wording under Signatures.")}</p>}
                {['heading', 'text', 'table', 'image'].includes(b.kind) && <details className="rounded-lg border border-slate-200 p-3 dark:border-slate-700"><summary className="cursor-pointer text-sm font-medium">{t("Section styling")}</summary><div className="mt-4 space-y-4"><Select label="Alignment" value={b.align} options={['left', 'center', 'right', 'justify']} onChange={align => updateBlock({ align })} />{b.kind !== 'image' && <><Field label="Font size override (pt)" type="number" min={8} max={48} value={b.fontSize ?? ''} onChange={v => updateBlock({ fontSize: v ? Number(v) : null })} help="Leave empty to use the template font size." /><div className="flex flex-wrap gap-4"><Toggle label="Bold" checked={b.bold} onChange={bold => updateBlock({ bold })} /><Toggle label="Italic" checked={b.italic} onChange={italic => updateBlock({ italic })} /><Toggle label="Underline" checked={b.underline} onChange={underline => updateBlock({ underline })} /></div><Field label="Text color override" type="color" value={b.color ?? design.textColor} onChange={color => updateBlock({ color })} /><button type="button" className="text-xs underline" onClick={() => updateBlock({ color: null, fontSize: null, bold: false, italic: false, underline: false })}>{t("Use template typography")}</button></>}<Toggle label="Section background" checked={!!b.background} onChange={v => updateBlock({ background: v ? '#F0F4FF' : null })} />{b.background && <Field label="Section background color" type="color" value={b.background} onChange={background => updateBlock({ background })} />}</div></details>}
                {b.kind !== 'pageBreak' && <Field label="Space after section (pt)" type="number" min={0} max={60} value={b.spacing} onChange={v => updateBlock({ spacing: Number(v) })} />}
              </>}
              <div className="space-y-3 border-t border-slate-200 pt-4 dark:border-slate-700"><Select label="New section type" value={blockKind} options={Object.entries(blockLabels) as [string, string][]} onChange={v => setBlockKind(v as OfferDesignBlock['kind'])} /><button type="button" className={button} disabled={design.blocks.length >= 50} onClick={() => { change({ schemaVersion: blockKind === 'compensation' ? 2 : design.schemaVersion, blocks: [...design.blocks, newOfferBlock(blockKind)] }); setSelectedBlock(design.blocks.length); }}><Plus size={16} />{t("Add section")}</button></div>
            </> : controls[section]}
          </div>
        </div>
        <div className={`min-w-0 ${mobileView !== 'preview' ? 'hidden lg:block' : ''}`}>
          <div className="flex flex-wrap items-center justify-between gap-2 rounded-t-xl border border-slate-200 bg-white px-4 py-3 dark:border-slate-700 dark:bg-slate-900"><div><h3 className="flex items-center gap-2 text-sm font-semibold"><FileText size={16} />{t("PDF preview")}</h3><p role="status" aria-live="polite" className="mt-1 text-xs text-slate-500">{t(previewBusy ? 'Updating preview…' : previewError ? 'Preview needs attention' : draftAdapter ? 'Actual candidate · saved terms · unapproved draft preview' : 'Sample data · same renderer as the issued PDF')}</p></div><button type="button" className={button} disabled={!preview || previewBusy || !!previewError} onClick={() => { if (preview) downloadOfferFile(preview.blob, draftAdapter ? `candidate-draft-${draftAdapter.offerId}.pdf` : 'offer-template-sample.pdf'); }}><Download size={16} />{t(draftAdapter ? 'Draft PDF' : 'Sample PDF')}</button></div>
          {previewError && <p role="alert" className="border-x border-rose-200 bg-rose-50 p-3 text-sm text-rose-800">{t(previewError)}{' '}{t("The last valid preview may be shown below.")}</p>}
          <div className="min-h-[540px] overflow-hidden rounded-b-xl border border-t-0 border-slate-200 bg-slate-200 dark:border-slate-700 dark:bg-slate-800">
            {preview ? <iframe title={t(draftAdapter ? 'Actual candidate draft PDF preview' : 'Offer letter PDF preview with sample candidate data')} src={`${preview.url}#toolbar=0&navpanes=0&view=FitH`} className={`h-[72vh] min-h-[540px] w-full ${previewBusy || previewError ? 'opacity-50' : ''}`} /> : <div className="flex min-h-[540px] items-center justify-center p-8 text-sm text-slate-600">{t(previewError ? 'Correct the design to generate a preview.' : 'Preparing your letter preview…')}</div>}
          </div>
          <p className="mt-2 text-xs leading-relaxed text-slate-500">{t(draftAdapter ? 'This is an unapproved candidate draft. Saving updates only this offer revision, never the template.' : "Preview uses fictional candidate and salary data. Actual offers use their saved terms. If your browser cannot display PDFs, use Sample PDF.")}</p>
        </div>
      </div>
      <details className="surface p-4"><summary className="cursor-pointer text-sm font-medium">{t("Import / export a design")}</summary><div className="mt-3 flex flex-wrap items-center gap-3"><button type="button" className={button} onClick={() => downloadOfferFile(new Blob([JSON.stringify(design, null, 2)], { type: 'application/json' }), `${name.replace(/[^a-z0-9_-]/gi, '-') || 'offer-template'}.json`)}>{t("Export template JSON")}</button><label className="text-sm">{t("Import template JSON")}<input type="file" accept=".json,application/json" className="mt-2 block max-w-full text-xs" onChange={e => { void importDesign(e.target.files?.[0]); e.target.value = ''; }} /></label><p className="w-full text-xs text-slate-500">{t("Exported files contain your uploaded logo, font and signature assets. Share only with authorized colleagues. Import never saves automatically.")}</p></div></details>
    </fieldset>}
  </section>;
}
