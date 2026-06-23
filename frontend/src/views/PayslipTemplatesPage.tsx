'use client';

import React, { useEffect, useRef, useState } from 'react';
import {
  CheckCircle2, ChevronDown, ChevronUp, Clock, Download, FileText,
  GripVertical, Loader2, Plus, RefreshCw, Settings2, Star, Trash2, X,
} from 'lucide-react';
import {
  DEFAULT_BRANDING, DEFAULT_LAYOUT, PayslipBranding, PayslipLayout,
  PayslipSectionConfig, SectionDef, TemplateDto, TemplateListItem,
  payslipTemplatesApi,
} from '../api/payslipTemplates';

// ── Utilities ─────────────────────────────────────────────────────────────────

const fmtDate = (iso: string) =>
  new Date(iso).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

const statusBadge = (status: string) => {
  const map: Record<string, string> = {
    draft:    'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-300',
    active:   'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    archived: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
  };
  return (
    <span className={`rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase ${map[status] ?? map.draft}`}>
      {status}
    </span>
  );
};

const ALLOWED_FONTS = ['NotoSans', 'Roboto', 'SourceSansPro', 'Helvetica'];
const LOCALES = [{ value: 'en', label: 'English' }, { value: 'ar', label: 'العربية' }, { value: 'bilingual', label: 'Bilingual (EN + AR)' }];

// ── Style constants ───────────────────────────────────────────────────────────

const surface = 'rounded-xl border border-slate-100 bg-white p-5 shadow-sm dark:border-white/[0.07] dark:bg-slate-900';
const inputCls = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-sapphire/60 focus:ring-2 focus:ring-sapphire/20 dark:border-white/10 dark:bg-slate-800 dark:text-white';
const btnPrimary = 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50';
const btnGhost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const btnDanger = 'inline-flex items-center gap-1.5 rounded-lg border border-rose-200 px-3 py-1.5 text-xs font-medium text-rose-600 hover:bg-rose-50 dark:border-rose-500/20 dark:text-rose-400 dark:hover:bg-rose-500/10';

// ── Main page ─────────────────────────────────────────────────────────────────

