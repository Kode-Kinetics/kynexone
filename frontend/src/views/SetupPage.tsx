'use client';

import { useCallback, useEffect, useState } from 'react';
import { notifyApiError } from '../api/client';
import { Award, Building2, GitBranch, Layers, Landmark, Tag, Plus, Pencil, Trash2, Database, Hash, Settings, Globe, Calendar, MapPin, Bell, ClipboardList, ChevronRight } from 'lucide-react';
import {
  companiesApi,
  branchesApi,
  departmentsApi,
  designationsApi,
  gradesApi,
  costCentersApi,
} from '../api/organization';
import { countryPacksApi, statutoryRulesApi } from '../api/countryPacks';
import type { CountryPackOption, StatutorySummary } from '../api/countryPacks';
import type {
  CompanyDto,
  CompanyRequest,
  BranchDto,
  BranchRequest,
  DepartmentDto,
  DepartmentRequest,
  DesignationDto,
  DesignationRequest,
  GradeDto,
  GradeRequest,
  CostCenterDto,
  CostCenterRequest,
} from '../api/organization';
import {
  masterDataApi,
  numberingRulesApi,
  systemSettingsApi,
  gccSettingsApi,
  fiscalYearsApi,
  locationsApi,
  notificationTemplatesApi,
  adminAuditApi,
} from '../api/setup';
import type {
  MasterDataType,
  MasterDataValue,
  NumberingRule,
  SystemSetting,
  GCCComplianceSetting,
  Location as AdminLocation,
  FiscalYear,
  NotificationTemplate,
  AdminAuditLog,
} from '../api/setup';
import { Modal } from '../components/Modal';
import { useAutoTranslate } from '../hooks/useAutoTranslate';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

type Tab = 'companies' | 'branches' | 'departments' | 'designations' | 'grades' | 'costCenters'
  | 'masterData' | 'numberingRules' | 'systemSettings' | 'gccSettings'
  | 'fiscalYears' | 'locations' | 'notificationTemplates' | 'emailConfig' | 'adminAuditLogs';

const tabs: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'companies', label: 'Companies', icon: Building2 },
  { id: 'branches', label: 'Branches', icon: GitBranch },
  { id: 'departments', label: 'Departments', icon: Layers },
  { id: 'designations', label: 'Designations', icon: Tag },
  { id: 'grades', label: 'Grades', icon: Award },
  { id: 'costCenters', label: 'Cost Centers', icon: Landmark },
  { id: 'masterData', label: 'Master Data', icon: Database },
  { id: 'numberingRules', label: 'Numbering', icon: Hash },
  { id: 'systemSettings', label: 'System Settings', icon: Settings },
  { id: 'gccSettings', label: 'GCC Settings', icon: Globe },
  { id: 'fiscalYears', label: 'Fiscal Years', icon: Calendar },
  { id: 'locations', label: 'Locations', icon: MapPin },
  { id: 'notificationTemplates', label: 'Notifications', icon: Bell },
  { id: 'emailConfig', label: 'Email / SMTP', icon: Settings },
  { id: 'adminAuditLogs', label: 'Audit Logs', icon: ClipboardList },
];

