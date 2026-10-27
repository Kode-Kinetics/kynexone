'use client';

import React, { useEffect, useRef, useState } from 'react';
import {
  CheckCircle2, ChevronDown, ChevronUp, Clock, Download, FileText,
  GripVertical, Loader2, Plus, RefreshCw, Star, X,
} from 'lucide-react';
import { notifyApiError } from '../api/client';
import { OrgRowActions, ShowTurnedOffToggle, useOrgRowRemoval } from '../components/setup/OrgRowActions';
import { msg } from '../i18n/translations';
import {
  DEFAULT_BRANDING, DEFAULT_LAYOUT, PayslipBranding, PayslipLayout,
  PayslipSectionConfig, SectionDef, TemplateDto, TemplateListItem,
  payslipTemplatesApi,
} from '../api/payslipTemplates';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { EnumLabel } from '../components/EnumLabel';
import { Modal } from '../components/Modal';

// ── Utilities ─────────────────────────────────────────────────────────────────

const statusBadge = (status: string) => {
  const map: Record<string, string> = {
    draft:    'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-300',
    active:   'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    off:      'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
    archived: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-caption font-semibold uppercase ${map[status] ?? map.draft}`}>
      <EnumLabel enum="Status" value={status} />
    </span>
  );
};

const ALLOWED_FONTS = ['NotoSans', 'Roboto', 'SourceSansPro', 'Helvetica'];
const LOCALES = [{ value: 'en', label: 'English' }, { value: 'ar', label: 'العربية' }, { value: 'bilingual', label: 'Bilingual (EN + AR)' }] as const;

// ── Style constants ───────────────────────────────────────────────────────────

const surface = 'rounded-xl border border-slate-100 bg-white p-5 shadow-sm dark:border-white/[0.07] dark:bg-slate-900';
const inputCls = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-sapphire/60 focus:ring-2 focus:ring-sapphire/20 dark:border-white/10 dark:bg-slate-800 dark:text-white';
const btnPrimary = 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50';
const btnGhost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const btnDanger = 'inline-flex items-center gap-1.5 rounded-lg border border-rose-200 px-3 py-1.5 text-xs font-medium text-rose-600 hover:bg-rose-50 dark:border-rose-500/20 dark:text-rose-400 dark:hover:bg-rose-500/10';

// ── Setup panel ───────────────────────────────────────────────────────────────

/** The payslip layouts, as the Setup → Payslip templates section (Setup shows the section title). */
export function PayslipTemplatesPanel() {
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const fx = useFormat();
  // The API enforces payroll.write on creating a template (PayslipTemplatesController POST); readers get no create button.
  const mayWrite = hasPermission('payroll.write');
  const [templates, setTemplates] = useState<TemplateListItem[]>([]);
  const [registry, setRegistry] = useState<SectionDef[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<TemplateDto | null>(null); // null = new
  const [showEditor, setShowEditor] = useState(false);
  const [versions, setVersions] = useState<TemplateListItem[]>([]);
  const [showVersions, setShowVersions] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [showOff, setShowOff] = useState(false);

  const reload = async () => {
    setLoading(true);
    const [tmpl, reg] = await Promise.all([
      payslipTemplatesApi.list({ includeTurnedOff: true }).catch((e) => { notifyApiError(e, 'The payslip templates could not be loaded.'); return [] as TemplateListItem[]; }),
      payslipTemplatesApi.registry().catch((e) => { notifyApiError(e, 'The payslip sections could not be loaded.'); return [] as SectionDef[]; }),
    ]);
    setTemplates(tmpl);
    setRegistry(reg);
    setLoading(false);
  };

  useEffect(() => { reload(); }, []);
  const removal = useOrgRowRemoval({ api: payslipTemplatesApi.removal, noun: msg('payslip template'), onDone: () => { void reload(); } });
  const shown = templates.filter((x) => showOff || x.status !== 'off');

  const openNew = () => { setEditing(null); setShowEditor(true); };
  const openEdit = (tpl: TemplateListItem) => {
    payslipTemplatesApi.get(tpl.id).then((dto) => { setEditing(dto); setShowEditor(true); });
  };

  const handleSetDefault = async (id: string) => {
    setBusy(true);
    await payslipTemplatesApi.setDefault(id).catch((e) => notifyApiError(e, 'The default template could not be changed.'));
    await reload();
    setBusy(false);
  };

  const openVersions = async (id: string) => {
    const v = await payslipTemplatesApi.versions(id).catch(() => [] as TemplateListItem[]);
    setVersions(v);
    setShowVersions(id);
  };

  return (
    <div className="flex flex-col gap-6">
      {/* Toolbar */}
      {removal.notice}
      {removal.confirmDialog}
      <div className="flex flex-wrap items-center justify-end gap-3">
        <ShowTurnedOffToggle checked={showOff} onChange={setShowOff} count={templates.filter((x) => x.status === 'off').length} />
        <div className="flex gap-2">
          <button type="button" className={btnGhost} onClick={reload}>
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
            {t('Refresh')}
          </button>
          {mayWrite && (
            <button type="button" className={btnPrimary} onClick={openNew}>
              <Plus className="h-4 w-4" aria-hidden="true" />
              {t('New Template')}
            </button>
          )}
        </div>
      </div>

      {/* Table */}
      <div className={surface + ' overflow-hidden p-0'}>
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <Loader2 className="h-6 w-6 animate-spin text-sapphire" />
          </div>
        ) : templates.length === 0 ? (
          <div className="flex flex-col items-center py-20 text-center">
            <FileText className="mb-3 h-10 w-10 text-slate-300 dark:text-slate-600" />
            <p className="font-medium text-slate-600 dark:text-slate-400">{t('No templates yet')}</p>
            {mayWrite && <p className="mt-1 text-sm text-slate-400">{t('Create your first payslip template to get started.')}</p>}
            {mayWrite && (
              <button type="button" className={btnPrimary + ' mt-4'} onClick={openNew}>
                <Plus className="h-4 w-4" aria-hidden="true" /> {t('Create Template')}
              </button>
            )}
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[t('Name'), t('Status'), t('Version'), t('Locale'), t('Updated'), ''].map((h, i) => (
                  <th key={i} className="px-5 py-3 text-start text-xs font-semibold uppercase tracking-wider text-slate-400">{h || <span className="sr-only">{t('Actions')}</span>}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-50 dark:divide-white/[0.04]">
              {shown.map((tpl) => (
                <tr key={tpl.id} className="group hover:bg-slate-50/60 dark:hover:bg-white/[0.02]">
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-2">
                      {tpl.isDefault && <Star className="h-3.5 w-3.5 fill-amber-400 text-amber-400" role="img" aria-label={t('Default template')} />}
                      <span className="font-medium text-slate-900 dark:text-white">{tpl.name}</span>
                    </div>
                  </td>
                  <td className="px-5 py-3">{statusBadge(tpl.status)}</td>
                  <td className="px-5 py-3 text-slate-500">v{tpl.version}</td>
                  <td className="px-5 py-3 text-slate-500"><EnumLabel enum="Status" value={tpl.status} /></td>
                  <td className="px-5 py-3 text-xs text-slate-400">{fx.date(tpl.updatedAtUtc)}</td>
                  <td className="px-5 py-3">
                    <div className="flex flex-wrap items-center gap-1">
                      {!tpl.isDefault && tpl.status !== 'archived' && (
                        <button type="button" title={t('Set as default')} aria-label={t('Set as default')} className={btnGhost} onClick={() => handleSetDefault(tpl.id)} disabled={busy}>
                          <Star className="h-3.5 w-3.5" aria-hidden="true" />
                        </button>
                      )}
                      <button type="button" title={t('Version history')} aria-label={`${t('Version history')} ${tpl.name}`} className={btnGhost} onClick={() => openVersions(tpl.id)}>
                        <Clock className="h-3.5 w-3.5" aria-hidden="true" />
                      </button>
                      <OrgRowActions name={tpl.name} isActive={tpl.status !== 'off'} busy={removal.busyId === tpl.id}
                        editable={tpl.status !== 'archived' && tpl.status !== 'off'}
                        removeBlockedReason={tpl.isDefault ? t('This is the default template. Set another template as default to remove it.') : undefined}
                        onEdit={() => openEdit(tpl)} onRemove={() => removal.remove({ id: tpl.id, name: tpl.name })} onRestore={() => removal.restore({ id: tpl.id, name: tpl.name })} />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Version history sidebar */}
      {showVersions && (
        <VersionHistoryDrawer
          versions={versions}
          onClose={() => setShowVersions(null)}
          onView={(v) => { setShowVersions(null); openEdit(v); }}
        />
      )}

      {/* Editor drawer */}
      {showEditor && (
        <TemplateEditorDrawer
          initial={editing}
          registry={registry}
          onClose={() => setShowEditor(false)}
          onSaved={() => { setShowEditor(false); reload(); }}
        />
      )}

    </div>
  );
}

// ── Version History Drawer ────────────────────────────────────────────────────

function VersionHistoryDrawer({
  versions, onClose, onView,
}: { versions: TemplateListItem[]; onClose: () => void; onView: (v: TemplateListItem) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  return (
    <Modal isOpen title={t('Version History')} onClose={onClose} closeLabel={t('Close')} placement="end" size="sm">
      <div className="space-y-2">
        {versions.map((v) => (
          <div key={v.id} className="rounded-lg border border-slate-100 dark:border-white/10 p-3 hover:border-sapphire/30">
            <div className="flex items-center justify-between">
              <span className="text-sm font-medium text-slate-900 dark:text-white">v{v.version}</span>
              {statusBadge(v.status)}
            </div>
            <p className="mt-1 text-xs text-slate-400">{fx.date(v.updatedAtUtc)}</p>
            {v.status !== 'archived' && (
              <button type="button" className={btnGhost + ' mt-2'} onClick={() => onView(v)}>
                {t('View')}
              </button>
            )}
          </div>
        ))}
      </div>
    </Modal>
  );
}

// ── Template Editor Drawer ────────────────────────────────────────────────────

type EditorTab = 'branding' | 'layout' | 'preview';

function TemplateEditorDrawer({
  initial, registry, onClose, onSaved,
}: {
  initial: TemplateDto | null;
  registry: SectionDef[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useLocale();
  const isNew = !initial;
  const parseBranding = (json: string): PayslipBranding => { try { return JSON.parse(json); } catch { return DEFAULT_BRANDING; } };
  const parseLayout = (json: string): PayslipLayout => { try { return JSON.parse(json); } catch { return DEFAULT_LAYOUT; } };

  const [name, setName] = useState(initial?.name ?? '');
  const [isDefault, setIsDefault] = useState(initial?.isDefault ?? false);
  const [branding, setBranding] = useState<PayslipBranding>(initial ? parseBranding(initial.brandingJson) : DEFAULT_BRANDING);
  const [layout, setLayout] = useState<PayslipLayout>(initial ? parseLayout(initial.layoutJson) : DEFAULT_LAYOUT);
  const [tab, setTab] = useState<EditorTab>('branding');
  const [saving, setSaving] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const logoInputRef = useRef<HTMLInputElement>(null);

  // Ensure registry sections appear in layout; add missing ones
  useEffect(() => {
    if (registry.length === 0) return;
    setLayout((prev) => {
      const existing = new Set(prev.sections.map((s) => s.key));
      const extra: PayslipSectionConfig[] = registry
        .filter((r) => !existing.has(r.key))
        .map((r, i) => ({ key: r.key, enabled: !r.canDisable, order: prev.sections.length + i + 1, fields: r.fields.filter(f => f.isComplianceLocked).map(f => f.key) }));
      if (extra.length === 0) return prev;
      return { ...prev, sections: [...prev.sections, ...extra] };
    });
  }, [registry]);

  const syncLocale = (loc: string) => {
    setBranding((b) => ({ ...b, locale: loc }));
    setLayout((l) => ({ ...l, locale: loc }));
  };

  const handleLogoUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const url = await payslipTemplatesApi.uploadLogo(file);
      setBranding((b) => ({ ...b, logoStorageUrl: url }));
    } catch { /* ignore upload errors in UI */ }
  };

  const moveSection = (key: string, dir: -1 | 1) => {
    setLayout((l) => {
      const sorted = [...l.sections].sort((a, b) => a.order - b.order);
      const idx = sorted.findIndex((s) => s.key === key);
      const swapIdx = idx + dir;
      if (swapIdx < 0 || swapIdx >= sorted.length) return l;
      [sorted[idx].order, sorted[swapIdx].order] = [sorted[swapIdx].order, sorted[idx].order];
      return { ...l, sections: sorted };
    });
  };

  const toggleSection = (key: string, enabled: boolean) => {
    const reg = registry.find((r) => r.key === key);
    if (reg && !reg.canDisable && !enabled) return; // non-disablable guard
    setLayout((l) => ({
      ...l,
      sections: l.sections.map((s) => s.key === key ? { ...s, enabled } : s),
    }));
  };

  const toggleField = (sectionKey: string, fieldKey: string, include: boolean) => {
    const reg = registry.find((r) => r.key === sectionKey);
    const fd = reg?.fields.find((f) => f.key === fieldKey);
    if (fd?.isComplianceLocked && !include) return; // locked field guard
    setLayout((l) => ({
      ...l,
      sections: l.sections.map((s) => s.key !== sectionKey ? s : {
        ...s,
        fields: include
          ? [...s.fields.filter((f) => f !== fieldKey), fieldKey]
          : s.fields.filter((f) => f !== fieldKey),
      }),
    }));
  };

  const loadPreview = async () => {
    if (!initial) return; // can only preview saved templates
    setPreviewLoading(true);
    try {
      const url = await payslipTemplatesApi.preview(initial.id);
      setPreviewUrl(url);
    } catch { /* ignore */ }
    setPreviewLoading(false);
  };

  const handleSave = async () => {
    setSaving(true);
    setErrors([]);
    try {
      if (isNew) {
        await payslipTemplatesApi.create(name, isDefault, branding, layout);
      } else {
        await payslipTemplatesApi.update(initial!.id, name, isDefault, branding, layout);
      }
      onSaved();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'response' in err) {
        const resp = (err as { response?: { data?: { errors?: string[] } } }).response;
        setErrors(resp?.data?.errors ?? [t('Save failed. Check your configuration.')]);
      } else {
        setErrors([t('Unexpected error. Please try again.')]);
      }
    }
    setSaving(false);
  };

  const sortedSections = [...layout.sections].sort((a, b) => a.order - b.order);

  const TAB_LABEL: Record<EditorTab, string> = { branding: t('Branding'), layout: t('Layout'), preview: t('Preview') };

  return (
    <Modal
      isOpen
      title={isNew ? t('New Template') : t('Edit: {name} (v{version})', { name: initial!.name, version: initial!.version })}
      onClose={onClose}
      closeLabel={t('Close')}
      placement="end"
      size="lg"
      footer={
        <>
          <button type="button" className={btnGhost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btnPrimary} onClick={handleSave} disabled={saving || !name.trim()}>
            {saving ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <CheckCircle2 className="h-4 w-4" aria-hidden="true" />}
            {isNew ? t('Create Template') : t('Save New Version')}
          </button>
        </>
      }
    >
      <div className="-mx-3 -my-3 flex flex-col sm:-mx-5 sm:-my-4">
        {!isNew && <p className="px-5 pt-3 text-xs text-slate-400">{t('Saving creates a new version and archives this one.')}</p>}

        {/* Tabs */}
        <div className="flex border-b border-slate-100 dark:border-white/[0.07]">
          {(['branding', 'layout', 'preview'] as EditorTab[]).map((tabKey) => (
            <button
              key={tabKey}
              type="button"
              onClick={() => { setTab(tabKey); if (tabKey === 'preview') loadPreview(); }}
              className={`px-5 py-3 text-sm font-medium border-b-2 transition-colors ${tab === tabKey
                ? 'border-sapphire text-sapphire'
                : 'border-transparent text-slate-500 hover:text-slate-700 dark:hover:text-slate-300'}`}
            >
              {TAB_LABEL[tabKey]}
              {tabKey === 'preview' && !initial && <span className="ms-1 text-caption text-slate-400">{t('Save first')}</span>}
            </button>
          ))}
        </div>

        {/* Tab content */}
        <div className="flex-1 overflow-y-auto p-5">
          {tab === 'branding' && (
            <div className="space-y-5">
              {/* Name + default */}
              <div className="grid gap-3">
                <label htmlFor="pt-name" className="block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Template Name')}</label>
                <input id="pt-name" className={inputCls} value={name} onChange={(e) => setName(e.target.value)} placeholder={t('Example: KSA Standard')} />
              </div>
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} className="rounded" />
                {t('Set as default template for all payslip downloads')}
              </label>

              <hr className="border-slate-100 dark:border-white/10" />

              {/* Locale */}
              <div>
                <label htmlFor="pt-locale" className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Locale')}</label>
                <select id="pt-locale" className={inputCls} value={branding.locale} onChange={(e) => syncLocale(e.target.value)}>
                  {LOCALES.map((l) => <option key={l.value} value={l.value}>{l.value === 'bilingual' ? t('Bilingual (EN + AR)') : l.label}</option>)}
                </select>
              </div>

              {/* Colors */}
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label htmlFor="pt-primary" className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Primary Color')}</label>
                  <div className="flex items-center gap-2">
                    <input type="color" aria-label={t('Primary Color')} value={branding.primaryColor}
                      onChange={(e) => setBranding((b) => ({ ...b, primaryColor: e.target.value }))}
                      className="h-9 w-12 cursor-pointer rounded border border-slate-200 p-0.5 dark:border-white/10" />
                    <input id="pt-primary" className={inputCls} value={branding.primaryColor}
                      onChange={(e) => setBranding((b) => ({ ...b, primaryColor: e.target.value }))} />
                  </div>
                </div>
                <div>
                  <label htmlFor="pt-accent" className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Accent Color')}</label>
                  <div className="flex items-center gap-2">
                    <input type="color" aria-label={t('Accent Color')} value={branding.accentColor}
                      onChange={(e) => setBranding((b) => ({ ...b, accentColor: e.target.value }))}
                      className="h-9 w-12 cursor-pointer rounded border border-slate-200 p-0.5 dark:border-white/10" />
                    <input id="pt-accent" className={inputCls} value={branding.accentColor}
                      onChange={(e) => setBranding((b) => ({ ...b, accentColor: e.target.value }))} />
                  </div>
                </div>
              </div>

              {/* Font */}
              <div>
                <label htmlFor="pt-font" className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Font Family')}</label>
                <select id="pt-font" className={inputCls} value={branding.fontFamily}
                  onChange={(e) => setBranding((b) => ({ ...b, fontFamily: e.target.value }))}>
                  {ALLOWED_FONTS.map((f) => <option key={f} value={f}>{f}</option>)}
                </select>
              </div>

              {/* Logo */}
              <div>
                <p className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Company Logo')}</p>
                <div className="flex items-center gap-3">
                  {branding.logoStorageUrl && (
                    <span className="truncate rounded bg-slate-100 px-2 py-1 text-xs dark:bg-white/10">
                      {branding.logoStorageUrl.split('/').pop()}
                    </span>
                  )}
                  <button type="button" className={btnGhost} onClick={() => logoInputRef.current?.click()}>
                    <Download className="h-3.5 w-3.5" aria-hidden="true" />
                    {branding.logoStorageUrl ? t('Replace Logo') : t('Upload Logo')}
                  </button>
                  {branding.logoStorageUrl && (
                    <button type="button" className={btnDanger}
                      onClick={() => setBranding((b) => ({ ...b, logoStorageUrl: null }))}>
                      <X className="h-3.5 w-3.5" aria-hidden="true" /> {t('Remove')}
                    </button>
                  )}
                </div>
                <input ref={logoInputRef} aria-label={t('Company Logo')} type="file" accept=".png,.jpg,.jpeg,.svg" hidden onChange={handleLogoUpload} />
                <p className="mt-1 text-xs text-slate-400">{t('PNG, JPG or SVG, max 2 MB. Appears in PDF header.')}</p>
              </div>

              {/* Header/footer text */}
              <div className="grid gap-3">
                <label htmlFor="pt-hen" className="block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Header Text (EN)')}</label>
                <input className={inputCls} maxLength={200} id="pt-hen" value={branding.headerTextEn}
                  onChange={(e) => setBranding((b) => ({ ...b, headerTextEn: e.target.value }))}
                  placeholder={t('Example: Confidential payslip for')} />
              </div>
              {(branding.locale === 'ar' || branding.locale === 'bilingual') && (
                <div className="grid gap-3">
                  <label htmlFor="pt-har" className="block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Header Text (AR)')}</label>
                  <input dir="rtl" className={inputCls} maxLength={200} id="pt-har" value={branding.headerTextAr}
                    onChange={(e) => setBranding((b) => ({ ...b, headerTextAr: e.target.value }))}
                    placeholder="مثال: كشف راتب سري لـ:" />
                </div>
              )}
              <div className="grid gap-3">
                <label htmlFor="pt-fen" className="block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Footer Text (EN)')}</label>
                <input className={inputCls} maxLength={200} id="pt-fen" value={branding.footerTextEn}
                  onChange={(e) => setBranding((b) => ({ ...b, footerTextEn: e.target.value }))}
                  placeholder={t('Example: This is a system-generated payslip.')} />
              </div>
              {(branding.locale === 'ar' || branding.locale === 'bilingual') && (
                <div className="grid gap-3">
                  <label htmlFor="pt-far" className="block text-xs font-semibold uppercase tracking-wider text-slate-500">{t('Footer Text (AR)')}</label>
                  <input dir="rtl" className={inputCls} maxLength={200} id="pt-far" value={branding.footerTextAr}
                    onChange={(e) => setBranding((b) => ({ ...b, footerTextAr: e.target.value }))}
                    placeholder="مثال: هذا كشف راتب صادر من النظام." />
                </div>
              )}
            </div>
          )}

          {tab === 'layout' && (
            <div className="space-y-4">
              <p className="text-sm text-slate-500 dark:text-slate-400">
                {t('Toggle sections on/off and choose which fields appear. Compliance-locked fields cannot be removed.')}
              </p>
              {sortedSections.map((sec) => {
                const def = registry.find((r) => r.key === sec.key);
                if (!def) return null;
                return (
                  <SectionCard
                    key={sec.key}
                    sec={sec}
                    def={def}
                    onToggle={(en) => toggleSection(sec.key, en)}
                    onMoveUp={() => moveSection(sec.key, -1)}
                    onMoveDown={() => moveSection(sec.key, 1)}
                    onToggleField={(fk, inc) => toggleField(sec.key, fk, inc)}
                    isFirst={sortedSections[0]?.key === sec.key}
                    isLast={sortedSections[sortedSections.length - 1]?.key === sec.key}
                  />
                );
              })}
            </div>
          )}

          {tab === 'preview' && (
            <div className="flex flex-col gap-4">
              <div className="flex items-center gap-3">
                <button type="button" className={btnGhost} onClick={loadPreview} disabled={!initial || previewLoading}>
                  {previewLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />}
                  {t('Regenerate Preview')}
                </button>
                {previewUrl && (
                  <a href={previewUrl} download="preview.pdf" className={btnGhost}>
                    <Download className="h-3.5 w-3.5" aria-hidden="true" /> {t('Download')}
                  </a>
                )}
              </div>
              {!initial && (
                <div className="rounded-lg border border-slate-200 p-8 text-center text-sm text-slate-400 dark:border-white/10">
                  {t('Save the template first to generate a preview.')}
                </div>
              )}
              {previewLoading && (
                <div className="flex items-center justify-center py-16">
                  <Loader2 className="h-8 w-8 animate-spin text-sapphire" />
                </div>
              )}
              {previewUrl && !previewLoading && (
                <iframe src={previewUrl} className="h-[600px] w-full rounded-lg border border-slate-200 dark:border-white/10" title={t('Payslip Preview')} />
              )}
            </div>
          )}

          {/* Validation errors */}
          {errors.length > 0 && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 p-4 dark:border-rose-500/20 dark:bg-rose-500/10">
              <p className="mb-1 text-xs font-semibold text-rose-600 dark:text-rose-400">{t('Validation errors')}</p>
              <ul className="list-inside list-disc space-y-0.5">
                {errors.map((e, i) => <li key={i} className="text-xs text-rose-600 dark:text-rose-400">{e}</li>)}
              </ul>
            </div>
          )}
        </div>

      </div>
    </Modal>
  );
}

// ── Section Card (layout tab) ─────────────────────────────────────────────────

function SectionCard({
  sec, def, onToggle, onMoveUp, onMoveDown, onToggleField, isFirst, isLast,
}: {
  sec: PayslipSectionConfig;
  def: SectionDef;
  onToggle: (enabled: boolean) => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onToggleField: (key: string, include: boolean) => void;
  isFirst: boolean;
  isLast: boolean;
}) {
  const { t } = useLocale();
  const [expanded, setExpanded] = useState(sec.enabled);

  return (
    <div className={`rounded-lg border transition-colors ${sec.enabled ? 'border-sapphire/20 bg-sapphire/5 dark:bg-sapphire/10' : 'border-slate-100 bg-slate-50/60 dark:border-white/[0.07] dark:bg-white/[0.02]'}`}>
      <div className="flex items-center gap-3 px-4 py-3">
        {/* Reorder buttons */}
        <div className="flex flex-col gap-0.5">
          <button type="button" aria-label={t('Move up')} onClick={onMoveUp} disabled={isFirst} className="text-slate-300 hover:text-slate-500 disabled:opacity-20">
            <ChevronUp className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
          <button type="button" aria-label={t('Move down')} onClick={onMoveDown} disabled={isLast} className="text-slate-300 hover:text-slate-500 disabled:opacity-20">
            <ChevronDown className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
        </div>

        <GripVertical className="h-4 w-4 text-slate-300" aria-hidden="true" />

        {/* Toggle */}
        <input
          type="checkbox"
          aria-label={def.labelEn}
          checked={sec.enabled}
          disabled={!def.canDisable}
          onChange={(e) => { onToggle(e.target.checked); setExpanded(e.target.checked); }}
          className="rounded accent-sapphire"
        />

        {/* Section name */}
        <div className="flex-1">
          <span className="text-sm font-medium text-slate-800 dark:text-white">{def.labelEn}</span>
          {def.labelAr && <span className="ms-2 text-xs text-slate-400">{def.labelAr}</span>}
          {!def.canDisable && (
            <span className="ms-2 rounded bg-rose-50 px-1 py-0.5 text-caption font-semibold text-rose-500 dark:bg-rose-500/10">
              {t('Required')}
            </span>
          )}
        </div>

        {sec.enabled && (
          <button type="button" aria-expanded={expanded} aria-label={expanded ? t('Collapse fields') : t('Expand fields')} onClick={() => setExpanded((x) => !x)} className="text-slate-400 hover:text-slate-600">
            {expanded ? <ChevronUp className="h-4 w-4" aria-hidden="true" /> : <ChevronDown className="h-4 w-4" aria-hidden="true" />}
          </button>
        )}
      </div>

      {sec.enabled && expanded && (
        <div className="border-t border-slate-100 px-4 pb-3 pt-2 dark:border-white/[0.07]">
          <p className="mb-2 text-caption font-semibold uppercase tracking-wider text-slate-400">{t('Fields')}</p>
          <div className="grid grid-cols-2 gap-x-4 gap-y-1">
            {def.fields.map((f) => (
              <label key={f.key} className={`flex items-center gap-2 rounded py-0.5 text-xs ${f.isComplianceLocked ? 'cursor-not-allowed opacity-70' : 'cursor-pointer'}`}>
                <input
                  type="checkbox"
                  checked={sec.fields.includes(f.key)}
                  disabled={f.isComplianceLocked}
                  onChange={(e) => onToggleField(f.key, e.target.checked)}
                  className="rounded accent-sapphire"
                />
                <span className="text-slate-700 dark:text-slate-300">{f.labelEn}</span>
                {f.isComplianceLocked && (
                  <span className="rounded bg-rose-50 px-1 text-caption font-semibold text-rose-500 dark:bg-rose-500/10">{t('Locked')}</span>
                )}
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