export function PayslipTemplatesPage() {
  const [templates, setTemplates] = useState<TemplateListItem[]>([]);
  const [registry, setRegistry] = useState<SectionDef[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<TemplateDto | null>(null); // null = new
  const [showEditor, setShowEditor] = useState(false);
  const [versions, setVersions] = useState<TemplateListItem[]>([]);
  const [showVersions, setShowVersions] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const reload = async () => {
    setLoading(true);
    const [tmpl, reg] = await Promise.all([
      payslipTemplatesApi.list().catch(() => [] as TemplateListItem[]),
      payslipTemplatesApi.registry().catch(() => [] as SectionDef[]),
    ]);
    setTemplates(tmpl);
    setRegistry(reg);
    setLoading(false);
  };

  useEffect(() => { reload(); }, []);

  const openNew = () => { setEditing(null); setShowEditor(true); };
  const openEdit = (t: TemplateListItem) => {
    payslipTemplatesApi.get(t.id).then((dto) => { setEditing(dto); setShowEditor(true); });
  };

  const handleSetDefault = async (id: string) => {
    setBusy(true);
    await payslipTemplatesApi.setDefault(id).catch(() => {});
    await reload();
    setBusy(false);
  };

  const handleDelete = async (id: string) => {
    if (!confirm('Delete this template? This cannot be undone.')) return;
    await payslipTemplatesApi.delete(id).catch(() => {});
    await reload();
  };

  const openVersions = async (id: string) => {
    const v = await payslipTemplatesApi.versions(id).catch(() => [] as TemplateListItem[]);
    setVersions(v);
    setShowVersions(id);
  };

  return (
    <div className="flex h-full flex-col gap-6 p-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-slate-900 dark:text-white">Payslip Templates</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            Configure branded, compliance-checked payslip layouts for your organisation.
          </p>
        </div>
        <div className="flex gap-2">
          <button type="button" className={btnGhost} onClick={reload}>
            <RefreshCw className="h-3.5 w-3.5" />
            Refresh
          </button>
          <button type="button" className={btnPrimary} onClick={openNew}>
            <Plus className="h-4 w-4" />
            New Template
          </button>
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
            <p className="font-medium text-slate-600 dark:text-slate-400">No templates yet</p>
            <p className="mt-1 text-sm text-slate-400">Create your first payslip template to get started.</p>
            <button type="button" className={btnPrimary + ' mt-4'} onClick={openNew}>
              <Plus className="h-4 w-4" /> Create Template
            </button>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Name', 'Status', 'Version', 'Locale', 'Updated', ''].map((h) => (
                  <th key={h} className="px-5 py-3 text-left text-xs font-bold uppercase tracking-wider text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-50 dark:divide-white/[0.04]">
              {templates.map((t) => (
                <tr key={t.id} className="group hover:bg-slate-50/60 dark:hover:bg-white/[0.02]">
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-2">
                      {t.isDefault && <Star className="h-3.5 w-3.5 fill-amber-400 text-amber-400" />}
                      <span className="font-medium text-slate-900 dark:text-white">{t.name}</span>
                    </div>
                  </td>
                  <td className="px-5 py-3">{statusBadge(t.status)}</td>
                  <td className="px-5 py-3 text-slate-500">v{t.version}</td>
                  <td className="px-5 py-3 text-slate-500">{t.status}</td>
                  <td className="px-5 py-3 text-xs text-slate-400">{fmtDate(t.updatedAtUtc)}</td>
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                      {!t.isDefault && t.status !== 'archived' && (
                        <button type="button" title="Set as default" className={btnGhost} onClick={() => handleSetDefault(t.id)} disabled={busy}>
                          <Star className="h-3.5 w-3.5" />
                        </button>
                      )}
                      {t.status !== 'archived' && (
                        <button type="button" title="Edit" className={btnGhost} onClick={() => openEdit(t)}>
                          <Settings2 className="h-3.5 w-3.5" />
                          Edit
                        </button>
                      )}
                      <button type="button" title="Version history" className={btnGhost} onClick={() => openVersions(t.id)}>
                        <Clock className="h-3.5 w-3.5" />
                      </button>
                      {!t.isDefault && (
                        <button type="button" title="Delete" className={btnDanger} onClick={() => handleDelete(t.id)}>
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      )}
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
  return (
    <div className="fixed inset-0 z-50 flex">
      <div className="absolute inset-0 bg-black/30" onClick={onClose} />
      <div className="relative ml-auto h-full w-80 overflow-y-auto bg-white shadow-2xl dark:bg-slate-900 p-5">
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-semibold text-slate-900 dark:text-white">Version History</h3>
          <button type="button" onClick={onClose}><X className="h-4 w-4 text-slate-400" /></button>
        </div>
        <div className="space-y-2">
          {versions.map((v) => (
            <div key={v.id} className="rounded-lg border border-slate-100 dark:border-white/10 p-3 hover:border-sapphire/30">
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium text-slate-900 dark:text-white">v{v.version}</span>
                {statusBadge(v.status)}
              </div>
              <p className="mt-1 text-xs text-slate-400">{fmtDate(v.updatedAtUtc)}</p>
              {v.status !== 'archived' && (
                <button type="button" className={btnGhost + ' mt-2'} onClick={() => onView(v)}>
                  View
                </button>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
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
        setErrors(resp?.data?.errors ?? ['Save failed. Check your configuration.']);
      } else {
        setErrors(['Unexpected error. Please try again.']);
      }
    }
    setSaving(false);
  };

  const sortedSections = [...layout.sections].sort((a, b) => a.order - b.order);

  return (
    <div className="fixed inset-0 z-50 flex">
      <div className="absolute inset-0 bg-black/30" onClick={onClose} />
      <div className="relative ml-auto flex h-full w-[640px] flex-col bg-white shadow-2xl dark:bg-slate-900">
        {/* Drawer header */}
        <div className="flex items-center justify-between border-b border-slate-100 px-5 py-4 dark:border-white/[0.07]">
          <div>
            <h2 className="font-semibold text-slate-900 dark:text-white">
              {isNew ? 'New Template' : `Edit: ${initial!.name} (v${initial!.version})`}
            </h2>
            {!isNew && <p className="text-xs text-slate-400">Saving creates a new version and archives this one.</p>}
          </div>
          <button type="button" onClick={onClose}><X className="h-4 w-4 text-slate-400" /></button>
        </div>

        {/* Tabs */}
        <div className="flex border-b border-slate-100 dark:border-white/[0.07]">
          {(['branding', 'layout', 'preview'] as EditorTab[]).map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => { setTab(t); if (t === 'preview') loadPreview(); }}
              className={`px-5 py-3 text-sm font-medium border-b-2 transition-colors ${tab === t
                ? 'border-sapphire text-sapphire'
                : 'border-transparent text-slate-500 hover:text-slate-700 dark:hover:text-slate-300'}`}
            >
              {t.charAt(0).toUpperCase() + t.slice(1)}
              {t === 'preview' && !initial && <span className="ml-1 text-[10px] text-slate-400">(save first)</span>}
            </button>
          ))}
        </div>

        {/* Tab content */}
        <div className="flex-1 overflow-y-auto p-5">
          {tab === 'branding' && (
            <div className="space-y-5">
              {/* Name + default */}
              <div className="grid gap-3">
                <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500">Template Name</label>
                <input className={inputCls} value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. KSA Standard" />
              </div>
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                <input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} className="rounded" />
                Set as default template for all payslip downloads
              </label>

              <hr className="border-slate-100 dark:border-white/10" />

              {/* Locale */}
              <div>
                <label className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">Locale</label>
                <select className={inputCls} value={branding.locale} onChange={(e) => syncLocale(e.target.value)}>
                  {LOCALES.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
                </select>
              </div>

              {/* Colors */}
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">Primary Color</label>
                  <div className="flex items-center gap-2">
                    <input type="color" value={branding.primaryColor}
                      onChange={(e) => setBranding((b) => ({ ...b, primaryColor: e.target.value }))}
                      className="h-9 w-12 cursor-pointer rounded border border-slate-200 p-0.5 dark:border-white/10" />
                    <input className={inputCls} value={branding.primaryColor}
                      onChange={(e) => setBranding((b) => ({ ...b, primaryColor: e.target.value }))} />
                  </div>
                </div>
                <div>
                  <label className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">Accent Color</label>
                  <div className="flex items-center gap-2">
                    <input type="color" value={branding.accentColor}
                      onChange={(e) => setBranding((b) => ({ ...b, accentColor: e.target.value }))}
                      className="h-9 w-12 cursor-pointer rounded border border-slate-200 p-0.5 dark:border-white/10" />
                    <input className={inputCls} value={branding.accentColor}
                      onChange={(e) => setBranding((b) => ({ ...b, accentColor: e.target.value }))} />
                  </div>
                </div>
              </div>

              {/* Font */}
              <div>
                <label className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">Font Family</label>
                <select className={inputCls} value={branding.fontFamily}
                  onChange={(e) => setBranding((b) => ({ ...b, fontFamily: e.target.value }))}>
                  {ALLOWED_FONTS.map((f) => <option key={f} value={f}>{f}</option>)}
                </select>
              </div>

              {/* Logo */}
              <div>
                <label className="mb-1 block text-xs font-semibold uppercase tracking-wider text-slate-500">Company Logo</label>
                <div className="flex items-center gap-3">
                  {branding.logoStorageUrl && (
                    <span className="truncate rounded bg-slate-100 px-2 py-1 text-xs dark:bg-white/10">
                      {branding.logoStorageUrl.split('/').pop()}
                    </span>
                  )}
                  <button type="button" className={btnGhost} onClick={() => logoInputRef.current?.click()}>
                    <Download className="h-3.5 w-3.5" />
                    {branding.logoStorageUrl ? 'Replace Logo' : 'Upload Logo'}
                  </button>
                  {branding.logoStorageUrl && (
                    <button type="button" className={btnDanger}
                      onClick={() => setBranding((b) => ({ ...b, logoStorageUrl: null }))}>
                      <X className="h-3.5 w-3.5" /> Remove
                    </button>
                  )}
                </div>
                <input ref={logoInputRef} type="file" accept=".png,.jpg,.jpeg,.svg" hidden onChange={handleLogoUpload} />
                <p className="mt-1 text-xs text-slate-400">PNG, JPG or SVG, max 2 MB. Appears in PDF header.</p>
              </div>

              {/* Header/footer text */}
              <div className="grid gap-3">
                <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500">Header Text (EN)</label>
                <input className={inputCls} maxLength={200} value={branding.headerTextEn}
                  onChange={(e) => setBranding((b) => ({ ...b, headerTextEn: e.target.value }))}
                  placeholder="e.g. Confidential payslip for:" />
              </div>
              {(branding.locale === 'ar' || branding.locale === 'bilingual') && (
                <div className="grid gap-3">
                  <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500">Header Text (AR)</label>
                  <input dir="rtl" className={inputCls} maxLength={200} value={branding.headerTextAr}
                    onChange={(e) => setBranding((b) => ({ ...b, headerTextAr: e.target.value }))}
                    placeholder="مثال: كشف راتب سري لـ:" />
                </div>
              )}
              <div className="grid gap-3">
                <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500">Footer Text (EN)</label>
                <input className={inputCls} maxLength={200} value={branding.footerTextEn}
                  onChange={(e) => setBranding((b) => ({ ...b, footerTextEn: e.target.value }))}
                  placeholder="e.g. This is a system-generated payslip." />
              </div>
              {(branding.locale === 'ar' || branding.locale === 'bilingual') && (
                <div className="grid gap-3">
                  <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500">Footer Text (AR)</label>
                  <input dir="rtl" className={inputCls} maxLength={200} value={branding.footerTextAr}
                    onChange={(e) => setBranding((b) => ({ ...b, footerTextAr: e.target.value }))}
                    placeholder="مثال: هذا كشف راتب صادر من النظام." />
                </div>
              )}
            </div>
          )}

          {tab === 'layout' && (
            <div className="space-y-4">
              <p className="text-sm text-slate-500 dark:text-slate-400">
                Toggle sections on/off and choose which fields appear. Compliance-locked fields
                <span className="mx-1 rounded bg-rose-50 px-1 py-0.5 text-[10px] font-semibold text-rose-600 dark:bg-rose-500/10">locked</span>
                cannot be removed.
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
                  {previewLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                  Regenerate Preview
                </button>
                {previewUrl && (
                  <a href={previewUrl} download="preview.pdf" className={btnGhost}>
                    <Download className="h-3.5 w-3.5" /> Download
                  </a>
                )}
              </div>
              {!initial && (
                <div className="rounded-lg border border-slate-200 p-8 text-center text-sm text-slate-400 dark:border-white/10">
                  Save the template first to generate a preview.
                </div>
              )}
              {previewLoading && (
                <div className="flex items-center justify-center py-16">
                  <Loader2 className="h-8 w-8 animate-spin text-sapphire" />
                </div>
              )}
              {previewUrl && !previewLoading && (
                <iframe src={previewUrl} className="h-[600px] w-full rounded-lg border border-slate-200 dark:border-white/10" title="Payslip Preview" />
              )}
            </div>
          )}

          {/* Validation errors */}
          {errors.length > 0 && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 p-4 dark:border-rose-500/20 dark:bg-rose-500/10">
              <p className="mb-1 text-xs font-semibold text-rose-600 dark:text-rose-400">Validation errors:</p>
              <ul className="list-inside list-disc space-y-0.5">
                {errors.map((e, i) => <li key={i} className="text-xs text-rose-600 dark:text-rose-400">{e}</li>)}
              </ul>
            </div>
          )}
        </div>

        {/* Footer actions */}
        <div className="flex items-center justify-between border-t border-slate-100 px-5 py-4 dark:border-white/[0.07]">
          <button type="button" className={btnGhost} onClick={onClose}>Cancel</button>
          <button type="button" className={btnPrimary} onClick={handleSave} disabled={saving || !name.trim()}>
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
            {isNew ? 'Create Template' : 'Save New Version'}
          </button>
        </div>
      </div>
    </div>
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
  const [expanded, setExpanded] = useState(sec.enabled);

  return (
    <div className={`rounded-lg border transition-colors ${sec.enabled ? 'border-sapphire/20 bg-sapphire/5 dark:bg-sapphire/10' : 'border-slate-100 bg-slate-50/60 dark:border-white/[0.07] dark:bg-white/[0.02]'}`}>
      <div className="flex items-center gap-3 px-4 py-3">
        {/* Reorder buttons */}
        <div className="flex flex-col gap-0.5">
          <button type="button" onClick={onMoveUp} disabled={isFirst} className="text-slate-300 hover:text-slate-500 disabled:opacity-20">
            <ChevronUp className="h-3.5 w-3.5" />
          </button>
          <button type="button" onClick={onMoveDown} disabled={isLast} className="text-slate-300 hover:text-slate-500 disabled:opacity-20">
            <ChevronDown className="h-3.5 w-3.5" />
          </button>
        </div>

        <GripVertical className="h-4 w-4 text-slate-300" />

        {/* Toggle */}
        <input
          type="checkbox"
          checked={sec.enabled}
          disabled={!def.canDisable}
          onChange={(e) => { onToggle(e.target.checked); setExpanded(e.target.checked); }}
          className="rounded accent-sapphire"
        />

        {/* Section name */}
        <div className="flex-1">
          <span className="text-sm font-medium text-slate-800 dark:text-white">{def.labelEn}</span>
          {def.labelAr && <span className="ml-2 text-xs text-slate-400">{def.labelAr}</span>}
          {!def.canDisable && (
            <span className="ml-2 rounded bg-rose-50 px-1 py-0.5 text-[9px] font-bold text-rose-500 dark:bg-rose-500/10">
              required
            </span>
          )}
        </div>

        {sec.enabled && (
          <button type="button" onClick={() => setExpanded((x) => !x)} className="text-slate-400 hover:text-slate-600">
            {expanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          </button>
        )}
      </div>

      {sec.enabled && expanded && (
        <div className="border-t border-slate-100 px-4 pb-3 pt-2 dark:border-white/[0.07]">
          <p className="mb-2 text-[10px] font-bold uppercase tracking-wider text-slate-400">Fields</p>
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
                  <span className="rounded bg-rose-50 px-1 text-[9px] font-bold text-rose-500 dark:bg-rose-500/10">locked</span>
                )}
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