// Small read-only field for the statutory pack profile panel.
function InfoRow({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div>
      <p className="text-xs font-medium text-slate-500 dark:text-slate-400">{label}</p>
      <p className="text-sm text-slate-800 dark:text-slate-100">{value}</p>
      {detail && <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400 leading-snug">{detail}</p>}
    </div>
  );
}

// ─── Companies ───────────────────────────────────────────────────────────────

function CompaniesTab() {
  const { currencyCode } = useTenantSettings();
  const [items, setItems] = useState<CompanyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CompanyDto | null>(null);
  const [form, setForm] = useState<CompanyRequest>(emptyCompany());
  const [saving, setSaving] = useState(false);
  const [availablePacks, setAvailablePacks] = useState<CountryPackOption[]>([]);
  const [statutorySummary, setStatutorySummary] = useState<StatutorySummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await companiesApi.list(); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    countryPacksApi.available().then(setAvailablePacks).catch(() => {});
  }, []);

  const loadStatutorySummary = async (companyId: string) => {
    setSummaryLoading(true);
    setStatutorySummary(null);
    try { setStatutorySummary(await countryPacksApi.statutorySummary(companyId)); }
    catch { /**/ }
    finally { setSummaryLoading(false); }
  };

  const openNew = () => { setEditing(null); setForm(emptyCompany()); setError(''); setModalOpen(true); };
  const openEdit = (c: CompanyDto) => {
    setEditing(c);
    setForm({ legalNameEn: c.legalNameEn, legalNameAr: c.legalNameAr, tradeName: c.tradeName, countryCode: c.countryCode, jurisdiction: c.jurisdiction, registrationNumber: c.registrationNumber, taxNumber: c.taxNumber, wpsEmployerId: c.wpsEmployerId, gosiEmployerId: c.gosiEmployerId, qiwaEstablishmentId: c.qiwaEstablishmentId, defaultCurrency: c.defaultCurrency, isActive: c.isActive });
    setError('');
    setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.legalNameEn.trim()) { setError('Legal name (English) is required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await companiesApi.update(editing.id, form);
      else await companiesApi.create(form);
      setModalOpen(false);
      load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };

  const f = (key: keyof CompanyRequest, v: string | boolean) => setForm((x) => ({ ...x, [key]: v }));
  const { translation: autoLegalNameAr, isTranslating: translatingLegalNameAr } = useAutoTranslate(form.legalNameEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoLegalNameAr && !form.legalNameAr) f('legalNameAr', autoLegalNameAr); }, [autoLegalNameAr]);
  const deleteCompany = async (id: string) => {
    if (!confirm('Delete this company? This cannot be undone.')) return;
    setDeleting(id);
    try { await companiesApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell
        columns={['Legal Name', 'Trade Name', 'Country', 'Currency', 'Status']}
        onAdd={openNew}
        addLabel="Add Company"
        loading={loading}
        empty={items.length === 0}
        emptyLabel="No companies yet"
        actions={
          <ImportExportToolbar
            entityName="Companies"
            onExport={async () => { const csv = await companiesApi.export(); downloadCsv(csv, 'companies.csv'); }}
            onDownloadTemplate={async () => { const csv = await companiesApi.importTemplate(); downloadCsv(csv, 'companies-template.csv'); }}
            onImport={async (csv) => { const r = await companiesApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {items.map((c) => (
          <tr key={c.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{c.legalNameEn}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.tradeName || '—'}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">
              {c.countryCode || '—'}
              {c.jurisdiction && <span className="ml-1 text-xs text-slate-400">({c.jurisdiction})</span>}
            </td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.defaultCurrency}</td>
            <td className="px-4 py-3">
              <ActiveBadge active={c.isActive} />
            </td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(c)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => loadStatutorySummary(c.id)} className="btn-secondary h-7 px-2 text-xs" title="View statutory pack profile"><Globe className="h-3 w-3" /></button>
                <button type="button" onClick={() => deleteCompany(c.id)} disabled={deleting === c.id} aria-label="Delete company" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      {/* Statutory Pack Profile panel — shown when a company's globe icon is clicked */}
      {(summaryLoading || statutorySummary) && (
        <div className="mt-4 rounded-xl border border-indigo-200 bg-indigo-50/60 p-5 dark:border-indigo-800/40 dark:bg-indigo-900/20">
          {summaryLoading && <p className="text-sm text-slate-500">Loading statutory profile…</p>}
          {statutorySummary && (
            <>
              <div className="mb-3 flex items-center justify-between">
                <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">
                  Statutory Pack Profile — {statutorySummary.countryNameEn}
                  {statutorySummary.jurisdiction && <span className="ml-1 text-xs font-normal text-slate-500">({statutorySummary.jurisdiction})</span>}
                </h3>
                <button type="button" onClick={() => setStatutorySummary(null)} className="text-xs text-slate-400 hover:text-slate-600">Dismiss</button>
              </div>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <InfoRow label="Social Insurance" value={statutorySummary.socialInsuranceScheme} detail={statutorySummary.socialInsuranceDescription} />
                <InfoRow label="EOSB Formula" value={statutorySummary.eosbFormula} />
                <InfoRow label="WPS Format" value={statutorySummary.wpsFormatLabel} />
                <InfoRow label="Nationalization Scheme" value={statutorySummary.nationalizationScheme} />
                <InfoRow label="Locale / Currency" value={`${statutorySummary.localeCode} · ${statutorySummary.currencyCode} ${statutorySummary.currencySymbol}`} />
                <InfoRow label="Calendar / RTL" value={`${statutorySummary.calendarSystem} · ${statutorySummary.isRtl ? 'RTL' : 'LTR'}`} />
              </div>
              <p className="mt-3 text-xs text-amber-700 border-t border-indigo-200 pt-3">
                Sample default — configure rates for your establishment in Tenant Admin → Statutory Rules Engine.
              </p>
            </>
          )}
        </div>
      )}

      <Modal
        isOpen={modalOpen}
        title={editing ? 'Edit Company' : 'Add Company'}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label="Legal Name (EN)" required>
            <input type="text" value={form.legalNameEn} onChange={(e) => f('legalNameEn', e.target.value)} className="input w-full" placeholder="Acme Corp LLC" />
          </FormField>
          <FormField label="Legal Name (AR)">
            <input type="text" value={form.legalNameAr ?? ''} onChange={(e) => f('legalNameAr', e.target.value)} className="input w-full" dir="rtl" placeholder={translatingLegalNameAr && !form.legalNameAr ? 'Translating…' : undefined} />
          </FormField>
          <FormField label="Trade Name">
            <input type="text" value={form.tradeName ?? ''} onChange={(e) => f('tradeName', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="Country" required>
            <select
              value={form.countryCode}
              onChange={(e) => {
                const cc = e.target.value;
                f('countryCode', cc);
                const pack = availablePacks.find((p) => p.countryCode === cc);
                f('jurisdiction', pack?.jurisdictions[0]?.code ?? '');
              }}
              className="select w-full"
            >
              <option value="">Select country…</option>
              {availablePacks.map((p) => (
                <option key={p.countryCode} value={p.countryCode}>{p.nameEn}</option>
              ))}
            </select>
          </FormField>
          <FormField label="Jurisdiction">
            <select
              value={form.jurisdiction ?? ''}
              onChange={(e) => f('jurisdiction', e.target.value)}
              className="select w-full"
              disabled={!form.countryCode}
            >
              <option value="">Select jurisdiction…</option>
              {(availablePacks.find((p) => p.countryCode === form.countryCode)?.jurisdictions ?? []).map((j) => (
                <option key={j.code} value={j.code}>{j.label}</option>
              ))}
            </select>
          </FormField>
          <FormField label="Registration Number" required>
            <input type="text" value={form.registrationNumber} onChange={(e) => f('registrationNumber', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="Tax Number">
            <input type="text" value={form.taxNumber ?? ''} onChange={(e) => f('taxNumber', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="Default Currency" required>
            <select value={form.defaultCurrency || currencyCode} onChange={(e) => f('defaultCurrency', e.target.value)} className="select w-full">
              {['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR'].map((c) => <option key={c}>{c}</option>)}
            </select>
          </FormField>
          <FormField label="Status">
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </FormField>
          <FormField label="WPS Employer ID">
            <input type="text" value={form.wpsEmployerId ?? ''} onChange={(e) => f('wpsEmployerId', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="GOSI Employer ID">
            <input type="text" value={form.gosiEmployerId ?? ''} onChange={(e) => f('gosiEmployerId', e.target.value)} className="input w-full" />
          </FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Branches ────────────────────────────────────────────────────────────────

function BranchesTab({ companies }: { companies: CompanyDto[] }) {
  const [items, setItems] = useState<BranchDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterCompany, setFilterCompany] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<BranchDto | null>(null);
  const [form, setForm] = useState<BranchRequest>(emptyBranch(companies[0]?.id ?? ''));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await branchesApi.list(filterCompany || undefined); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, [filterCompany]);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyBranch(companies[0]?.id ?? '')); setError(''); setModalOpen(true); };
  const openEdit = (b: BranchDto) => {
    setEditing(b);
    setForm({ companyId: b.companyId, code: b.code, nameEn: b.nameEn, nameAr: b.nameAr, countryCode: b.countryCode, city: b.city, addressLine1: b.addressLine1, addressLine2: b.addressLine2, timeZoneId: b.timeZoneId, laborOfficeCode: b.laborOfficeCode, isHeadOffice: b.isHeadOffice, isActive: b.isActive });
    setError(''); setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.nameEn.trim() || !form.code.trim()) { setError('Name and code are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await branchesApi.update(editing.id, form);
      else await branchesApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };

  const f = (key: keyof BranchRequest, v: string | boolean) => setForm((x) => ({ ...x, [key]: v }));
  const { translation: autoBranchNameAr, isTranslating: translatingBranchNameAr } = useAutoTranslate(form.nameEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoBranchNameAr && !form.nameAr) f('nameAr', autoBranchNameAr); }, [autoBranchNameAr]);
  const deleteBranch = async (id: string) => {
    if (!confirm('Delete this branch?')) return;
    setDeleting(id);
    try { await branchesApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  const companyName = (id: string) => companies.find((c) => c.id === id)?.legalNameEn ?? id;

  return (
    <>
      <TableShell
        columns={['Code', 'Name', 'Company', 'City', 'Head Office', 'Status']}
        onAdd={openNew}
        addLabel="Add Branch"
        loading={loading}
        empty={items.length === 0}
        emptyLabel="No branches yet"
        filter={
          companies.length > 0 ? (
            <select value={filterCompany} onChange={(e) => setFilterCompany(e.target.value)} className="select" title="Filter by company">
              <option value="">All Group Companies</option>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}
            </select>
          ) : undefined
        }
        actions={
          <ImportExportToolbar
            entityName="Branches"
            onExport={async () => { const csv = await branchesApi.export(); downloadCsv(csv, 'branches.csv'); }}
            onDownloadTemplate={async () => { const csv = await branchesApi.importTemplate(); downloadCsv(csv, 'branches-template.csv'); }}
            onImport={async (csv) => { const r = await branchesApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {items.map((b) => (
          <tr key={b.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{b.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{b.nameEn}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{companyName(b.companyId)}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{b.city || '—'}</td>
            <td className="px-4 py-3">{b.isHeadOffice ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire dark:bg-sapphire/20">HQ</span> : '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={b.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(b)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteBranch(b.id)} disabled={deleting === b.id} aria-label="Delete branch" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? 'Edit Branch' : 'Add Branch'}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label="Company" required>
            <select value={form.companyId} onChange={(e) => f('companyId', e.target.value)} className="select w-full">
              <option value="">Select company</option>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}
            </select>
          </FormField>
          <FormField label="Code" required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="DXB-01" />
          </FormField>
          <FormField label="Name (EN)" required>
            <input type="text" value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" placeholder="Dubai Head Office" />
          </FormField>
          <FormField label="Name (AR)">
            <input type="text" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full" dir="rtl" placeholder={translatingBranchNameAr && !form.nameAr ? 'Translating…' : undefined} />
          </FormField>
          <FormField label="Country Code" required>
            <input type="text" value={form.countryCode} onChange={(e) => f('countryCode', e.target.value)} className="input w-full" placeholder="AE" maxLength={10} />
          </FormField>
          <FormField label="City" required>
            <input type="text" value={form.city} onChange={(e) => f('city', e.target.value)} className="input w-full" placeholder="Dubai" />
          </FormField>
          <FormField label="Time Zone" required>
            <input type="text" value={form.timeZoneId} onChange={(e) => f('timeZoneId', e.target.value)} className="input w-full" placeholder="America/New_York" />
          </FormField>
          <FormField label="Status">
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </FormField>
          <div className="col-span-2 flex items-center gap-2">
            <input type="checkbox" id="isHQ" checked={form.isHeadOffice} onChange={(e) => f('isHeadOffice', e.target.checked)} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isHQ" className="text-sm font-medium text-slate-700 dark:text-slate-300">This is the head office</label>
          </div>
        </div>
      </Modal>
    </>
  );
}

// ─── Departments ─────────────────────────────────────────────────────────────

function DepartmentsTab({ costCenters }: { costCenters: CostCenterDto[] }) {
  const [items, setItems] = useState<DepartmentDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<DepartmentDto | null>(null);
  const [form, setForm] = useState<DepartmentRequest>(emptyDept());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await departmentsApi.list(); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyDept()); setError(''); setModalOpen(true); };
  const openEdit = (d: DepartmentDto) => {
    setEditing(d);
    setForm({ branchId: d.branchId, parentDepartmentId: d.parentDepartmentId, costCenterId: d.costCenterId, code: d.code, nameEn: d.nameEn, nameAr: d.nameAr, managerEmployeeId: d.managerEmployeeId, isActive: d.isActive });
    setError(''); setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.nameEn.trim() || !form.code.trim()) { setError('Name and code are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await departmentsApi.update(editing.id, form);
      else await departmentsApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };

  const f = (key: keyof DepartmentRequest, v: string | boolean | number | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const { translation: autoDeptNameAr, isTranslating: translatingDeptNameAr } = useAutoTranslate(form.nameEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoDeptNameAr && !form.nameAr) f('nameAr', autoDeptNameAr); }, [autoDeptNameAr]);
  const deleteDept = async (id: string) => {
    if (!confirm('Delete this department?')) return;
    setDeleting(id);
    try { await departmentsApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell
        columns={['Code', 'Name', 'Status']}
        onAdd={openNew}
        addLabel="Add Department"
        loading={loading}
        empty={items.length === 0}
        emptyLabel="No departments yet"
        actions={
          <ImportExportToolbar
            entityName="Departments"
            onExport={async () => { const csv = await departmentsApi.export(); downloadCsv(csv, 'departments.csv'); }}
            onDownloadTemplate={async () => { const csv = await departmentsApi.importTemplate(); downloadCsv(csv, 'departments-template.csv'); }}
            onImport={async (csv) => { const r = await departmentsApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {items.map((d) => (
          <tr key={d.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{d.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{d.nameEn}</td>
            <td className="px-4 py-3"><ActiveBadge active={d.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(d)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteDept(d.id)} disabled={deleting === d.id} aria-label="Delete department" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? 'Edit Department' : 'Add Department'}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Code" required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="ENG" />
          </FormField>
          <FormField label="Name (EN)" required>
            <input type="text" value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" placeholder="Engineering" />
          </FormField>
          <FormField label="Name (AR)">
            <input type="text" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full" dir="rtl" placeholder={translatingDeptNameAr && !form.nameAr ? 'Translating…' : undefined} />
          </FormField>
          <FormField label="Cost Center">
            <select value={form.costCenterId ?? ''} onChange={(e) => f('costCenterId', e.target.value || undefined)} className="select w-full">
              <option value="">None</option>
              {costCenters.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </FormField>
          <FormField label="Status">
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Designations ─────────────────────────────────────────────────────────────

function DesignationsTab({ grades }: { grades: GradeDto[] }) {
  const [items, setItems] = useState<DesignationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<DesignationDto | null>(null);
  const [form, setForm] = useState<DesignationRequest>(emptyDesig());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await designationsApi.list(); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyDesig()); setError(''); setModalOpen(true); };
  const openEdit = (d: DesignationDto) => {
    setEditing(d);
    setForm({ departmentId: d.departmentId, code: d.code, titleEn: d.titleEn, titleAr: d.titleAr, jobGrade: d.jobGrade, gradeId: d.gradeId, jobLevel: d.jobLevel, jobDescription: d.jobDescription, isManagerRole: d.isManagerRole, isActive: d.isActive });
    setError(''); setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.titleEn.trim() || !form.code.trim()) { setError('Title and code are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await designationsApi.update(editing.id, form);
      else await designationsApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };

  const f = (key: keyof DesignationRequest, v: string | boolean | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const { translation: autoTitleAr, isTranslating: translatingTitleAr } = useAutoTranslate(form.titleEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoTitleAr && !form.titleAr) f('titleAr', autoTitleAr); }, [autoTitleAr]);
  const deleteDesig = async (id: string) => {
    if (!confirm('Delete this designation?')) return;
    setDeleting(id);
    try { await designationsApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell
        columns={['Code', 'Title', 'Job Grade', 'Manager Role', 'Status']}
        onAdd={openNew}
        addLabel="Add Designation"
        loading={loading}
        empty={items.length === 0}
        emptyLabel="No designations yet"
        actions={
          <ImportExportToolbar
            entityName="Designations"
            onExport={async () => { const csv = await designationsApi.export(); downloadCsv(csv, 'designations.csv'); }}
            onDownloadTemplate={async () => { const csv = await designationsApi.importTemplate(); downloadCsv(csv, 'designations-template.csv'); }}
            onImport={async (csv) => { const r = await designationsApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {items.map((d) => (
          <tr key={d.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{d.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{d.titleEn}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{d.jobGrade || '—'}</td>
            <td className="px-4 py-3">{d.isManagerRole ? <span className="rounded-full bg-violet-500/10 px-2 py-0.5 text-xs font-semibold text-violet-600 dark:text-violet-400">Manager</span> : '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={d.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(d)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteDesig(d.id)} disabled={deleting === d.id} aria-label="Delete designation" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? 'Edit Designation' : 'Add Designation'}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Code" required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="SWE-L3" />
          </FormField>
          <FormField label="Title (EN)" required>
            <input type="text" value={form.titleEn} onChange={(e) => f('titleEn', e.target.value)} className="input w-full" placeholder="Software Engineer" />
          </FormField>
          <FormField label="Title (AR)">
            <input type="text" value={form.titleAr ?? ''} onChange={(e) => f('titleAr', e.target.value)} className="input w-full" dir="rtl" placeholder={translatingTitleAr && !form.titleAr ? 'Translating…' : undefined} />
          </FormField>
          <FormField label="Job Grade">
            <input type="text" value={form.jobGrade ?? ''} onChange={(e) => f('jobGrade', e.target.value)} className="input w-full" placeholder="L3" />
          </FormField>
          <FormField label="Grade / Band">
            <select value={form.gradeId ?? ''} onChange={(e) => f('gradeId', e.target.value || undefined)} className="select w-full">
              <option value="">None</option>
              {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </FormField>
          <FormField label="Job Level">
            <input type="text" value={form.jobLevel ?? ''} onChange={(e) => f('jobLevel', e.target.value)} className="input w-full" placeholder="Officer" />
          </FormField>
          <FormField label="Job Description">
            <textarea value={form.jobDescription ?? ''} onChange={(e) => f('jobDescription', e.target.value)} className="input min-h-24 w-full" placeholder="Core responsibilities and scope" />
          </FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="isMgr" checked={form.isManagerRole} onChange={(e) => f('isManagerRole', e.target.checked)} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isMgr" className="text-sm font-medium text-slate-700 dark:text-slate-300">Manager role</label>
          </div>
          <FormField label="Status">
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Grades ─────────────────────────────────────────────────────────────────

function GradesTab() {
  const [items, setItems] = useState<GradeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GradeDto | null>(null);
  const [form, setForm] = useState<GradeRequest>(emptyGrade());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await gradesApi.list(); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyGrade()); setError(''); setModalOpen(true); };
  const openEdit = (g: GradeDto) => {
    setEditing(g);
    setForm({ code: g.code, name: g.name, band: g.band, level: g.level, isActive: g.isActive });
    setError('');
    setModalOpen(true);
  };
  const handleSave = async () => {
    if (!form.code.trim() || !form.name.trim()) { setError('Code and name are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await gradesApi.update(editing.id, form);
      else await gradesApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };
  const f = (key: keyof GradeRequest, v: string | boolean | number) => setForm((x) => ({ ...x, [key]: v }));
  const deleteGrade = async (id: string) => {
    if (!confirm('Delete this grade?')) return;
    setDeleting(id);
    try { await gradesApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell columns={['Code', 'Name', 'Band', 'Level', 'Status']} onAdd={openNew} addLabel="Add Grade" loading={loading} empty={items.length === 0} emptyLabel="No grades yet"
        actions={
          <ImportExportToolbar
            entityName="Grades"
            onExport={async () => { const csv = await gradesApi.export(); downloadCsv(csv, 'grades.csv'); }}
            onDownloadTemplate={async () => { const csv = await gradesApi.importTemplate(); downloadCsv(csv, 'grades-template.csv'); }}
            onImport={async (csv) => { const r = await gradesApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {items.map((g) => (
          <tr key={g.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{g.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{g.name}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{g.band || '—'}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{g.level}</td>
            <td className="px-4 py-3"><ActiveBadge active={g.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(g)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteGrade(g.id)} disabled={deleting === g.id} aria-label="Delete grade" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? 'Edit Grade' : 'Add Grade'} onClose={() => setModalOpen(false)} footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Code" required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="G5" /></FormField>
          <FormField label="Name" required><input value={form.name} onChange={(e) => f('name', e.target.value)} className="input w-full" placeholder="Professional Grade 5" /></FormField>
          <FormField label="Band"><input value={form.band ?? ''} onChange={(e) => f('band', e.target.value)} className="input w-full" placeholder="Professional" /></FormField>
          <FormField label="Level"><input type="number" value={form.level} onChange={(e) => f('level', Number(e.target.value))} className="input w-full" /></FormField>
          <FormField label="Status"><select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full"><option value="true">Active</option><option value="false">Inactive</option></select></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Cost Centers ───────────────────────────────────────────────────────────

function CostCentersTab({ companies }: { companies: CompanyDto[] }) {
  const [items, setItems] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CostCenterDto | null>(null);
  const [form, setForm] = useState<CostCenterRequest>(emptyCostCenter());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { const r = await costCentersApi.list(); setItems(r.items); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyCostCenter(companies[0]?.id)); setError(''); setModalOpen(true); };
  const openEdit = (c: CostCenterDto) => {
    setEditing(c);
    setForm({ companyId: c.companyId, code: c.code, name: c.name, isActive: c.isActive });
    setError('');
    setModalOpen(true);
  };
  const handleSave = async () => {
    if (!form.code.trim() || !form.name.trim()) { setError('Code and name are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await costCentersApi.update(editing.id, form);
      else await costCentersApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save. Please try again.'); }
    finally { setSaving(false); }
  };
  const f = (key: keyof CostCenterRequest, v: string | boolean | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const deleteCostCenter = async (id: string) => {
    if (!confirm('Delete this cost center?')) return;
    setDeleting(id);
    try { await costCentersApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };
  const companyName = (id?: string) => companies.find((c) => c.id === id)?.legalNameEn ?? '—';

  return (
    <>
      <TableShell columns={['Code', 'Name', 'Company', 'Status']} onAdd={openNew} addLabel="Add Cost Center" loading={loading} empty={items.length === 0} emptyLabel="No cost centers yet"
        actions={
          <ImportExportToolbar
            entityName="Cost Centers"
            onExport={async () => { const csv = await costCentersApi.export(); downloadCsv(csv, 'cost-centers.csv'); }}
            onDownloadTemplate={async () => { const csv = await costCentersApi.importTemplate(); downloadCsv(csv, 'cost-centers-template.csv'); }}
            onImport={async (csv) => { const r = await costCentersApi.import(csv); load(); return r; }}
          />
        }
      >
        {items.map((c) => (
          <tr key={c.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{c.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{c.name}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{companyName(c.companyId)}</td>
            <td className="px-4 py-3"><ActiveBadge active={c.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(c)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteCostCenter(c.id)} disabled={deleting === c.id} aria-label="Delete cost center" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? 'Edit Cost Center' : 'Add Cost Center'} onClose={() => setModalOpen(false)} footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Company"><select value={form.companyId ?? ''} onChange={(e) => f('companyId', e.target.value || undefined)} className="select w-full"><option value="">None</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}</select></FormField>
          <FormField label="Code" required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="HR-OPS" /></FormField>
          <FormField label="Name" required><input value={form.name} onChange={(e) => f('name', e.target.value)} className="input w-full" placeholder="HR Operations" /></FormField>
          <FormField label="Status"><select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full"><option value="true">Active</option><option value="false">Inactive</option></select></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Master Data ─────────────────────────────────────────────────────────────

function MasterDataTab() {
  const [types, setTypes] = useState<MasterDataType[]>([]);
  const [values, setValues] = useState<MasterDataValue[]>([]);
  const [selectedType, setSelectedType] = useState<MasterDataType | null>(null);
  const [loading, setLoading] = useState(true);
  const [valuesLoading, setValuesLoading] = useState(false);
  const [typeModal, setTypeModal] = useState(false);
  const [valueModal, setValueModal] = useState(false);
  const [editingType, setEditingType] = useState<MasterDataType | null>(null);
  const [editingValue, setEditingValue] = useState<MasterDataValue | null>(null);
  const [typeForm, setTypeForm] = useState({ code: '', nameEn: '', nameAr: '', description: '', allowCustomValues: true });
  const [valueForm, setValueForm] = useState({ code: '', valueEn: '', valueAr: '', sortOrder: 0, isDefault: false });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const loadTypes = useCallback(async () => {
    setLoading(true);
    try { setTypes(await masterDataApi.listTypes(false)); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  const loadValues = useCallback(async (typeId: string) => {
    setValuesLoading(true);
    try { setValues(await masterDataApi.listValues(typeId, false)); } catch { /**/ }
    finally { setValuesLoading(false); }
  }, []);

  useEffect(() => { loadTypes(); }, [loadTypes]);

  const selectType = (t: MasterDataType) => {
    setSelectedType(t);
    loadValues(t.id);
  };

  const openNewType = () => {
    setEditingType(null);
    setTypeForm({ code: '', nameEn: '', nameAr: '', description: '', allowCustomValues: true });
    setError(''); setTypeModal(true);
  };
  const openEditType = (t: MasterDataType) => {
    setEditingType(t);
    setTypeForm({ code: t.code, nameEn: t.nameEn, nameAr: t.nameAr, description: t.description, allowCustomValues: t.allowCustomValues });
    setError(''); setTypeModal(true);
  };
  const saveType = async () => {
    if (!typeForm.nameEn.trim() || !typeForm.code.trim()) { setError('Code and name are required'); return; }
    setSaving(true); setError('');
    try {
      if (editingType) await masterDataApi.updateType(editingType.id, typeForm);
      else await masterDataApi.createType(typeForm);
      setTypeModal(false); loadTypes();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };

  const openNewValue = () => {
    setEditingValue(null);
    setValueForm({ code: '', valueEn: '', valueAr: '', sortOrder: 0, isDefault: false });
    setError(''); setValueModal(true);
  };
  const openEditValue = (v: MasterDataValue) => {
    setEditingValue(v);
    setValueForm({ code: v.code, valueEn: v.valueEn, valueAr: v.valueAr, sortOrder: v.sortOrder, isDefault: v.isDefault });
    setError(''); setValueModal(true);
  };
  const saveValue = async () => {
    if (!selectedType || !valueForm.valueEn.trim() || !valueForm.code.trim()) { setError('Code and value are required'); return; }
    setSaving(true); setError('');
    try {
      if (editingValue) await masterDataApi.updateValue(editingValue.id, { ...valueForm });
      else await masterDataApi.createValue(selectedType.id, { ...valueForm, isDefault: valueForm.isDefault });
      setValueModal(false); loadValues(selectedType.id);
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };
  const deleteValue = async (v: MasterDataValue) => {
    if (v.isSystemDefined) return;
    if (!confirm(`Delete "${v.valueEn}"?`)) return;
    try { await masterDataApi.deleteValue(v.id); if (selectedType) loadValues(selectedType.id); } catch (e) { notifyApiError(e); }
  };

  return (
    <div className="flex gap-4 min-h-[400px]">
      {/* Types list */}
      <div className="w-72 shrink-0">
        <div className="surface p-3">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-xs font-bold uppercase tracking-wide text-slate-400">Types</span>
            <button type="button" onClick={openNewType} className="btn-primary h-7 px-2 text-xs"><Plus className="h-3 w-3" /> New</button>
          </div>
          {loading ? (
            <div className="flex justify-center py-8"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
          ) : types.length === 0 ? (
            <p className="py-6 text-center text-xs text-slate-400">No types yet</p>
          ) : (
            <ul className="space-y-0.5">
              {types.map((t) => (
                <li key={t.id}>
                  <div
                    role="button"
                    tabIndex={0}
                    onClick={() => selectType(t)}
                    onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectType(t); } }}
                    className={`group flex w-full cursor-pointer items-center justify-between rounded-lg px-3 py-2 text-sm ${selectedType?.id === t.id ? 'bg-sapphire/10 text-sapphire font-semibold' : 'hover:bg-slate-50 dark:hover:bg-white/[0.03] text-slate-700 dark:text-slate-300'}`}
                  >
                    <span className="truncate">{t.nameEn}</span>
                    <div className="flex items-center gap-1">
                      {!t.isSystemDefined && (
                        <button type="button" aria-label="Edit type" onClick={(e) => { e.stopPropagation(); openEditType(t); }} className="opacity-0 group-hover:opacity-100 rounded p-0.5 hover:bg-slate-200 dark:hover:bg-white/10">
                          <Pencil className="h-3 w-3" />
                        </button>
                      )}
                      <ChevronRight className="h-3 w-3 opacity-40" />
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      {/* Values */}
      <div className="flex-1">
        {!selectedType ? (
          <div className="surface flex h-full items-center justify-center">
            <p className="text-sm text-slate-400">Select a type to manage values</p>
          </div>
        ) : (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <div>
                <h3 className="font-semibold text-slate-900 dark:text-white">{selectedType.nameEn}</h3>
                <p className="text-xs text-slate-400">{selectedType.description || selectedType.code}</p>
              </div>
              <button type="button" onClick={openNewValue} className="btn-primary h-8 px-3 text-sm"><Plus className="h-3.5 w-3.5" /> Add Value</button>
            </div>
            <div className="surface overflow-hidden">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {['Code', 'Value (EN)', 'Value (AR)', 'Sort', 'Default', 'Status', ''].map((h) => (
                      <th key={h} className="px-4 py-2.5 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {valuesLoading ? (
                    <tr><td colSpan={7} className="py-8 text-center"><div className="mx-auto h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
                  ) : values.length === 0 ? (
                    <tr><td colSpan={7} className="py-8 text-center text-slate-400 text-sm">No values yet</td></tr>
                  ) : values.map((v) => (
                    <tr key={v.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-4 py-2.5 font-mono text-xs text-slate-500">{v.code}</td>
                      <td className="px-4 py-2.5 font-medium text-slate-900 dark:text-white">{v.valueEn}</td>
                      <td className="px-4 py-2.5 text-slate-500 dark:text-slate-400" dir="rtl">{v.valueAr}</td>
                      <td className="px-4 py-2.5 text-slate-500">{v.sortOrder}</td>
                      <td className="px-4 py-2.5">{v.isDefault ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">Default</span> : '—'}</td>
                      <td className="px-4 py-2.5"><ActiveBadge active={v.isActive} /></td>
                      <td className="px-4 py-2.5">
                        <div className="flex gap-1 opacity-0 group-hover:opacity-100">
                          <button type="button" onClick={() => openEditValue(v)} className="btn-secondary h-6 px-2 text-xs"><Pencil className="h-3 w-3" /></button>
                          {!v.isSystemDefined && <button type="button" onClick={() => deleteValue(v)} className="h-6 px-2 text-xs rounded-lg border border-red-200 text-red-500 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20">✕</button>}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>

      {/* Type Modal */}
      <Modal isOpen={typeModal} title={editingType ? 'Edit Type' : 'New Type'} onClose={() => setTypeModal(false)}
        footer={<><button type="button" onClick={() => setTypeModal(false)} className="btn-secondary">Cancel</button><button type="button" onClick={saveType} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Code" required><input value={typeForm.code} onChange={(e) => setTypeForm(x => ({ ...x, code: e.target.value }))} className="input w-full" placeholder="NATIONALITY" /></FormField>
          <FormField label="Name (EN)" required><input value={typeForm.nameEn} onChange={(e) => setTypeForm(x => ({ ...x, nameEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label="Name (AR)"><input value={typeForm.nameAr} onChange={(e) => setTypeForm(x => ({ ...x, nameAr: e.target.value }))} className="input w-full" dir="rtl" /></FormField>
          <FormField label="Description"><input value={typeForm.description} onChange={(e) => setTypeForm(x => ({ ...x, description: e.target.value }))} className="input w-full" /></FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="allowCustom" checked={typeForm.allowCustomValues} onChange={(e) => setTypeForm(x => ({ ...x, allowCustomValues: e.target.checked }))} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="allowCustom" className="text-sm text-slate-700 dark:text-slate-300">Allow custom values</label>
          </div>
        </div>
      </Modal>

      {/* Value Modal */}
      <Modal isOpen={valueModal} title={editingValue ? 'Edit Value' : 'New Value'} onClose={() => setValueModal(false)}
        footer={<><button type="button" onClick={() => setValueModal(false)} className="btn-secondary">Cancel</button><button type="button" onClick={saveValue} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Code" required><input value={valueForm.code} onChange={(e) => setValueForm(x => ({ ...x, code: e.target.value }))} className="input w-full" placeholder="UAE" /></FormField>
          <FormField label="Value (EN)" required><input value={valueForm.valueEn} onChange={(e) => setValueForm(x => ({ ...x, valueEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label="Value (AR)"><input value={valueForm.valueAr} onChange={(e) => setValueForm(x => ({ ...x, valueAr: e.target.value }))} className="input w-full" dir="rtl" /></FormField>
          <FormField label="Sort Order"><input type="number" value={valueForm.sortOrder} onChange={(e) => setValueForm(x => ({ ...x, sortOrder: Number(e.target.value) }))} className="input w-full" /></FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="isDefault" checked={valueForm.isDefault} onChange={(e) => setValueForm(x => ({ ...x, isDefault: e.target.checked }))} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isDefault" className="text-sm text-slate-700 dark:text-slate-300">Default value</label>
          </div>
        </div>
      </Modal>
    </div>
  );
}

// ─── Numbering Rules ──────────────────────────────────────────────────────────

function NumberingRulesTab() {
  const [items, setItems] = useState<NumberingRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<NumberingRule | null>(null);
  const [form, setForm] = useState({ entityType: '', prefix: '', suffix: '', paddingLength: 5, separator: '-', includeYear: true, includeMonth: false, resetYearly: true });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await numberingRulesApi.list()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const openEdit = (r: NumberingRule) => {
    setEditing(r);
    setForm({ entityType: r.entityType, prefix: r.prefix, suffix: r.suffix, paddingLength: r.paddingLength, separator: r.separator, includeYear: r.includeYear, includeMonth: r.includeMonth, resetYearly: r.resetYearly });
    setError(''); setModalOpen(true);
  };
  const openNew = () => {
    setEditing(null);
    setForm({ entityType: '', prefix: '', suffix: '', paddingLength: 5, separator: '-', includeYear: true, includeMonth: false, resetYearly: true });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    if (!form.entityType.trim() || !form.prefix.trim()) { setError('Entity type and prefix are required'); return; }
    setSaving(true); setError('');
    try { await numberingRulesApi.upsert(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string | boolean | number) => setForm(x => ({ ...x, [key]: v }));
  const deleteRule = async (id: string) => {
    if (!confirm('Delete this numbering rule?')) return;
    setDeleting(id);
    try { await numberingRulesApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  const preview = (r: NumberingRule) => {
    const parts: string[] = [r.prefix];
    if (r.includeYear) parts.push('2025');
    if (r.includeMonth) parts.push('06');
    parts.push('0'.repeat(r.paddingLength - 1) + '1');
    if (r.suffix) parts.push(r.suffix);
    return parts.join(r.separator);
  };

  return (
    <>
      <TableShell columns={['Entity Type', 'Prefix', 'Preview', 'Padding', 'Reset Yearly', '']}
        onAdd={openNew} addLabel="New Rule" loading={loading} empty={items.length === 0} emptyLabel="No rules defined">
        {items.map((r) => (
          <tr key={r.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{r.entityType}</td>
            <td className="px-4 py-3 font-mono text-xs text-slate-500">{r.prefix}</td>
            <td className="px-4 py-3 font-mono text-xs text-sapphire">{preview(r)}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.paddingLength}</td>
            <td className="px-4 py-3">{r.resetYearly ? <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-xs font-semibold text-emerald-600">Yes</span> : '—'}</td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(r)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteRule(r.id)} disabled={deleting === r.id} aria-label="Delete rule" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? `Edit Rule — ${editing.entityType}` : 'New Numbering Rule'} onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label="Entity Type" required>
            <input value={form.entityType} onChange={(e) => f('entityType', e.target.value)} className="input w-full" placeholder="Employee" disabled={!!editing} />
          </FormField>
          <FormField label="Prefix" required>
            <input value={form.prefix} onChange={(e) => f('prefix', e.target.value)} className="input w-full" placeholder="EMP" />
          </FormField>
          <FormField label="Suffix">
            <input value={form.suffix} onChange={(e) => f('suffix', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="Separator">
            <input value={form.separator} onChange={(e) => f('separator', e.target.value)} className="input w-full" placeholder="-" maxLength={3} />
          </FormField>
          <FormField label="Padding Length">
            <input type="number" value={form.paddingLength} onChange={(e) => f('paddingLength', Number(e.target.value))} className="input w-full" min={1} max={10} />
          </FormField>
          <div className="col-span-2 flex gap-6">
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.includeYear} onChange={(e) => f('includeYear', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Include Year</label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.includeMonth} onChange={(e) => f('includeMonth', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Include Month</label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.resetYearly} onChange={(e) => f('resetYearly', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Reset Yearly</label>
          </div>
        </div>
      </Modal>
    </>
  );
}

// ─── System Settings ──────────────────────────────────────────────────────────

function SystemSettingsTab() {
  const [items, setItems] = useState<SystemSetting[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterCat, setFilterCat] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<SystemSetting | null>(null);
  const [form, setForm] = useState({ category: '', settingKey: '', settingValue: '', dataType: 'string', description: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await systemSettingsApi.list(filterCat || undefined)); } catch { /**/ }
    finally { setLoading(false); }
  }, [filterCat]);
  useEffect(() => { load(); }, [load]);

  const categories = [...new Set(items.map((i) => i.category))].sort();

  const openNew = () => {
    setEditing(null);
    setForm({ category: '', settingKey: '', settingValue: '', dataType: 'string', description: '' });
    setError(''); setModalOpen(true);
  };
  const openEdit = (s: SystemSetting) => {
    setEditing(s);
    setForm({ category: s.category, settingKey: s.settingKey, settingValue: s.settingValue, dataType: s.dataType, description: s.description });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    if (!form.category.trim() || !form.settingKey.trim()) { setError('Category and key are required'); return; }
    setSaving(true); setError('');
    try { await systemSettingsApi.upsert(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };

  return (
    <>
      <TableShell columns={['Category', 'Key', 'Value', 'Type', '']} onAdd={openNew} addLabel="New Setting"
        loading={loading} empty={items.length === 0} emptyLabel="No settings"
        filter={
          <select value={filterCat} onChange={(e) => setFilterCat(e.target.value)} className="select">
            <option value="">All Categories</option>
            {categories.map((c) => <option key={c}>{c}</option>)}
          </select>
        }>
        {items.map((s) => (
          <tr key={s.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{s.category}</td>
            <td className="px-4 py-3 font-mono text-xs text-slate-700 dark:text-slate-300">{s.settingKey}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300 max-w-xs truncate">{s.settingValue || '—'}</td>
            <td className="px-4 py-3 text-slate-400 text-xs">{s.dataType}</td>
            <td className="px-4 py-3">
              {!s.isReadOnly && <button type="button" onClick={() => openEdit(s)} className="btn-secondary h-7 px-2 text-xs opacity-0 group-hover:opacity-100"><Pencil className="h-3 w-3" /> Edit</button>}
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? 'Edit Setting' : 'New Setting'} onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Category" required><input value={form.category} onChange={(e) => setForm(x => ({ ...x, category: e.target.value }))} className="input w-full" placeholder="HR" /></FormField>
          <FormField label="Key" required><input value={form.settingKey} onChange={(e) => setForm(x => ({ ...x, settingKey: e.target.value }))} className="input w-full" placeholder="PROBATION_DAYS" /></FormField>
          <FormField label="Value"><input value={form.settingValue} onChange={(e) => setForm(x => ({ ...x, settingValue: e.target.value }))} className="input w-full" /></FormField>
          <FormField label="Data Type">
            <select value={form.dataType} onChange={(e) => setForm(x => ({ ...x, dataType: e.target.value }))} className="select w-full">
              {['string', 'number', 'boolean', 'json'].map((t) => <option key={t}>{t}</option>)}
            </select>
          </FormField>
          <FormField label="Description"><input value={form.description} onChange={(e) => setForm(x => ({ ...x, description: e.target.value }))} className="input w-full" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── GCC Settings ────────────────────────────────────────────────────────────

function GCCSettingsTab() {
  const [items, setItems] = useState<GCCComplianceSetting[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GCCComplianceSetting | null>(null);
  const [form, setForm] = useState<Partial<GCCComplianceSetting> & { countryCode: string }>({
    countryCode: 'US', wpsEnabled: false, wpsAgentId: '', wpsMolCode: '', sifEnabled: false,
    eosbEnabled: true, eosbYears1To5Rate: 0.5, eosbYearsAbove5Rate: 1.0, eosbMinYears: 1,
    workWeek: 'Mon-Fri', weekendDays: 'Sat,Sun', visaTrackingEnabled: true, visaAlertDays: 30,
    iqamaRequired: false, iqamaAlertDays: 30, emiratesIdRequired: false,
    ramadanHoursEnabled: false, ramadanReducedHoursPerDay: 2,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await gccSettingsApi.list()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const openNew = () => {
    setEditing(null);
    setForm({ countryCode: 'US', wpsEnabled: false, wpsAgentId: '', wpsMolCode: '', sifEnabled: false, eosbEnabled: true, eosbYears1To5Rate: 0.5, eosbYearsAbove5Rate: 1.0, eosbMinYears: 1, workWeek: 'Mon-Fri', weekendDays: 'Sat,Sun', visaTrackingEnabled: true, visaAlertDays: 30, iqamaRequired: false, iqamaAlertDays: 30, emiratesIdRequired: false, ramadanHoursEnabled: false, ramadanReducedHoursPerDay: 2 });
    setError(''); setModalOpen(true);
  };
  const openEdit = (s: GCCComplianceSetting) => {
    setEditing(s);
    setForm({ ...s });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    setSaving(true); setError('');
    try { await gccSettingsApi.upsert(form as GCCComplianceSetting & { countryCode: string }); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string | boolean | number) => setForm(x => ({ ...x, [key]: v }));

  return (
    <>
      <div className="space-y-4">
        <div className="flex justify-end">
          <button type="button" onClick={openNew} className="btn-primary"><Plus className="h-4 w-4" /> Add Country Config</button>
        </div>
        {loading ? <div className="flex justify-center py-12"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
          : items.length === 0 ? <div className="surface py-12 text-center text-sm text-slate-400">No GCC settings configured</div>
          : (
            <div className="grid gap-4 sm:grid-cols-2">
              {items.map((s) => (
                <div key={s.id} className="surface p-4 space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-bold text-slate-900 dark:text-white text-lg">{s.countryCode}</span>
                    <button type="button" onClick={() => openEdit(s)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                  </div>
                  <div className="grid grid-cols-2 gap-1 text-xs">
                    <span className="text-slate-400">EOSB</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.eosbEnabled ? `${s.eosbYears1To5Rate}× / ${s.eosbYearsAbove5Rate}×` : 'Disabled'}</span>
                    <span className="text-slate-400">WPS</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.wpsEnabled ? s.wpsAgentId || 'Enabled' : 'Disabled'}</span>
                    <span className="text-slate-400">Work Week</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.workWeek}</span>
                    <span className="text-slate-400">Weekend</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.weekendDays}</span>
                    <span className="text-slate-400">Visa Alert</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.visaTrackingEnabled ? `${s.visaAlertDays} days` : 'Off'}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
      </div>

      <Modal isOpen={modalOpen} title={editing ? `GCC Settings — ${editing.countryCode}` : 'New Country Config'} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3 text-sm">
          <FormField label="Country Code" required><input value={form.countryCode} onChange={(e) => f('countryCode', e.target.value)} className="input w-full" placeholder="AE" maxLength={5} disabled={!!editing} /></FormField>
          <FormField label="Work Week"><input value={form.workWeek ?? ''} onChange={(e) => f('workWeek', e.target.value)} className="input w-full" placeholder="Mon-Fri" /></FormField>
          <FormField label="Weekend Days"><input value={form.weekendDays ?? ''} onChange={(e) => f('weekendDays', e.target.value)} className="input w-full" placeholder="Fri,Sat" /></FormField>
          <div />

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">EOSB</div>
          <label className="col-span-2 flex items-center gap-2"><input type="checkbox" checked={form.eosbEnabled ?? false} onChange={(e) => f('eosbEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Enable EOSB</label>
          <FormField label="Years 1-5 Rate"><input type="number" step="0.1" value={form.eosbYears1To5Rate ?? 0.5} onChange={(e) => f('eosbYears1To5Rate', Number(e.target.value))} className="input w-full" /></FormField>
          <FormField label="Years 5+ Rate"><input type="number" step="0.1" value={form.eosbYearsAbove5Rate ?? 1} onChange={(e) => f('eosbYearsAbove5Rate', Number(e.target.value))} className="input w-full" /></FormField>
          <FormField label="Min Years"><input type="number" value={form.eosbMinYears ?? 1} onChange={(e) => f('eosbMinYears', Number(e.target.value))} className="input w-full" /></FormField>

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">WPS / SIF</div>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.wpsEnabled ?? false} onChange={(e) => f('wpsEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Enable WPS</label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.sifEnabled ?? false} onChange={(e) => f('sifEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Enable SIF</label>
          <FormField label="WPS Agent ID"><input value={form.wpsAgentId ?? ''} onChange={(e) => f('wpsAgentId', e.target.value)} className="input w-full" /></FormField>
          <FormField label="MOL Code"><input value={form.wpsMolCode ?? ''} onChange={(e) => f('wpsMolCode', e.target.value)} className="input w-full" /></FormField>

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">Documents & Compliance</div>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.visaTrackingEnabled ?? false} onChange={(e) => f('visaTrackingEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Visa Tracking</label>
          <FormField label="Visa Alert Days"><input type="number" value={form.visaAlertDays ?? 30} onChange={(e) => f('visaAlertDays', Number(e.target.value))} className="input w-full" /></FormField>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.iqamaRequired ?? false} onChange={(e) => f('iqamaRequired', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Iqama Required</label>
          <FormField label="Iqama Alert Days"><input type="number" value={form.iqamaAlertDays ?? 30} onChange={(e) => f('iqamaAlertDays', Number(e.target.value))} className="input w-full" /></FormField>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.emiratesIdRequired ?? false} onChange={(e) => f('emiratesIdRequired', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Emirates ID Required</label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.ramadanHoursEnabled ?? false} onChange={(e) => f('ramadanHoursEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> Ramadan Hours</label>
          <FormField label="Ramadan Reduced Hrs/Day"><input type="number" value={form.ramadanReducedHoursPerDay ?? 2} onChange={(e) => f('ramadanReducedHoursPerDay', Number(e.target.value))} className="input w-full" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Fiscal Years ─────────────────────────────────────────────────────────────

function FiscalYearsTab() {
  const [items, setItems] = useState<FiscalYear[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [form, setForm] = useState({ year: new Date().getFullYear(), startDate: '', endDate: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [closing, setClosing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await fiscalYearsApi.list()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const save = async () => {
    if (!form.startDate || !form.endDate) { setError('Start and end dates are required'); return; }
    setSaving(true); setError('');
    try { await fiscalYearsApi.create(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to create fiscal year.'); }
    finally { setSaving(false); }
  };
  const closeFY = async (id: string) => {
    if (!confirm('Close this fiscal year? This cannot be undone.')) return;
    setClosing(id);
    try { await fiscalYearsApi.close(id); load(); } catch { /**/ }
    finally { setClosing(null); }
  };
  const deleteFY = async (id: string) => {
    if (!confirm('Delete this fiscal year?')) return;
    setDeleting(id);
    try { await fiscalYearsApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  const statusColor = (s: string) => s === 'Open' ? 'bg-emerald-500/10 text-emerald-600' : s === 'Closed' ? 'bg-slate-100 text-slate-400 dark:bg-white/10 dark:text-slate-500' : 'bg-amber-500/10 text-amber-600';

  return (
    <>
      <TableShell columns={['Year', 'Start', 'End', 'Status', 'Current', '']} onAdd={() => { setForm({ year: new Date().getFullYear(), startDate: '', endDate: '' }); setError(''); setModalOpen(true); }}
        addLabel="New Fiscal Year" loading={loading} empty={items.length === 0} emptyLabel="No fiscal years">
        {items.map((fy) => (
          <tr key={fy.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-semibold text-slate-900 dark:text-white">{fy.year}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fy.startDate}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fy.endDate}</td>
            <td className="px-4 py-3"><span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusColor(fy.status)}`}>{fy.status}</span></td>
            <td className="px-4 py-3">{fy.isCurrent ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">Current</span> : '—'}</td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                {fy.status === 'Open' && (
                  <button type="button" onClick={() => closeFY(fy.id)} disabled={closing === fy.id} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                    {closing === fy.id ? '…' : 'Close'}
                  </button>
                )}
                {!fy.isCurrent && (
                  <button type="button" onClick={() => deleteFY(fy.id)} disabled={deleting === fy.id} aria-label="Delete fiscal year" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                )}
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title="New Fiscal Year" onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Create'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label="Year" required><input type="number" value={form.year} onChange={(e) => setForm(x => ({ ...x, year: Number(e.target.value) }))} className="input w-full" /></FormField>
          <FormField label="Start Date" required><input type="date" value={form.startDate} onChange={(e) => setForm(x => ({ ...x, startDate: e.target.value }))} className="input w-full" /></FormField>
          <FormField label="End Date" required><input type="date" value={form.endDate} onChange={(e) => setForm(x => ({ ...x, endDate: e.target.value }))} className="input w-full" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Locations ────────────────────────────────────────────────────────────────

function LocationsTab() {
  const [items, setItems] = useState<AdminLocation[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<AdminLocation | null>(null);
  const [form, setForm] = useState({ code: '', nameEn: '', nameAr: '', addressLine1: '', city: '', countryCode: 'US', postalCode: '', latitude: '', longitude: '', geofenceRadiusMeters: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await locationsApi.list()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const emptyForm = () => ({ code: '', nameEn: '', nameAr: '', addressLine1: '', city: '', countryCode: 'US', postalCode: '', latitude: '', longitude: '', geofenceRadiusMeters: '' });
  const openNew = () => { setEditing(null); setForm(emptyForm()); setError(''); setModalOpen(true); };
  const openEdit = (l: AdminLocation) => {
    setEditing(l);
    setForm({ code: l.code, nameEn: l.nameEn, nameAr: l.nameAr, addressLine1: l.addressLine1, city: l.city, countryCode: l.countryCode, postalCode: l.postalCode, latitude: l.latitude?.toString() ?? '', longitude: l.longitude?.toString() ?? '', geofenceRadiusMeters: l.geofenceRadiusMeters?.toString() ?? '' });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    if (!form.code.trim() || !form.nameEn.trim()) { setError('Code and name are required'); return; }
    setSaving(true); setError('');
    const body = { ...form, latitude: form.latitude ? Number(form.latitude) : undefined, longitude: form.longitude ? Number(form.longitude) : undefined, geofenceRadiusMeters: form.geofenceRadiusMeters ? Number(form.geofenceRadiusMeters) : undefined };
    try {
      if (editing) await locationsApi.update(editing.id, body);
      else await locationsApi.create(body);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string) => setForm(x => ({ ...x, [key]: v }));
  const deleteLocation = async (id: string) => {
    if (!confirm('Delete this location?')) return;
    setDeleting(id);
    try { await locationsApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell columns={['Code', 'Name', 'City', 'Country', 'Geofence', 'Status', '']} onAdd={openNew} addLabel="Add Location"
        loading={loading} empty={items.length === 0} emptyLabel="No locations"
        actions={
          <ImportExportToolbar
            entityName="Locations"
            onExport={async () => { const csv = await locationsApi.export(); downloadCsv(csv, 'locations.csv'); }}
            onDownloadTemplate={async () => { const csv = await locationsApi.importTemplate(); downloadCsv(csv, 'locations-template.csv'); }}
            onImport={async (csv) => { const r = await locationsApi.import(csv); load(); return r; }}
          />
        }
      >
        {items.map((l) => (
          <tr key={l.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500">{l.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{l.nameEn}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{l.city}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{l.countryCode}</td>
            <td className="px-4 py-3 text-slate-500 text-xs">{l.geofenceRadiusMeters ? `${l.geofenceRadiusMeters}m` : '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={l.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(l)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteLocation(l.id)} disabled={deleting === l.id} aria-label="Delete location" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? 'Edit Location' : 'Add Location'} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label="Code" required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="DXB-HQ" /></FormField>
          <FormField label="Name (EN)" required><input value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" /></FormField>
          <FormField label="Name (AR)"><input value={form.nameAr} onChange={(e) => f('nameAr', e.target.value)} className="input w-full" dir="rtl" /></FormField>
          <FormField label="Country Code" required><input value={form.countryCode} onChange={(e) => f('countryCode', e.target.value)} className="input w-full" placeholder="AE" maxLength={10} /></FormField>
          <FormField label="City"><input value={form.city} onChange={(e) => f('city', e.target.value)} className="input w-full" /></FormField>
          <FormField label="Postal Code"><input value={form.postalCode} onChange={(e) => f('postalCode', e.target.value)} className="input w-full" /></FormField>
          <FormField label="Address"><input value={form.addressLine1} onChange={(e) => f('addressLine1', e.target.value)} className="input w-full" /></FormField>
          <div />
          <FormField label="Latitude"><input type="number" step="any" value={form.latitude} onChange={(e) => f('latitude', e.target.value)} className="input w-full" placeholder="25.2048" /></FormField>
          <FormField label="Longitude"><input type="number" step="any" value={form.longitude} onChange={(e) => f('longitude', e.target.value)} className="input w-full" placeholder="55.2708" /></FormField>
          <FormField label="Geofence Radius (m)"><input type="number" value={form.geofenceRadiusMeters} onChange={(e) => f('geofenceRadiusMeters', e.target.value)} className="input w-full" placeholder="100" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Notification Templates ───────────────────────────────────────────────────

function NotificationTemplatesTab() {
  const [items, setItems] = useState<NotificationTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterChannel, setFilterChannel] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<NotificationTemplate | null>(null);
  const [form, setForm] = useState({ code: '', eventType: '', channel: 'Email', subjectEn: '', subjectAr: '', bodyEn: '', bodyAr: '', variables: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await notificationTemplatesApi.list(filterChannel || undefined)); } catch { /**/ }
    finally { setLoading(false); }
  }, [filterChannel]);
  useEffect(() => { load(); }, [load]);

  const openNew = () => {
    setEditing(null);
    setForm({ code: '', eventType: '', channel: 'Email', subjectEn: '', subjectAr: '', bodyEn: '', bodyAr: '', variables: '' });
    setError(''); setModalOpen(true);
  };
  const openEdit = (t: NotificationTemplate) => {
    setEditing(t);
    setForm({ code: t.code, eventType: t.eventType, channel: t.channel, subjectEn: t.subjectEn, subjectAr: t.subjectAr, bodyEn: t.bodyEn, bodyAr: t.bodyAr, variables: t.variables });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    if (!form.code.trim() || !form.eventType.trim() || !form.bodyEn.trim()) { setError('Code, event type and body (EN) are required'); return; }
    setSaving(true); setError('');
    try {
      if (editing) await notificationTemplatesApi.update(editing.id, form);
      else await notificationTemplatesApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to save.'); }
    finally { setSaving(false); }
  };
  const deleteTemplate = async (id: string) => {
    if (!confirm('Delete this notification template?')) return;
    setDeleting(id);
    try { await notificationTemplatesApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell columns={['Code', 'Event Type', 'Channel', 'Subject (EN)', 'Status', '']} onAdd={openNew} addLabel="New Template"
        loading={loading} empty={items.length === 0} emptyLabel="No templates"
        filter={
          <select value={filterChannel} onChange={(e) => setFilterChannel(e.target.value)} className="select">
            <option value="">All Channels</option>
            {['Email', 'SMS', 'InApp', 'WhatsApp'].map((c) => <option key={c}>{c}</option>)}
          </select>
        }>
        {items.map((t) => (
          <tr key={t.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500">{t.code}</td>
            <td className="px-4 py-3 text-slate-700 dark:text-slate-300">{t.eventType}</td>
            <td className="px-4 py-3"><span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">{t.channel}</span></td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300 max-w-xs truncate">{t.subjectEn || '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={t.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100">
                <button type="button" onClick={() => openEdit(t)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" /> Edit</button>
                <button type="button" onClick={() => deleteTemplate(t.id)} disabled={deleting === t.id} aria-label="Delete template" className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? 'Edit Template' : 'New Template'} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">Cancel</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label="Code" required><input value={form.code} onChange={(e) => setForm(x => ({ ...x, code: e.target.value }))} className="input w-full" placeholder="LEAVE_APPROVED" /></FormField>
          <FormField label="Event Type" required><input value={form.eventType} onChange={(e) => setForm(x => ({ ...x, eventType: e.target.value }))} className="input w-full" placeholder="LeaveApproved" /></FormField>
          <FormField label="Channel" required>
            <select value={form.channel} onChange={(e) => setForm(x => ({ ...x, channel: e.target.value }))} className="select w-full">
              {['Email', 'SMS', 'InApp', 'WhatsApp'].map((c) => <option key={c}>{c}</option>)}
            </select>
          </FormField>
          <FormField label="Variables"><input value={form.variables} onChange={(e) => setForm(x => ({ ...x, variables: e.target.value }))} className="input w-full" placeholder="{{employeeName}},{{leaveType}}" /></FormField>
          <FormField label="Subject (EN)"><input value={form.subjectEn} onChange={(e) => setForm(x => ({ ...x, subjectEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label="Subject (AR)"><input value={form.subjectAr} onChange={(e) => setForm(x => ({ ...x, subjectAr: e.target.value }))} className="input w-full" dir="rtl" /></FormField>
          <FormField label="Body (EN)" required><textarea value={form.bodyEn} onChange={(e) => setForm(x => ({ ...x, bodyEn: e.target.value }))} className="input w-full min-h-28" /></FormField>
          <FormField label="Body (AR)"><textarea value={form.bodyAr} onChange={(e) => setForm(x => ({ ...x, bodyAr: e.target.value }))} className="input w-full min-h-28" dir="rtl" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Admin Audit Logs ─────────────────────────────────────────────────────────

function AdminAuditLogsTab() {
  const [items, setItems] = useState<AdminAuditLog[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [filterEntity, setFilterEntity] = useState('');
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const r = await adminAuditApi.list({ entityType: filterEntity || undefined, from: filterFrom || undefined, to: filterTo || undefined, page, pageSize });
      setItems(r.items); setTotal(r.total);
    } catch { /**/ }
    finally { setLoading(false); }
  }, [filterEntity, filterFrom, filterTo, page]);
  useEffect(() => { load(); }, [load]);

  const actionColor = (a: string) => a === 'Create' ? 'bg-emerald-500/10 text-emerald-600' : a === 'Delete' ? 'bg-red-500/10 text-red-500' : 'bg-amber-500/10 text-amber-600';

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-2">
        <div>
          <label className="mb-1 block text-xs font-medium text-slate-500">Entity Type</label>
          <input value={filterEntity} onChange={(e) => { setFilterEntity(e.target.value); setPage(1); }} className="input h-8 w-44 text-sm" placeholder="e.g. MasterDataType" />
        </div>
        <div>
          <label className="mb-1 block text-xs font-medium text-slate-500">From</label>
          <input type="date" value={filterFrom} onChange={(e) => { setFilterFrom(e.target.value); setPage(1); }} className="input h-8 text-sm" />
        </div>
        <div>
          <label className="mb-1 block text-xs font-medium text-slate-500">To</label>
          <input type="date" value={filterTo} onChange={(e) => { setFilterTo(e.target.value); setPage(1); }} className="input h-8 text-sm" />
        </div>
      </div>
      <div className="surface overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-100 dark:border-white/[0.07]">
              {['Time', 'Entity', 'Action', 'By', 'Details'].map((h) => (
                <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {loading ? (
              <tr><td colSpan={5} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={5} className="py-12 text-center text-slate-400">No audit logs</td></tr>
            ) : items.map((log) => (
              <tr key={log.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-4 py-2.5 text-slate-500 text-xs whitespace-nowrap">{new Date(log.createdAtUtc).toLocaleString()}</td>
                <td className="px-4 py-2.5 text-slate-700 dark:text-slate-300 text-xs">{log.entityType}</td>
                <td className="px-4 py-2.5"><span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${actionColor(log.action)}`}>{log.action}</span></td>
                <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{log.performedByName}</td>
                <td className="px-4 py-2.5 text-xs text-slate-400 font-mono max-w-xs truncate">{log.entityId}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {total > pageSize && (
        <div className="flex items-center justify-between text-sm text-slate-500">
          <span>{total} total records</span>
          <div className="flex gap-2">
            <button type="button" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page === 1} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">← Prev</button>
            <span className="px-2 py-1">Page {page} of {Math.ceil(total / pageSize)}</span>
            <button type="button" onClick={() => setPage((p) => p + 1)} disabled={page >= Math.ceil(total / pageSize)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Next →</button>
          </div>
        </div>
      )}
    </div>
  );
}

// ─── Email / SMTP Configuration ──────────────────────────────────────────────

function EmailConfigTab() {
  const KEYS = ['Smtp.Host', 'Smtp.Port', 'Smtp.Username', 'Smtp.Password', 'Smtp.FromAddress', 'Smtp.FromName', 'Smtp.UseTls'] as const;
  type SmtpKey = typeof KEYS[number];
  const [cfg, setCfg] = useState<Record<SmtpKey, string>>({
    'Smtp.Host': '', 'Smtp.Port': '587', 'Smtp.Username': '', 'Smtp.Password': '',
    'Smtp.FromAddress': '', 'Smtp.FromName': '', 'Smtp.UseTls': 'true',
  });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    systemSettingsApi.list('Email').then((settings) => {
      const merged = { ...cfg };
      for (const s of settings) {
        if (KEYS.includes(s.settingKey as SmtpKey)) merged[s.settingKey as SmtpKey] = s.settingValue;
      }
      setCfg(merged);
    }).catch(() => {}).finally(() => setLoading(false));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async () => {
    setSaving(true); setSaved(false);
    try {
      for (const key of KEYS) {
        await systemSettingsApi.upsert({ category: 'Email', settingKey: key, settingValue: cfg[key], dataType: 'string', description: key });
      }
      setSaved(true); setTimeout(() => setSaved(false), 3000);
    } catch { /**/ } finally { setSaving(false); }
  };

  const testConnection = async () => {
    setTesting(true); setTestResult(null);
    try {
      await systemSettingsApi.upsert({ category: 'Email', settingKey: 'Smtp.Host', settingValue: cfg['Smtp.Host'], dataType: 'string', description: 'Smtp.Host' });
      setTestResult({ ok: true, message: 'Settings saved. Send a test email from the notification system to verify SMTP connectivity.' });
    } catch { setTestResult({ ok: false, message: 'Failed to save settings.' }); } finally { setTesting(false); }
  };

  if (loading) return <div className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>;

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h2 className="text-base font-semibold text-slate-900 dark:text-white">Email / SMTP Configuration</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Configure outbound email for payslip delivery, alerts, appointment letters, and notifications.
          Credentials are stored encrypted in the database.
        </p>
      </div>

      <div className="surface space-y-4 p-5">
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">SMTP Host *</label>
            <input className="input w-full" placeholder="smtp.example.com" value={cfg['Smtp.Host']} onChange={e => setCfg(c => ({ ...c, 'Smtp.Host': e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Port</label>
            <input type="number" className="input w-full" placeholder="587" value={cfg['Smtp.Port']} onChange={e => setCfg(c => ({ ...c, 'Smtp.Port': e.target.value }))} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Username</label>
            <input className="input w-full" placeholder="noreply@company.com" value={cfg['Smtp.Username']} onChange={e => setCfg(c => ({ ...c, 'Smtp.Username': e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Password</label>
            <input type="password" className="input w-full" placeholder="••••••••" value={cfg['Smtp.Password']} onChange={e => setCfg(c => ({ ...c, 'Smtp.Password': e.target.value }))} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">From Address *</label>
            <input type="email" className="input w-full" placeholder="hr@company.com" value={cfg['Smtp.FromAddress']} onChange={e => setCfg(c => ({ ...c, 'Smtp.FromAddress': e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">From Name</label>
            <input className="input w-full" placeholder="HR Team" value={cfg['Smtp.FromName']} onChange={e => setCfg(c => ({ ...c, 'Smtp.FromName': e.target.value }))} />
          </div>
        </div>
        <div className="flex items-center gap-2">
          <input type="checkbox" id="useTls" className="h-4 w-4 rounded accent-sapphire" checked={cfg['Smtp.UseTls'] === 'true'} onChange={e => setCfg(c => ({ ...c, 'Smtp.UseTls': e.target.checked ? 'true' : 'false' }))} />
          <label htmlFor="useTls" className="text-sm text-slate-700 dark:text-slate-300">Use STARTTLS (recommended)</label>
        </div>
      </div>

      {testResult && (
        <p className={`rounded-lg px-3 py-2 text-sm ${testResult.ok ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-red-50 text-red-600 dark:bg-red-500/10 dark:text-red-400'}`}>
          {testResult.message}
        </p>
      )}
      {saved && <p className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">Settings saved successfully.</p>}

      <div className="flex gap-3">
        <button type="button" className="btn-primary" onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Save Settings'}</button>
        <button type="button" className="btn-secondary" onClick={testConnection} disabled={testing}>{testing ? 'Saving…' : 'Save & Verify'}</button>
      </div>

      <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.03]">
        <p className="text-xs font-semibold text-slate-600 dark:text-slate-400 mb-2">When configured, email is sent for:</p>
        <ul className="space-y-1 text-xs text-slate-500 dark:text-slate-400">
          <li>• Payslip published → employee receives PDF by email</li>
          <li>• Payroll run approved / locked → notify payroll team</li>
          <li>• Appointment & Experience letters → emailed to employee</li>
          <li>• Offer letters → emailed to candidate</li>
          <li>• Leave request approved / rejected → notify employee</li>
          <li>• Any notification template with channel = Email</li>
        </ul>
      </div>
    </div>
  );
}

// ─── Shared helpers ──────────────────────────────────────────────────────────

function TableShell({
  columns, onAdd, addLabel, loading, empty, emptyLabel, filter, actions, children,
}: {
  columns: string[];
  onAdd: () => void;
  addLabel: string;
  loading: boolean;
  empty: boolean;
  emptyLabel: string;
  filter?: React.ReactNode;
  actions?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2">{filter}</div>
        <div className="flex items-center gap-2">
          {actions}
          <button type="button" onClick={onAdd} className="btn-primary">
            <Plus className="h-4 w-4" />
            {addLabel}
          </button>
        </div>
      </div>
      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {columns.map((col) => (
                  <th key={col} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                    {col}
                  </th>
                ))}
                <th className="w-20 px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading && (
                <tr>
                  <td colSpan={columns.length + 1} className="py-12 text-center">
                    <div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
                  </td>
                </tr>
              )}
              {!loading && empty && (
                <tr>
                  <td colSpan={columns.length + 1} className="py-12 text-center text-sm text-slate-400 dark:text-slate-500">
                    {emptyLabel}
                  </td>
                </tr>
              )}
              {!loading && children}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function ActiveBadge({ active }: { active: boolean }) {
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-semibold ${active ? 'bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20' : 'bg-slate-100 text-slate-400 dark:bg-white/10 dark:text-slate-500'}`}>
      <span className={`h-1.5 w-1.5 rounded-full ${active ? 'bg-emeraldZ' : 'bg-slate-400'}`} />
      {active ? 'Active' : 'Inactive'}
    </span>
  );
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: React.ReactNode }) {
  return (
    <div>
      <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label} {required && <span className="text-red-500">*</span>}
      </label>
      {children}
    </div>
  );
}

function FormError({ error }: { error: string }) {
  if (!error) return null;
  return (
    <p className="mb-3 rounded-lg bg-red-50 px-3 py-2.5 text-sm text-red-600 dark:bg-red-500/10 dark:text-red-400">
      {error}
    </p>
  );
}

// ─── Empty form factories ────────────────────────────────────────────────────

const emptyCompany = (): CompanyRequest => ({
  legalNameEn: '', legalNameAr: '', tradeName: '', countryCode: '', jurisdiction: '',
  registrationNumber: '', taxNumber: '', wpsEmployerId: '', gosiEmployerId: '',
  qiwaEstablishmentId: '', defaultCurrency: '', isActive: true,
});

const emptyBranch = (companyId: string): BranchRequest => ({
  companyId, code: '', nameEn: '', nameAr: '', countryCode: '', city: '',
  addressLine1: '', addressLine2: '', timeZoneId: '', laborOfficeCode: '',
  isHeadOffice: false, isActive: true,
});

const emptyDept = (): DepartmentRequest => ({
  code: '', nameEn: '', nameAr: '', costCenterId: undefined, isActive: true,
});

const emptyDesig = (): DesignationRequest => ({
  code: '', titleEn: '', titleAr: '', jobGrade: '', gradeId: undefined, jobLevel: '', jobDescription: '', isManagerRole: false, isActive: true,
});

const emptyGrade = (): GradeRequest => ({
  code: '', name: '', band: '', level: 0, isActive: true,
});

const emptyCostCenter = (companyId?: string): CostCenterRequest => ({
  companyId, code: '', name: '', isActive: true,
});

// ─── SetupPage ───────────────────────────────────────────────────────────────

export function SetupPage() {
  const [activeTab, setActiveTab] = useState<Tab>('companies');
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);

  useEffect(() => {
    companiesApi.list(1, 100).then((r) => setCompanies(r.items)).catch(() => {});
    gradesApi.list(1, 100).then((r) => setGrades(r.items)).catch(() => {});
    costCentersApi.list(undefined, 1, 100).then((r) => setCostCenters(r.items)).catch(() => {});
  }, []);

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Setup & Administration</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          Manage your organization structure, branches, and designations
        </p>
      </div>

      {/* Tab bar — wrapping pill tabs */}
      <div className="flex flex-wrap gap-1.5">
        {tabs.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            onClick={() => setActiveTab(id)}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition-all ${
              activeTab === id
                ? 'bg-sapphire text-white shadow-sm'
                : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/[0.06] dark:text-slate-400 dark:hover:bg-white/[0.10] dark:hover:text-slate-200'
            }`}
          >
            <Icon className="h-3.5 w-3.5 shrink-0" />
            {label}
          </button>
        ))}
      </div>

      {/* Tab content */}
      <div>
        {activeTab === 'companies' && <CompaniesTab />}
        {activeTab === 'branches' && <BranchesTab companies={companies} />}
        {activeTab === 'departments' && <DepartmentsTab costCenters={costCenters} />}
        {activeTab === 'designations' && <DesignationsTab grades={grades} />}
        {activeTab === 'grades' && <GradesTab />}
        {activeTab === 'costCenters' && <CostCentersTab companies={companies} />}
        {activeTab === 'masterData' && <MasterDataTab />}
        {activeTab === 'numberingRules' && <NumberingRulesTab />}
        {activeTab === 'systemSettings' && <SystemSettingsTab />}
        {activeTab === 'gccSettings' && <GCCSettingsTab />}
        {activeTab === 'fiscalYears' && <FiscalYearsTab />}
        {activeTab === 'locations' && <LocationsTab />}
        {activeTab === 'notificationTemplates' && <NotificationTemplatesTab />}
        {activeTab === 'emailConfig' && <EmailConfigTab />}
        {activeTab === 'adminAuditLogs' && <AdminAuditLogsTab />}
      </div>
    </div>
  );
}
