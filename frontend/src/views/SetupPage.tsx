'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { createPortal } from 'react-dom';
import { notifyApiError } from '../api/client';
import { Award, Building2, GitBranch, Layers, Landmark, Tag, Plus, Pencil, Trash2, Database, Hash, Settings, Globe, Calendar, MapPin, Bell, ClipboardList, ChevronRight, ListChecks, Upload, Eye, EyeOff, ShieldCheck, LayoutGrid, FileText, Users, BookOpen, Mail } from 'lucide-react';
import { AiSetupAssistant } from '../components/AiSetupAssistant';
import { InfoTip } from '../components/InfoTip';
import { deriveWorkEmailLocalPart, assembleWorkEmail } from '../api/employees';
import { SetupStudioWorkspace } from '../components/setup/SetupStudioWorkspace';
import { SetupSectionNav, SetupSectionsToggle, type SetupSectionNavGroup, type SetupSectionNavItem } from '../components/setup/SetupSectionNav';
import { SetupSectionHeader } from '../components/setup/SetupSectionHeader';
import { buildSetupSearchIndex, type SetupSearchEntry } from '../components/setup/setupSearch';
import { progressCompany } from '../components/setup/setupProgress';
import { useGuidedSetupCompanies } from '../components/setup/useSetupProgress';
import { readinessProgress, setupLandingCompanyLink } from '../lib/companySetupWizard';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { CompanyRowActions } from '../components/setup/CompanyRowActions';
import { CompanyDraftsCard } from '../components/companySetup/CompanyDraftsCard';
import { GuidedSetupChooser } from '../components/companySetup/GuidedSetupChooser';
import { SetupEditorState, SetupPendingFilesState, useSetupEditorState } from '../components/setup/SetupEditorState';
import { useSetupNavigationGuard } from '../hooks/useSetupNavigationGuard';
import { useSetupCompanyQuery } from '../hooks/useSetupCompanyQuery';
import dynamic from 'next/dynamic';
import { PolicyDocumentManager } from '../components/PolicyDocumentManager';
import { usePolicyIntake } from '../lib/policyIntake';
import { OrgStructureImportPanel } from '../components/OrgStructureImportPanel';
import { EstablishmentPanel } from '../components/EstablishmentPanel';
import { StatutoryRulesPanel } from '../components/StatutoryRulesPanel';
import { GlSetupPanel } from '../components/gl/GlSetupPanel';
import { CountrySelect } from '../components/CountrySelect';
import {
  companiesApi,
  branchesApi,
  departmentsApi,
  designationsApi,
  gradesApi,
  costCentersApi,
  WORK_EMAIL_PATTERNS,
  WORK_EMAIL_PATTERN_LABELS,
  DEFAULT_WORK_EMAIL_PATTERN,
  isValidEmailDomain,
} from '../api/organization';
import { countryPacksApi } from '../api/countryPacks';
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
  GradePayScaleComponentDto,
  GradePayScaleComponentRequest,
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
import { TransliterateButton } from '../components/TransliterateButton';
import { CompanyCell, CompanyFilterSelect, CompanySelectField, UNASSIGNED_FILTER, UnassignedCompanyNotice, filterByCompany, useOrgCompanies } from '../components/setup/OrgCompanyControls';
import { notifyGradesChanged } from '../lib/gradeEvents';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useAuth } from '../contexts/AuthContext';
import { useCompany } from '../contexts/CompanyContext';
import { useT } from '../hooks/useT';
import { SETUP_SECTION_GROUPS, SETUP_SETTING_SECTIONS, SETUP_TOOLS, resolveSetupArea, setupNavigationModel, type SetupSettingId, type SetupSettingSection } from '../lib/setupAreas';
import { resolveSetupPath, serializeSetupPath, type SetupStudioPath } from '../lib/setupPathState';
import { setupCountryCode } from '../lib/setupDraftLifecycle';
import { msg } from '../i18n/translations';
import { useConfirm } from '../components/gl/glUi';
import { useReleaseA } from '../lib/releaseA';
import { glCompanies } from '../lib/setupAccess';
import Link from 'next/link';

// Load the existing module only when its settings are opened in the workspace.
const LeaveTypesTab = dynamic(() => import('./LeavePage').then(module => module.LeaveTypesTab));
const PoliciesTab = dynamic(() => import('./LeavePage').then(module => module.PoliciesTab));
const HolidayCalendarTab = dynamic(() => import('./LeavePage').then(module => module.HolidayCalendarTab));
const OnboardingPolicyWorkspace = dynamic(() => import('../components/setup/OnboardingPolicyWorkspace').then(module => module.OnboardingPolicyWorkspace));

type Tab = 'aiSetup' | 'importOrganization' | 'policyLibrary' | SetupSettingId;
type SettingsTab = SetupSettingId;
/** A navigation entry: a settings section, a setup tool, or one of the three Setup paths that share tab=aiSetup. */
type SetupNavId = Exclude<Tab, 'aiSetup'> | 'guidedSetup' | 'allSetupAreas' | 'policyStarter';
function isSettingsTab(id: string): id is SettingsTab { return tabs.some(tab => tab.id === id); }

const settingIcons: Record<SettingsTab, React.ElementType> = {
  companies: Building2, branches: GitBranch, locations: MapPin, costCenters: Landmark, departments: Layers,
  grades: Award, designations: Tag, establishment: Users,
  gccSettings: Globe, statutoryRules: ShieldCheck, fiscalYears: Calendar, glMapping: BookOpen,
  masterData: Database, numberingRules: Hash, notificationTemplates: Bell, emailConfig: Mail, systemSettings: Settings, adminAuditLogs: ClipboardList,
};

// Names, descriptions and groups come from the shared catalogue (lib/setupAreas.ts), which All setup areas reads too,
// so a section has one name in the navigation, its header and the Studio.
const tabs: (SetupSettingSection & { icon: React.ElementType })[] = SETUP_SETTING_SECTIONS.map(section => ({ ...section, icon: settingIcons[section.id] }));

/** The setup tools at the top of the navigation, keyed by their entry. */
const toolSections: Record<Exclude<SetupNavId, SettingsTab>, { label: string; icon: React.ElementType }> = {
  guidedSetup: { label: SETUP_TOOLS.guidedSetup.label, icon: ListChecks },
  allSetupAreas: { label: SETUP_TOOLS.allSetupAreas.label, icon: LayoutGrid },
  importOrganization: { label: SETUP_TOOLS.importOrganization.label, icon: Upload },
  policyStarter: { label: SETUP_TOOLS.policyStarter.label, icon: FileText },
  policyLibrary: { label: SETUP_TOOLS.policyLibrary.label, icon: ClipboardList },
};
const studioPaths: Record<'guidedSetup' | 'allSetupAreas' | 'policyStarter', SetupStudioPath> = {
  guidedSetup: 'guided', allSetupAreas: 'configuration', policyStarter: 'policyStarter',
};

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
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const { currencyCode } = useTenantSettings();
  const { hasPermission } = useAuth();
  const { accountType, isGroupScope } = useCompany();
  const router = useRouter();
  // Company mutations require organization write access. organization.read-only viewers
  // (e.g. HR Officer, Compliance Officer, Recruiter, HR Assistant, Auditor) reach this
  // tab via the /companies redirect and get the same read-only list they had before,
  // with no inert (always-403) action controls.
  const canWrite = hasPermission('organization.write') || hasPermission('organization.establishment.write');
  // The ERP company code decides whose ledger the ERP books a company's journals into: only the general-ledger
  // manager may set or change it (the server enforces this and explains a refusal; the form only reflects it).
  const canSetErpCode = hasPermission('finance.gl.manage');
  const [items, setItems] = useState<CompanyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CompanyDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<CompanyRequest>(emptyCompany());
  const [saving, setSaving] = useState(false);
  const [availablePacks, setAvailablePacks] = useState<CountryPackOption[]>([]);
  const [statutorySummary, setStatutorySummary] = useState<StatutorySummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);
  const [toggling, setToggling] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await companiesApi.listAll()); } catch { /**/ }
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

  // New companies are created by the guided wizard (company + its setup in one commit, server-side
  // drafts); this modal edits existing companies only.
  const openCreate = () => router.push('/setup/new-company');
  const openEdit = (c: CompanyDto) => {
    setEditing(c);
    setForm({ legalNameEn: c.legalNameEn, legalNameAr: c.legalNameAr, tradeName: c.tradeName, countryCode: c.countryCode, jurisdiction: c.jurisdiction, registrationNumber: c.registrationNumber, taxNumber: c.taxNumber, wpsEmployerId: c.wpsEmployerId, gosiEmployerId: c.gosiEmployerId, qiwaEstablishmentId: c.qiwaEstablishmentId, defaultCurrency: c.defaultCurrency, emailDomain: c.emailDomain ?? '', workEmailPattern: c.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN, isActive: c.isActive, erpCompanyCode: c.erpCompanyCode ?? '' });
    setError('');
    setModalOpen(true);
  };

  // Legal entities with NO country. Their employees cannot be added at all — identity documents,
  // leave entitlements and statutory rules are every one of them resolved from this field — and the
  // Add Employee modal is where the operator used to discover it, several attempts too late.
  // Surfaced here, where the fix is, and NEVER guessed: a country inferred from the currency or the
  // tenant slug would seed the wrong labour law in silence.
  const missingCountry = items.filter((c) => !(c.countryCode ?? '').trim());
  const selectedCountryPack = availablePacks.find(p => setupCountryCode(p.countryCode) === setupCountryCode(form.countryCode));
  const jurisdictions = selectedCountryPack?.jurisdictions ?? [];

  // Field-addressed save errors, shown under the input they belong to rather than in the banner.
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof CompanyRequest, string>>>({});
  useEffect(() => { if (modalOpen) setFieldErrors({}); }, [modalOpen, editing]);
  const companyFieldLabels: Partial<Record<keyof CompanyRequest, string>> = {
    legalNameEn: 'Legal name (English)', countryCode: 'Country', registrationNumber: 'Registration number',
    defaultCurrency: 'Default currency', emailDomain: 'Email domain', erpCompanyCode: 'ERP company code',
  };
  // Fields that render their own inline error; anything else the server names goes to the banner.
  const inlineCompanyFields: ReadonlySet<keyof CompanyRequest> = new Set<keyof CompanyRequest>(['registrationNumber']);
  // ASP.NET validation ProblemDetails: { errors: { "RegistrationNumber": ["The RegistrationNumber field is required."] } }.
  const mapCompanyValidationErrors = (errors: Record<string, unknown>) => {
    const fields: Partial<Record<keyof CompanyRequest, string>> = {};
    const banner: string[] = [];
    for (const [rawKey, raw] of Object.entries(errors)) {
      const segment = (rawKey.split('.').pop() ?? rawKey).replace(/^\$/, '');
      const key = (segment.charAt(0).toLowerCase() + segment.slice(1)) as keyof CompanyRequest;
      const serverMessage = String(Array.isArray(raw) ? raw[0] ?? '' : raw ?? '').trim();
      const label = companyFieldLabels[key];
      const message = label && /required/i.test(serverMessage) ? `${label} is required.` : serverMessage || `${label ?? segment} is invalid.`;
      if (inlineCompanyFields.has(key)) fields[key] = message;
      else banner.push(message);
    }
    return { fields, banner };
  };

  const handleSave = async () => {
    setFieldErrors({});
    if (!form.legalNameEn.trim()) { setError('Legal name (English) is required'); return; }
    // The field has always been marked required; it was never enforced, which is part of how a
    // tenant's first company reached production with an empty country.
    if (!(form.countryCode ?? '').trim()) {
      setError('Country is required — it decides this entity’s statutory rules, leave entitlements and the identity documents its employees must hold.');
      return;
    }
    if (!(form.registrationNumber ?? '').trim()) {
      setError('');
      setFieldErrors({ registrationNumber: 'Registration number is required — it identifies this legal entity on statutory filings.' });
      return;
    }
    if (!(form.defaultCurrency ?? '').trim()) { setError('Default currency is required'); return; }
    // Advisory client-side domain-format check mirroring the server regex (server stays authoritative).
    // Blank is allowed — employees then fall back to manual work-email entry.
    if (!isValidEmailDomain(form.emailDomain ?? '')) {
      setError('Email domain looks invalid. Use a bare domain like "acme.sa" (no "@", scheme, or path), or leave it blank.');
      return;
    }
    if (!editing) return;
    setSaving(true); setError('');
    try {
      await companiesApi.update(editing.id, form);
      setModalOpen(false);
      load();
    } catch (err: unknown) {
      const resp = (err as { response?: { data?: { error?: string; message?: string; field?: string; errors?: Record<string, unknown> } } })?.response?.data;
      if (resp?.error === 'registration_number_taken' || resp?.field === 'registrationNumber')
        setFieldErrors({ registrationNumber: resp.message ?? 'Another company in this workspace already uses this registration number.' });
      else if (resp?.errors && typeof resp.errors === 'object') {
        const mapped = mapCompanyValidationErrors(resp.errors);
        setFieldErrors(mapped.fields);
        setError(mapped.banner.join(' ') || (Object.keys(mapped.fields).length ? '' : 'Failed to save. Please try again.'));
      }
      else setError(resp?.message ?? 'Failed to save. Please try again.');
    }
    finally { setSaving(false); }
  };

  const f = (key: keyof CompanyRequest, v: string | boolean) => {
    setForm((x) => ({ ...x, [key]: v }));
    setFieldErrors((current) => (current[key] ? { ...current, [key]: undefined } : current));
  };
  const deleteCompany = async (id: string) => {
    if (!(await confirm({ title: t('Delete company'), message: t('Delete this company? This cannot be undone.'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await companiesApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  // Suspend / reactivate — merged from the standalone /companies page so no capability
  // is lost when that route folds into Setup. Reproduces the last-active-company guard.
  const toggleStatus = async (c: CompanyDto) => {
    const action = c.isActive ? 'Suspend' : 'Reactivate';
    const name = c.tradeName || c.legalNameEn;
    if (!(await confirm({
      title: c.isActive ? t('Suspend company') : t('Reactivate company'),
      message: c.isActive ? t('Suspend {name}?', { name }) : t('Reactivate {name}?', { name }),
      confirmLabel: c.isActive ? t('Suspend') : t('Reactivate'),
      tone: c.isActive ? 'danger' : 'primary',
    }))) return;
    setToggling(c.id);
    try { await companiesApi.setStatus(c.id, !c.isActive); load(); }
    catch (e: unknown) {
      const err = (e as { response?: { data?: { error?: string } } })?.response?.data;
      if (err?.error === 'last_active_company')
        alert('Cannot deactivate the only active company — activate another company first.');
      else notifyApiError(e);
    } finally { setToggling(null); }
  };

  return (
    <>
      {canWrite && isGroupScope && <CompanyDraftsCard />}
      {missingCountry.length > 0 && (
        <div role="alert" className="mb-3 rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-400/30 dark:bg-amber-500/[0.08] dark:text-amber-200">
          <p className="font-semibold">
            {missingCountry.length === 1
              ? `${missingCountry[0].tradeName?.trim() || missingCountry[0].legalNameEn} has no country set — set it in Setup → Companies before adding employees.`
              : `${missingCountry.length} companies have no country set — set each one here before adding employees.`}
          </p>
          <p className="mt-1 text-xs">
            A company’s country decides its statutory rules, leave entitlements and the identity documents its
            employees must hold. Until it is set, employees of that entity cannot be added.
            {missingCountry.length > 1 && ` Missing: ${missingCountry.map((c) => c.tradeName?.trim() || c.legalNameEn).join(', ')}.`}
          </p>
          {canWrite && (
            <button type="button" onClick={() => openEdit(missingCountry[0])} className="btn-secondary mt-2 h-7 px-2 text-xs">
              <Pencil className="h-3 w-3" /> Set country for {missingCountry[0].tradeName?.trim() || missingCountry[0].legalNameEn}
            </button>
          )}
        </div>
      )}
      <TableShell
        columns={[msg('Legal Name'), msg('Trade Name'), msg('Country'), msg('Currency'), msg('Status')]}
        onAdd={canWrite && isGroupScope && (accountType === 'Group' || items.length === 0) ? openCreate : undefined}
        addLabel={msg('Create company')}
        loading={loading}
        empty={items.length === 0}
        emptyLabel={msg('No companies yet')} emptyHint={msg('Add your first company, or import a CSV.')}
        actions={canWrite ? (
          <ImportExportToolbar
            entityName="Companies"
            onExport={async () => { const csv = await companiesApi.export(); downloadCsv(csv, 'companies.csv'); }}
            onDownloadTemplate={async () => { const csv = await companiesApi.importTemplate(); downloadCsv(csv, 'companies-template.csv'); }}
            onImport={async (csv) => { const r = await companiesApi.import(csv); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        ) : undefined}
      >
        {items.map((c) => (
          <tr key={c.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{c.legalNameEn}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.tradeName || '—'}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">
              {/* "—" read as "nothing to show". It is not: a blank country blocks every employee of
                  this entity, so it is labelled as the problem it is. */}
              {c.countryCode?.trim() || (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-800 dark:bg-amber-500/[0.12] dark:text-amber-300">Not set</span>
              )}
              {c.jurisdiction && <span className="ms-1 text-xs text-slate-400">({c.jurisdiction})</span>}
            </td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.defaultCurrency}</td>
            <td className="px-4 py-3">
              {/* Preserve the standalone page's Draft-awareness and "Suspended" wording,
                  which the plain ActiveBadge (Active/Inactive) did not convey. */}
              {c.approvalStatus === 'Draft' ? (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-700 dark:bg-amber-500/[0.12] dark:text-amber-300">Draft — awaiting platform approval</span>
              ) : c.isActive ? (
                <ActiveBadge active />
              ) : (
                <span className="rounded-full bg-slate-200 px-2 py-0.5 text-[10px] font-semibold text-slate-600 dark:bg-white/[0.08] dark:text-slate-300">Suspended</span>
              )}
            </td>
            <td className="sticky end-0 bg-white px-4 py-3 dark:bg-slate-900">
              {canWrite && <CompanyRowActions companyName={c.tradeName || c.legalNameEn} onEdit={() => openEdit(c)} actions={[
                { label: 'View statutory profile', onSelect: () => { void loadStatutorySummary(c.id); } },
                ...(c.approvalStatus !== 'Draft' ? [{ label: c.isActive ? 'Suspend' : 'Reactivate', disabled: toggling === c.id, onSelect: () => { void toggleStatus(c); } }] : []),
                { label: 'Delete company', destructive: true, disabled: deleting === c.id, onSelect: () => { void deleteCompany(c.id); } },
              ]} />}
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
                  {statutorySummary.jurisdiction && <span className="ms-1 text-xs font-normal text-slate-500">({statutorySummary.jurisdiction})</span>}
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
              <p className="mt-3 border-t border-indigo-200 pt-3 text-xs text-amber-700 dark:border-indigo-700/40 dark:text-amber-300">
                Review the tenant-wide rule history in <Link href="/setup?tab=statutoryRules" className="font-semibold underline">Statutory Rules</Link>. Configure company-specific values under GL &amp; rates.
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
            <div className="flex items-stretch gap-1.5">
              <input type="text" value={form.legalNameAr ?? ''} onChange={(e) => f('legalNameAr', e.target.value)} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={form.legalNameEn} onSuggest={(s) => f('legalNameAr', s)} />
            </div>
          </FormField>
          <FormField label="Trade Name">
            <input type="text" value={form.tradeName ?? ''} onChange={(e) => f('tradeName', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label="Country" required>
            <CountrySelect
              value={setupCountryCode(form.countryCode)}
              onChange={(cc) => {
                const pack = availablePacks.find(p => setupCountryCode(p.countryCode) === cc);
                setForm(current => ({ ...current, countryCode: cc, jurisdiction: pack?.jurisdictions[0]?.code ?? '' }));
              }}
            />
            <p className="mt-1 text-xs text-slate-500">{t('Country selection does not confirm payroll or statutory coverage. Review the statutory profile separately.')}</p>
          </FormField>
          <FormField label={t('Jurisdiction (optional)')} info={t('A special legal regime within the selected country, such as a free zone. Choose only when applicable.')}>
            <select
              value={form.jurisdiction ?? ''}
              onChange={(e) => f('jurisdiction', e.target.value)}
              className="select w-full"
              disabled={!form.countryCode || (!jurisdictions.length && !form.jurisdiction)}
            >
              <option value="">{t('No special jurisdiction selected')}</option>
              {form.jurisdiction && !jurisdictions.some(j => j.code === form.jurisdiction) && <option value={form.jurisdiction}>{form.jurisdiction}</option>}
              {jurisdictions.map((j) => (
                <option key={j.code} value={j.code}>{j.label}</option>
              ))}
            </select>
          </FormField>
          <FormField label={t('Registration Number')} required error={fieldErrors.registrationNumber} errorId="company-registration-number-error">
            <input
              type="text"
              value={form.registrationNumber}
              onChange={(e) => f('registrationNumber', e.target.value)}
              className={`input w-full ${fieldErrors.registrationNumber ? 'border-red-400 focus:border-red-500 dark:border-red-500/60' : ''}`}
              aria-label={t('Registration Number')}
              aria-required="true"
              aria-invalid={fieldErrors.registrationNumber ? true : undefined}
              aria-describedby={fieldErrors.registrationNumber ? 'company-registration-number-error' : undefined}
            />
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
          <FormField label={t('ERP company code')}>
            <input type="text" maxLength={30} value={form.erpCompanyCode ?? ''} onChange={(e) => f('erpCompanyCode', e.target.value.trim())} className="input w-full" placeholder="1000" spellCheck={false}
              readOnly={!canSetErpCode} aria-readonly={!canSetErpCode} data-testid="company-erp-code" />
            <p className="mt-1 text-xs text-slate-400">{t('The code your ERP uses for this legal entity (SAP company code, Oracle balancing segment). Journal exports stamp it on every line.')}</p>
            {!canSetErpCode && <p className="mt-1 text-xs text-slate-500">{t('Only a finance user who manages the general ledger can change this code.')}</p>}
          </FormField>
          <FormField label={t('Work Email Domain (optional)')} info={t('Optional preference for email suggestions. It does not restrict IT-issued employee addresses. It can also help identify your workspace at sign-in.')}>
            <div className="flex items-stretch overflow-hidden rounded-lg border border-slate-200 focus-within:border-sapphire dark:border-white/10">
              <span className="grid place-items-center bg-slate-50 px-2.5 text-sm font-medium text-slate-400 dark:bg-white/[0.04] dark:text-slate-500">@</span>
              <input
                type="text"
                aria-label={t('Work Email Domain (optional)')}
                value={form.emailDomain ?? ''}
                onChange={(e) => f('emailDomain', e.target.value.trim().toLowerCase())}
                className="w-full border-0 bg-transparent px-2.5 py-2 text-sm outline-none focus:ring-0"
                placeholder="acme.sa"
                inputMode="url"
                spellCheck={false}
                autoCapitalize="none"
              />
            </div>
            <p className={`mt-1 text-xs ${form.emailDomain && !isValidEmailDomain(form.emailDomain) ? 'text-rose-500' : 'text-slate-400'}`}>
              {form.emailDomain && !isValidEmailDomain(form.emailDomain)
                ? 'Enter a bare domain like "acme.sa" — no "@", scheme, or path.'
                : t('Optional preference for email suggestions. It does not restrict IT-issued employee addresses. It can also help identify your workspace at sign-in.')}
            </p>
          </FormField>
          <FormField label={t('Work Email Pattern (optional)')} info={t('Choose how a name appears in an optional suggestion. This does not create a mailbox or assign an employee email.')}>
            <select aria-label={t('Work Email Pattern (optional)')} value={form.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN} onChange={(e) => f('workEmailPattern', e.target.value)} className="select w-full">
              {WORK_EMAIL_PATTERNS.map((p) => (
                <option key={p} value={p}>{WORK_EMAIL_PATTERN_LABELS[p]}</option>
              ))}
            </select>
            <p className="mt-1 text-xs text-slate-500" data-testid="work-email-pattern-preview">
              {t('Example employee email')}: <span dir="ltr">{form.emailDomain && isValidEmailDomain(form.emailDomain)
                ? assembleWorkEmail(deriveWorkEmailLocalPart('John Smith', form.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN), form.emailDomain)
                : deriveWorkEmailLocalPart('John Smith', form.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN)}</span>.
              {' '}{form.emailDomain && isValidEmailDomain(form.emailDomain)
                ? t('Choose how a name appears in an optional suggestion. This does not create a mailbox or assign an employee email.')
                : t('Add a domain to preview a complete suggested address. Full work email addresses can still be entered manually.')}
            </p>
          </FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Branches ────────────────────────────────────────────────────────────────

function BranchesTab({ companies }: { companies: CompanyDto[] }) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<BranchDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterCompany, setFilterCompany] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<BranchDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<BranchRequest>(emptyBranch(companies[0]?.id ?? ''));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await branchesApi.listAll(filterCompany || undefined)); } catch { /**/ }
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
    if (!form.nameEn.trim() || !form.code.trim()) { setError(t('Name and code are required')); return; }
    setSaving(true); setError('');
    try {
      if (editing) await branchesApi.update(editing.id, form);
      else await branchesApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save. Please try again.')); }
    finally { setSaving(false); }
  };

  const f = (key: keyof BranchRequest, v: string | boolean) => setForm((x) => ({ ...x, [key]: v }));
  const deleteBranch = async (id: string) => {
    if (!(await confirm({ title: t('Delete branch'), message: t('Delete this branch?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await branchesApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  // The server names each branch's company; the loaded company list is the fallback. Never the raw id.
  const companyName = (b: BranchDto) =>
    b.companyName?.trim() || companies.find((c) => c.id === b.companyId)?.legalNameEn || 'Unknown company';

  return (
    <>
      <TableShell
        columns={[msg('Code'), msg('Name'), msg('Company'), msg('City'), msg('Head Office'), msg('Status')]}
        onAdd={openNew}
        addLabel={msg('Add Branch')}
        loading={loading}
        empty={items.length === 0}
        emptyLabel={msg('No branches yet')} emptyHint={msg('Add your first branch, or import a CSV.')}
        filter={
          companies.length > 0 ? (
            <select value={filterCompany} onChange={(e) => setFilterCompany(e.target.value)} className="select" title={t('Filter by company')}>
              <option value="">{t('All Group Companies')}</option>
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
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{companyName(b)}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{b.city || '—'}</td>
            <td className="px-4 py-3">{b.isHeadOffice ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire dark:bg-sapphire/20">HQ</span> : '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={b.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(b)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteBranch(b.id)} disabled={deleting === b.id} aria-label={t('Delete branch')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? t('Edit Branch') : t('Add Branch')}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label={t('Company')} required>
            <select value={form.companyId} onChange={(e) => f('companyId', e.target.value)} className="select w-full">
              <option value="">{t('Select company')}</option>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}
            </select>
          </FormField>
          <FormField label={t('Code')} required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="DXB-01" />
          </FormField>
          <FormField label={t('Name (EN)')} required>
            <input type="text" value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" placeholder="Dubai Head Office" />
          </FormField>
          <FormField label={t('Name (AR)')}>
            <div className="flex items-stretch gap-1.5">
              <input type="text" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={form.nameEn} kind="organization-label" onSuggest={(s) => f('nameAr', s)} />
            </div>
          </FormField>
          <FormField label={t('Country')} required>
            <CountrySelect value={form.countryCode} onChange={(cc) => f('countryCode', cc)} ariaLabel="Branch country" />
          </FormField>
          <FormField label={t('City')} required>
            <input type="text" value={form.city} onChange={(e) => f('city', e.target.value)} className="input w-full" placeholder="Dubai" />
          </FormField>
          <FormField label={t('Time Zone')} required>
            <input type="text" value={form.timeZoneId} onChange={(e) => f('timeZoneId', e.target.value)} className="input w-full" placeholder="America/New_York" />
          </FormField>
          <FormField label={t('Status')}>
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">{t('Active')}</option>
              <option value="false">{t('Inactive')}</option>
            </select>
          </FormField>
          <div className="col-span-2 flex items-center gap-2">
            <input type="checkbox" id="isHQ" checked={form.isHeadOffice} onChange={(e) => f('isHeadOffice', e.target.checked)} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isHQ" className="text-sm font-medium text-slate-700 dark:text-slate-300">{t('This is the head office')}</label>
          </div>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Departments ─────────────────────────────────────────────────────────────

function DepartmentsTab({ costCenters }: { costCenters: CostCenterDto[] }) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<DepartmentDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<DepartmentDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<DepartmentRequest>(emptyDept());
  // Addendum B: a department belongs to one company.
  const org = useOrgCompanies();
  const [companyFilter, setCompanyFilter] = useState('');
  const unassignedCount = items.filter((d) => !d.companyId).length;
  const shown = filterByCompany(items, companyFilter);
  const importCompanyId = companyFilter && companyFilter !== UNASSIGNED_FILTER ? companyFilter : org.defaultCompanyId || undefined;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await departmentsApi.listAll()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm({ ...emptyDept(), companyId: org.defaultCompanyId || undefined }); setError(''); setModalOpen(true); };
  const openEdit = (d: DepartmentDto) => {
    setEditing(d);
    setForm({ branchId: d.branchId, parentDepartmentId: d.parentDepartmentId, costCenterId: d.costCenterId, code: d.code, nameEn: d.nameEn, nameAr: d.nameAr, managerEmployeeId: d.managerEmployeeId, isActive: d.isActive, companyId: d.companyId ?? undefined });
    setError(''); setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.nameEn.trim() || !form.code.trim()) { setError(t('Name and code are required')); return; }
    if (!form.companyId && org.options.length > 1) { setError(t('Choose the company this belongs to.')); return; }
    setSaving(true); setError('');
    try {
      if (editing) await departmentsApi.update(editing.id, form);
      else await departmentsApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save. Please try again.')); }
    finally { setSaving(false); }
  };

  const f = (key: keyof DepartmentRequest, v: string | boolean | number | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const deleteDept = async (id: string) => {
    if (!(await confirm({ title: t('Delete department'), message: t('Delete this department?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await departmentsApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <UnassignedCompanyNotice count={unassignedCount} onShow={() => setCompanyFilter(UNASSIGNED_FILTER)} />
      <TableShell
        columns={[msg('Code'), msg('Name'), msg('Company'), msg('Status')]}
        onAdd={openNew}
        addLabel={msg('Add Department')}
        loading={loading}
        empty={shown.length === 0}
        emptyLabel={msg('No departments yet')} emptyHint={msg('Add your first department, or import a CSV.')}
        filter={<CompanyFilterSelect value={companyFilter} onChange={setCompanyFilter} options={org.options} hasUnassigned={unassignedCount > 0} />}
        actions={
          <ImportExportToolbar
            entityName="Departments"
            onExport={async () => { const csv = await departmentsApi.export(); downloadCsv(csv, 'departments.csv'); }}
            onDownloadTemplate={async () => { const csv = await departmentsApi.importTemplate(); downloadCsv(csv, 'departments-template.csv'); }}
            onImport={async (csv) => { const r = await departmentsApi.import(csv, importCompanyId); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {shown.map((d) => (
          <tr key={d.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{d.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{d.nameEn}</td>
            <td className="px-4 py-3"><CompanyCell name={org.nameOf(d.companyId)} /></td>
            <td className="px-4 py-3"><ActiveBadge active={d.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(d)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteDept(d.id)} disabled={deleting === d.id} aria-label={t('Delete department')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? t('Edit Department') : t('Add Department')}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Company')} required>
            <CompanySelectField value={form.companyId ?? ''} onChange={(v) => setForm((x) => ({ ...x, companyId: v || undefined, costCenterId: undefined }))}
              options={org.options} locked={!!editing?.companyId} lockedName={org.nameOf(editing?.companyId)} />
          </FormField>
          <FormField label={t('Code')} required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="ENG" />
          </FormField>
          <FormField label={t('Name (EN)')} required>
            <input type="text" value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" placeholder="Engineering" />
          </FormField>
          <FormField label={t('Name (AR)')}>
            <div className="flex items-stretch gap-1.5">
              <input type="text" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={form.nameEn} kind="organization-label" onSuggest={(s) => f('nameAr', s)} />
            </div>
          </FormField>
          <FormField label={t('Cost Center')}>
            <select value={form.costCenterId ?? ''} onChange={(e) => f('costCenterId', e.target.value || undefined)} className="select w-full">
              <option value="">{t('None')}</option>
              {costCenters.filter((c) => !form.companyId || c.companyId === form.companyId).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </FormField>
          <FormField label={t('Status')}>
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">{t('Active')}</option>
              <option value="false">{t('Inactive')}</option>
            </select>
          </FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Designations ─────────────────────────────────────────────────────────────

function DesignationsTab({ grades }: { grades: GradeDto[] }) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<DesignationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<DesignationDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<DesignationRequest>(emptyDesig());
  // Addendum B: a designation belongs to one company, and so does its grade.
  const org = useOrgCompanies();
  const [companyFilter, setCompanyFilter] = useState('');
  const unassignedCount = items.filter((d) => !d.companyId).length;
  const shown = filterByCompany(items, companyFilter);
  const importCompanyId = companyFilter && companyFilter !== UNASSIGNED_FILTER ? companyFilter : org.defaultCompanyId || undefined;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await designationsApi.listAll()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm({ ...emptyDesig(), companyId: org.defaultCompanyId || undefined }); setError(''); setModalOpen(true); };
  const openEdit = (d: DesignationDto) => {
    setEditing(d);
    setForm({ departmentId: d.departmentId, code: d.code, titleEn: d.titleEn, titleAr: d.titleAr, jobGrade: d.jobGrade, gradeId: d.gradeId, jobLevel: d.jobLevel, jobDescription: d.jobDescription, isManagerRole: d.isManagerRole, isActive: d.isActive, companyId: d.companyId ?? undefined });
    setError(''); setModalOpen(true);
  };

  const handleSave = async () => {
    if (!form.titleEn.trim() || !form.code.trim()) { setError(t('Title and code are required')); return; }
    if (!form.companyId && org.options.length > 1) { setError(t('Choose the company this belongs to.')); return; }
    setSaving(true); setError('');
    try {
      if (editing) await designationsApi.update(editing.id, form);
      else await designationsApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save. Please try again.')); }
    finally { setSaving(false); }
  };

  const f = (key: keyof DesignationRequest, v: string | boolean | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const deleteDesig = async (id: string) => {
    if (!(await confirm({ title: t('Delete designation'), message: t('Delete this designation?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await designationsApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <UnassignedCompanyNotice count={unassignedCount} onShow={() => setCompanyFilter(UNASSIGNED_FILTER)} />
      <TableShell
        columns={[msg('Code'), msg('Title'), msg('Company'), msg('Job Grade'), msg('Manager Role'), msg('Status')]}
        onAdd={openNew}
        addLabel={msg('Add Designation')}
        loading={loading}
        empty={shown.length === 0}
        filter={<CompanyFilterSelect value={companyFilter} onChange={setCompanyFilter} options={org.options} hasUnassigned={unassignedCount > 0} />}
        emptyLabel={msg('No designations yet')} emptyHint={msg('Add your first designation, or import a CSV.')}
        actions={
          <ImportExportToolbar
            entityName="Designations"
            onExport={async () => { const csv = await designationsApi.export(); downloadCsv(csv, 'designations.csv'); }}
            onDownloadTemplate={async () => { const csv = await designationsApi.importTemplate(); downloadCsv(csv, 'designations-template.csv'); }}
            onImport={async (csv) => { const r = await designationsApi.import(csv, importCompanyId); load(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {shown.map((d) => (
          <tr key={d.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{d.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{d.titleEn}</td>
            <td className="px-4 py-3"><CompanyCell name={org.nameOf(d.companyId)} /></td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{d.jobGrade || '—'}</td>
            <td className="px-4 py-3">{d.isManagerRole ? <span className="rounded-full bg-violet-500/10 px-2 py-0.5 text-xs font-semibold text-violet-600 dark:text-violet-400">{t('Manager')}</span> : '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={d.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(d)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteDesig(d.id)} disabled={deleting === d.id} aria-label={t('Delete designation')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>

      <Modal
        isOpen={modalOpen}
        title={editing ? t('Edit Designation') : t('Add Designation')}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button>
          </>
        }
      >
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Company')} required>
            <CompanySelectField value={form.companyId ?? ''} onChange={(v) => setForm((x) => ({ ...x, companyId: v || undefined, gradeId: undefined, departmentId: undefined }))}
              options={org.options} locked={!!editing?.companyId} lockedName={org.nameOf(editing?.companyId)} />
          </FormField>
          <FormField label={t('Code')} required>
            <input type="text" value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="SWE-L3" />
          </FormField>
          <FormField label={t('Title (EN)')} required>
            <input type="text" value={form.titleEn} onChange={(e) => f('titleEn', e.target.value)} className="input w-full" placeholder="Software Engineer" />
          </FormField>
          <FormField label={t('Title (AR)')}>
            <div className="flex items-stretch gap-1.5">
              <input type="text" value={form.titleAr ?? ''} onChange={(e) => f('titleAr', e.target.value)} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={form.titleEn} kind="organization-label" onSuggest={(s) => f('titleAr', s)} />
            </div>
          </FormField>
          <FormField label={t('Job Grade')}>
            <input type="text" value={form.jobGrade ?? ''} onChange={(e) => f('jobGrade', e.target.value)} className="input w-full" placeholder="L3" />
          </FormField>
          <FormField label={t('Grade / Band')}>
            <select value={form.gradeId ?? ''} onChange={(e) => f('gradeId', e.target.value || undefined)} className="select w-full">
              <option value="">{t('None')}</option>
              {grades.filter((g) => !form.companyId || g.companyId === form.companyId).map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </FormField>
          <FormField label={t('Job Level')}>
            <input type="text" value={form.jobLevel ?? ''} onChange={(e) => f('jobLevel', e.target.value)} className="input w-full" placeholder="Officer" />
          </FormField>
          <FormField label={t('Job Description')}>
            <textarea value={form.jobDescription ?? ''} onChange={(e) => f('jobDescription', e.target.value)} className="input min-h-24 w-full" placeholder={t('Core responsibilities and scope')} />
          </FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="isMgr" checked={form.isManagerRole} onChange={(e) => f('isManagerRole', e.target.checked)} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isMgr" className="text-sm font-medium text-slate-700 dark:text-slate-300">{t('Manager role')}</label>
          </div>
          <FormField label={t('Status')}>
            <select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">{t('Active')}</option>
              <option value="false">{t('Inactive')}</option>
            </select>
          </FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Grades ─────────────────────────────────────────────────────────────────

function GradesTab() {
  // Release A (R1): grade benefits live in Benefits by grade. For a release_a tenant the pay-scale lines are frozen:
  // not shown for editing and never written (the API refuses them with 409); the rows are kept and importable there.
  const benefitsByGrade = useReleaseA();
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<GradeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GradeDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<GradeRequest>(emptyGrade());
  const [payScale, setPayScale] = useState<GradePayScaleComponentRequest[]>([]);
  // Addendum B: a grade belongs to one company and takes its currency by default.
  const org = useOrgCompanies();
  const [companyFilter, setCompanyFilter] = useState('');
  const unassignedCount = items.filter((g) => !g.companyId).length;
  const shown = filterByCompany(items, companyFilter);
  const importCompanyId = companyFilter && companyFilter !== UNASSIGNED_FILTER ? companyFilter : org.defaultCompanyId || undefined;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await gradesApi.listAll()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm({ ...emptyGrade(), companyId: org.defaultCompanyId || undefined }); setPayScale([]); setError(''); setModalOpen(true); };
  const openEdit = async (g: GradeDto) => {
    setEditing(g);
    setForm({ code: g.code, name: g.name, nameAr: g.nameAr ?? '', band: g.band, level: g.level, minSalary: g.minSalary, midSalary: g.midSalary, maxSalary: g.maxSalary, currency: g.currency, isActive: g.isActive, companyId: g.companyId ?? undefined });
    setPayScale([]);
    setError('');
    setModalOpen(true);
    try {
      const comps = await gradesApi.getPayScale(g.id);
      setPayScale(comps.map(({ id, ...rest }: GradePayScaleComponentDto) => rest));
    } catch { /* empty pay scale */ }
  };
  const handleSave = async () => {
    if (!form.code.trim() || !form.name.trim()) { setError(t('Code and name are required')); return; }
    if (!form.companyId && org.options.length > 1) { setError(t('Choose the company this belongs to.')); return; }
    if ((form.maxSalary ?? 0) > 0 && (form.minSalary ?? 0) > (form.maxSalary ?? 0)) { setError(t('Min salary cannot exceed max salary')); return; }
    setSaving(true); setError('');
    // The grade itself is saved before its pay scale: open employee forms must hear about it even if the pay
    // scale write then fails.
    let gradeSaved = false;
    try {
      const saved = editing ? await gradesApi.update(editing.id, form) : await gradesApi.create(form);
      gradeSaved = true;
      if (!benefitsByGrade) await gradesApi.setPayScale(saved.id, payScale);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save. Please try again.')); }
    finally { setSaving(false); if (gradeSaved) notifyGradesChanged(); }
  };
  const f = (key: keyof GradeRequest, v: string | boolean | number) => setForm((x) => ({ ...x, [key]: v }));
  const deleteGrade = async (id: string) => {
    if (!(await confirm({ title: t('Delete grade'), message: t('Delete this grade?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await gradesApi.remove(id); load(); notifyGradesChanged(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  const payRange = (g: GradeDto) =>
    g.maxSalary > 0 ? `${g.currency} ${g.minSalary.toLocaleString()}–${g.maxSalary.toLocaleString()}` : '—';

  return (
    <>
      <UnassignedCompanyNotice count={unassignedCount} onShow={() => setCompanyFilter(UNASSIGNED_FILTER)} />
      <TableShell columns={[msg('Code'), msg('Name'), msg('Company'), msg('Band'), msg('Level'), msg('Pay Range'), msg('Status')]} onAdd={openNew} addLabel={msg('Add Grade')} loading={loading} empty={shown.length === 0} emptyLabel={msg('No grades yet')} emptyHint={msg('Add your first grade, or import a CSV.')}
        filter={<CompanyFilterSelect value={companyFilter} onChange={setCompanyFilter} options={org.options} hasUnassigned={unassignedCount > 0} />}
        actions={
          <ImportExportToolbar
            entityName="Grades"
            onExport={async () => { const csv = await gradesApi.export(); downloadCsv(csv, 'grades.csv'); }}
            onDownloadTemplate={async () => { const csv = await gradesApi.importTemplate(); downloadCsv(csv, 'grades-template.csv'); }}
            onImport={async (csv) => { const r = await gradesApi.import(csv, importCompanyId); load(); notifyGradesChanged(); return { received: r.received, created: r.created, skipped: r.skipped, errors: r.errors }; }}
          />
        }
      >
        {shown.map((g) => (
          <tr key={g.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{g.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{g.name}</td>
            <td className="px-4 py-3"><CompanyCell name={org.nameOf(g.companyId)} /></td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{g.band || '—'}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{g.level}</td>
            <td className="px-4 py-3 tabular-nums text-slate-600 dark:text-slate-300">{payRange(g)}</td>
            <td className="px-4 py-3"><ActiveBadge active={g.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(g)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteGrade(g.id)} disabled={deleting === g.id} aria-label={t('Delete grade')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? t('Edit Grade') : t('Add Grade')} onClose={() => setModalOpen(false)} footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Company')} required>
            <CompanySelectField value={form.companyId ?? ''} onChange={(v) => setForm((x) => ({ ...x, companyId: v || undefined }))}
              options={org.options} locked={!!editing?.companyId} lockedName={org.nameOf(editing?.companyId)} />
          </FormField>
          <div className="grid grid-cols-2 gap-3">
            <FormField label={t('Code')} required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="G5" /></FormField>
            <FormField label={t('Level')}><input type="number" value={form.level} onChange={(e) => f('level', Number(e.target.value))} className="input w-full" /></FormField>
          </div>
          <FormField label={t('Name')} required><input value={form.name} onChange={(e) => f('name', e.target.value)} className="input w-full" placeholder="Professional Grade 5" /></FormField>
          <FormField label={t('Name (Arabic, optional)')}><input dir="rtl" lang="ar" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full" placeholder="الدرجة المهنية 5" /></FormField>
          <FormField label={t('Band')}><input value={form.band ?? ''} onChange={(e) => f('band', e.target.value)} className="input w-full" placeholder="Professional" /></FormField>

          {/* Pay scale band */}
          <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
            <p className="mb-2 text-[10px] font-bold uppercase tracking-[0.16em] text-slate-400 dark:text-slate-500">{t('Pay Scale')}</p>
            <div className="grid grid-cols-4 gap-2">
              <FormField label={t('Min')}><input type="number" value={form.minSalary ?? 0} onChange={(e) => f('minSalary', Number(e.target.value))} className="input w-full" /></FormField>
              <FormField label={t('Mid')}><input type="number" value={form.midSalary ?? 0} onChange={(e) => f('midSalary', Number(e.target.value))} className="input w-full" /></FormField>
              <FormField label={t('Max')}><input type="number" value={form.maxSalary ?? 0} onChange={(e) => f('maxSalary', Number(e.target.value))} className="input w-full" /></FormField>
              <FormField label={t('Currency')}><input value={form.currency ?? ''} onChange={(e) => f('currency', e.target.value)} className="input w-full" placeholder={t('Company currency')} /></FormField>
            </div>
            {benefitsByGrade
              ? <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('Allowances and benefits for this grade are set in Benefits by grade.')}{' '}
                <Link href="/benefits/by-grade" className="text-sapphire underline">{t('Open Benefits by grade')}</Link></p>
              : <PayScaleEditor components={payScale} onChange={setPayScale} />}
          </div>

          <FormField label={t('Status')}><select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full"><option value="true">{t('Active')}</option><option value="false">{t('Inactive')}</option></select></FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

/** Editable grid of benefit/earning lines that make up a grade's standard package. */
function PayScaleEditor({ components, onChange }: { components: GradePayScaleComponentRequest[]; onChange: (c: GradePayScaleComponentRequest[]) => void }) {
  const t = useT();
  const update = (i: number, patch: Partial<GradePayScaleComponentRequest>) =>
    onChange(components.map((c, idx) => (idx === i ? { ...c, ...patch } : c)));
  const remove = (i: number) => onChange(components.filter((_, idx) => idx !== i));
  const add = () => onChange([...components, {
    componentCode: '', componentName: '', componentType: 'Earning', calculationType: 'Fixed',
    amount: 0, percentage: 0, isTaxable: false, frequency: 'Monthly', sortOrder: components.length + 1, isActive: true,
  }]);

  return (
    <div className="mt-3">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-xs font-medium text-slate-500 dark:text-slate-400">{t('Benefit components')}</span>
        <button type="button" onClick={add} className="btn-secondary h-6 px-2 text-xs">+ {t('Add component')}</button>
      </div>
      {components.length === 0 ? (
        <p className="py-2 text-xs text-slate-500 dark:text-slate-400">{t('No components — e.g. Basic, Housing, Transport, Ticket, Insurance, Health.')}</p>
      ) : (
        <div className="space-y-1.5">
          {components.map((c, i) => (
            <div key={i} className="grid grid-cols-12 items-center gap-1.5">
              <input aria-label={t('Component code')} value={c.componentCode} onChange={(e) => update(i, { componentCode: e.target.value })} className="input col-span-2 h-8 text-xs" placeholder="BASIC" />
              <input aria-label={t('Component name')} value={c.componentName} onChange={(e) => update(i, { componentName: e.target.value })} className="input col-span-3 h-8 text-xs" placeholder={t('Basic')} />
              <select aria-label={t('Component type')} title={t('Component type')} value={c.componentType} onChange={(e) => update(i, { componentType: e.target.value })} className="select col-span-2 h-8 text-xs"><option value="Earning">{t('Earning')}</option><option value="Benefit">{t('Benefit')}</option><option value="Deduction">{t('Deduction')}</option></select>
              <select aria-label={t('Calculation type')} title={t('Calculation type')} value={c.calculationType} onChange={(e) => update(i, { calculationType: e.target.value })} className="select col-span-2 h-8 text-xs"><option value="Fixed">{t('Fixed')}</option><option value="PercentOfBasic">{t('% of Basic')}</option></select>
              {c.calculationType === 'PercentOfBasic'
                ? <input aria-label={t('Percentage of basic')} type="number" value={c.percentage} onChange={(e) => update(i, { percentage: Number(e.target.value) })} className="input col-span-2 h-8 text-xs" placeholder="%" />
                : <input aria-label={t('Amount')} type="number" value={c.amount} onChange={(e) => update(i, { amount: Number(e.target.value) })} className="input col-span-2 h-8 text-xs" placeholder={t('Amount')} />}
              <button type="button" onClick={() => remove(i)} aria-label={t('Remove component')} className="col-span-1 grid h-8 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:text-rose-500 dark:border-white/10"><Trash2 className="h-3.5 w-3.5" /></button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ─── Cost Centers ───────────────────────────────────────────────────────────

function CostCentersTab({ companies }: { companies: CompanyDto[] }) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CostCenterDto | null>(null);
  useSetupEditorState(modalOpen);
  const [form, setForm] = useState<CostCenterRequest>(emptyCostCenter());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [deleting, setDeleting] = useState<string | null>(null);
  // Addendum B: a cost centre belongs to one company; the switcher's company is the default for a new one.
  const org = useOrgCompanies(companies);
  const [companyFilter, setCompanyFilter] = useState('');
  const unassignedCount = items.filter((c) => !c.companyId).length;
  const shown = filterByCompany(items, companyFilter);
  const importCompanyId = companyFilter && companyFilter !== UNASSIGNED_FILTER ? companyFilter : org.defaultCompanyId || undefined;

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await costCentersApi.listAll()); } catch { /**/ }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const openNew = () => { setEditing(null); setForm(emptyCostCenter(org.defaultCompanyId || undefined)); setError(''); setModalOpen(true); };
  const openEdit = (c: CostCenterDto) => {
    setEditing(c);
    setForm({ companyId: c.companyId, code: c.code, name: c.name, nameAr: c.nameAr ?? '', isActive: c.isActive });
    setError('');
    setModalOpen(true);
  };
  const handleSave = async () => {
    if (!form.code.trim() || !form.name.trim()) { setError(t('Code and name are required')); return; }
    if (!form.companyId && org.options.length > 1) { setError(t('Choose the company this belongs to.')); return; }
    setSaving(true); setError('');
    try {
      if (editing) await costCentersApi.update(editing.id, form);
      else await costCentersApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save. Please try again.')); }
    finally { setSaving(false); }
  };
  const f = (key: keyof CostCenterRequest, v: string | boolean | undefined) => setForm((x) => ({ ...x, [key]: v }));
  const deleteCostCenter = async (id: string) => {
    if (!(await confirm({ title: t('Delete cost center'), message: t('Delete this cost center?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await costCentersApi.remove(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <UnassignedCompanyNotice count={unassignedCount} onShow={() => setCompanyFilter(UNASSIGNED_FILTER)} />
      <TableShell columns={[msg('Code'), msg('Name'), msg('Company'), msg('Status')]} onAdd={openNew} addLabel={msg('Add Cost Center')} loading={loading} empty={shown.length === 0} emptyLabel={msg('No cost centres yet')} emptyHint={msg('Add your first cost centre, or import a CSV.')}
        filter={<CompanyFilterSelect value={companyFilter} onChange={setCompanyFilter} options={org.options} hasUnassigned={unassignedCount > 0} />}
        actions={
          <ImportExportToolbar
            entityName="Cost Centers"
            onExport={async () => { const csv = await costCentersApi.export(); downloadCsv(csv, 'cost-centers.csv'); }}
            onDownloadTemplate={async () => { const csv = await costCentersApi.importTemplate(); downloadCsv(csv, 'cost-centers-template.csv'); }}
            onImport={async (csv) => { const r = await costCentersApi.import(csv, importCompanyId); load(); return r; }}
          />
        }
      >
        {shown.map((c) => (
          <tr key={c.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500 dark:text-slate-400">{c.code}</td>
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{c.name}</td>
            <td className="px-4 py-3"><CompanyCell name={org.nameOf(c.companyId)} /></td>
            <td className="px-4 py-3"><ActiveBadge active={c.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(c)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteCostCenter(c.id)} disabled={deleting === c.id} aria-label={t('Delete cost center')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? t('Edit Cost Center') : t('Add Cost Center')} onClose={() => setModalOpen(false)} footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={handleSave} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Company')} required>
            <CompanySelectField value={form.companyId ?? ''} onChange={(v) => f('companyId', v || undefined)}
              options={org.options} locked={!!editing?.companyId} lockedName={org.nameOf(editing?.companyId)} />
          </FormField>
          <FormField label={t('Code')} required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="HR-OPS" /></FormField>
          <FormField label={t('Name')} required><input value={form.name} onChange={(e) => f('name', e.target.value)} className="input w-full" placeholder="HR Operations" /></FormField>
          <FormField label={t('Name (AR)')}>
            <div className="flex items-stretch gap-1.5">
              <input type="text" value={form.nameAr ?? ''} onChange={(e) => f('nameAr', e.target.value)} className="input w-full flex-1" dir="rtl" lang="ar" />
              <TransliterateButton source={form.name} kind="organization-label" onSuggest={(s) => f('nameAr', s)} />
            </div>
          </FormField>
          <FormField label={t('Status')}><select value={form.isActive ? 'true' : 'false'} onChange={(e) => f('isActive', e.target.value === 'true')} className="select w-full"><option value="true">{t('Active')}</option><option value="false">{t('Inactive')}</option></select></FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Master Data ─────────────────────────────────────────────────────────────

function MasterDataTab() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [types, setTypes] = useState<MasterDataType[]>([]);
  const [values, setValues] = useState<MasterDataValue[]>([]);
  const [selectedType, setSelectedType] = useState<MasterDataType | null>(null);
  const [loading, setLoading] = useState(true);
  const [valuesLoading, setValuesLoading] = useState(false);
  const [typeModal, setTypeModal] = useState(false);
  const [valueModal, setValueModal] = useState(false);
  const [editingType, setEditingType] = useState<MasterDataType | null>(null);
  const [editingValue, setEditingValue] = useState<MasterDataValue | null>(null);
  useSetupEditorState(typeModal || valueModal);
  // isActive travels with every edit: the API keeps the current status only when it is absent, and the
  // form always knows it, so a rename can never silently reactivate (or retire) a record.
  const [typeForm, setTypeForm] = useState({ code: '', nameEn: '', nameAr: '', description: '', allowCustomValues: true, isActive: true });
  const [valueForm, setValueForm] = useState({ code: '', valueEn: '', valueAr: '', sortOrder: 0, isDefault: false, isActive: true });
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

  const selectType = (type: MasterDataType) => {
    setSelectedType(type);
    loadValues(type.id);
  };

  const openNewType = () => {
    setEditingType(null);
    setTypeForm({ code: '', nameEn: '', nameAr: '', description: '', allowCustomValues: true, isActive: true });
    setError(''); setTypeModal(true);
  };
  const openEditType = (type: MasterDataType) => {
    setEditingType(type);
    setTypeForm({ code: type.code, nameEn: type.nameEn, nameAr: type.nameAr, description: type.description, allowCustomValues: type.allowCustomValues, isActive: type.isActive });
    setError(''); setTypeModal(true);
  };
  const saveType = async () => {
    if (!typeForm.nameEn.trim() || !typeForm.code.trim()) { setError(t('Code and name are required.')); return; }
    setSaving(true); setError('');
    try {
      if (editingType) await masterDataApi.updateType(editingType.id, typeForm);
      else await masterDataApi.createType({ code: typeForm.code, nameEn: typeForm.nameEn, nameAr: typeForm.nameAr, description: typeForm.description, allowCustomValues: typeForm.allowCustomValues });
      setTypeModal(false); loadTypes();
      if (editingType && selectedType?.id === editingType.id) setSelectedType({ ...selectedType, ...typeForm });
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };

  const openNewValue = () => {
    setEditingValue(null);
    setValueForm({ code: '', valueEn: '', valueAr: '', sortOrder: 0, isDefault: false, isActive: true });
    setError(''); setValueModal(true);
  };
  const openEditValue = (v: MasterDataValue) => {
    setEditingValue(v);
    setValueForm({ code: v.code, valueEn: v.valueEn, valueAr: v.valueAr, sortOrder: v.sortOrder, isDefault: v.isDefault, isActive: v.isActive });
    setError(''); setValueModal(true);
  };
  const saveValue = async () => {
    if (!selectedType || !valueForm.valueEn.trim() || !valueForm.code.trim()) { setError(t('Code and value are required.')); return; }
    setSaving(true); setError('');
    try {
      if (editingValue) await masterDataApi.updateValue(editingValue.id, { ...valueForm });
      else await masterDataApi.createValue(selectedType.id, { code: valueForm.code, valueEn: valueForm.valueEn, valueAr: valueForm.valueAr, sortOrder: valueForm.sortOrder, isDefault: valueForm.isDefault });
      setValueModal(false); loadValues(selectedType.id);
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };
  const deleteValue = async (v: MasterDataValue) => {
    if (v.isSystemDefined) return;
    const ok = await confirm({
      title: t('Delete value'),
      message: t('Delete "{value}"? Employees who already hold this value keep it, but it can no longer be chosen.', { value: v.valueEn }),
      confirmLabel: t('Delete'),
      tone: 'danger',
    });
    if (!ok) return;
    try { await masterDataApi.deleteValue(v.id); if (selectedType) loadValues(selectedType.id); } catch (e) { notifyApiError(e); }
  };

  return (
    <div className="flex min-h-[400px] min-w-0 flex-col gap-4 md:flex-row">
      {!loading && types.length === 0 ? (
        // Nothing to pick from yet: one full-width card that explains the section and offers its first action,
        // instead of an empty list beside an empty "select a type" panel.
        <section aria-labelledby="drop-down-lists-empty" className="surface flex w-full flex-1 flex-col items-center justify-center px-6 py-12 text-center">
          <Database aria-hidden="true" className="h-8 w-8 text-slate-400 dark:text-slate-500" />
          <h3 id="drop-down-lists-empty" className="mt-3 text-base font-semibold text-slate-950 dark:text-white">{t('No drop-down lists yet')}</h3>
          <p className="mt-1 max-w-md text-sm leading-6 text-slate-600 dark:text-slate-300">{t('Drop-down lists hold the choices people pick in forms, such as contract type or marital status.')}</p>
          <button type="button" onClick={openNewType} className="btn-primary mt-5"><Plus className="h-4 w-4" aria-hidden="true" /> {t('New list')}</button>
        </section>
      ) : (<>
      {/* Types list */}
      <div className="w-full shrink-0 md:w-72">
        <div className="surface p-3">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-xs font-bold uppercase tracking-wide text-slate-400">{t('Types')}</span>
            <button type="button" onClick={openNewType} className="btn-primary h-7 px-2 text-xs"><Plus className="h-3 w-3" /> {t('New type')}</button>
          </div>
          {loading ? (
            <div className="flex justify-center py-8"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
          ) : types.length === 0 ? (
            <p className="py-6 text-center text-xs text-slate-500 dark:text-slate-400">{t('No types yet')}</p>
          ) : (
            <ul className="space-y-0.5">
              {types.map((type) => (
                <li key={type.id}>
                  <div
                    role="button"
                    tabIndex={0}
                    onClick={() => selectType(type)}
                    onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectType(type); } }}
                    className={`group flex w-full cursor-pointer items-center justify-between rounded-lg px-3 py-2 text-sm ${selectedType?.id === type.id ? 'bg-sapphire/10 text-sapphire font-semibold' : 'hover:bg-slate-50 dark:hover:bg-white/[0.03] text-slate-700 dark:text-slate-300'}`}
                  >
                    <span className={`truncate ${type.isActive ? '' : 'text-slate-400 line-through'}`}>{type.nameEn}</span>
                    <div className="flex items-center gap-1">
                      {!type.isActive && <span className="rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-300">{t('Inactive')}</span>}
                      {!type.isSystemDefined && (
                        <button type="button" aria-label={t('Edit {name}', { name: type.nameEn })} onClick={(e) => { e.stopPropagation(); openEditType(type); }} className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs font-medium text-slate-500 hover:bg-slate-200 hover:text-slate-700 dark:text-slate-300 dark:hover:bg-white/10">
                          <Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}
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
      <div className="min-w-0 flex-1">
        {!selectedType ? (
          <div className="surface flex h-full items-center justify-center">
            <p className="text-sm text-slate-500 dark:text-slate-400">{t('Select a type to manage values')}</p>
          </div>
        ) : (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <div>
                <h3 className="font-semibold text-slate-900 dark:text-white">{selectedType.nameEn}</h3>
                <p className="text-xs text-slate-400">{selectedType.description || selectedType.code}</p>
              </div>
              <button type="button" onClick={openNewValue} className="btn-primary h-8 px-3 text-sm"><Plus className="h-3.5 w-3.5" /> {t('Add value')}</button>
            </div>
            <div className="surface overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {[msg('Code'), msg('Value (English)'), msg('Value (Arabic)'), msg('Sort order'), msg('Default'), msg('Status'), msg('Actions')].map((h) => (
                      <th key={h} className="px-4 py-2.5 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{t(h)}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {valuesLoading ? (
                    <tr><td colSpan={7} className="py-8 text-center"><div className="mx-auto h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
                  ) : values.length === 0 ? (
                    <tr><td colSpan={7} className="py-8 text-center text-sm text-slate-500 dark:text-slate-400">{t('No values yet')}</td></tr>
                  ) : values.map((v) => (
                    <tr key={v.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-4 py-2.5 font-mono text-xs text-slate-500">{v.code}</td>
                      <td className="px-4 py-2.5 font-medium text-slate-900 dark:text-white">{v.valueEn}</td>
                      <td className="px-4 py-2.5 text-slate-500 dark:text-slate-400" dir="rtl">{v.valueAr}</td>
                      <td className="px-4 py-2.5 text-slate-500">{v.sortOrder}</td>
                      <td className="px-4 py-2.5">{v.isDefault ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">{t('Default')}</span> : '—'}</td>
                      <td className="px-4 py-2.5"><ActiveBadge active={v.isActive} /></td>
                      <td className="px-4 py-2.5">
                        <div className="flex gap-1">
                          <button type="button" onClick={() => openEditValue(v)} aria-label={t('Edit {name}', { name: v.valueEn })} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                          {!v.isSystemDefined && <button type="button" onClick={() => deleteValue(v)} aria-label={t('Delete {name}', { name: v.valueEn })} className={DELETE_ROW_BUTTON}><Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}</button>}
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
      </>)}

      {/* Type Modal */}
      <Modal isOpen={typeModal} title={editingType ? t('Edit type') : t('New type')} onClose={() => setTypeModal(false)}
        footer={<><button type="button" onClick={() => setTypeModal(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={saveType} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Code')} required><input value={typeForm.code} onChange={(e) => setTypeForm(x => ({ ...x, code: e.target.value }))} className="input w-full" placeholder="NATIONALITY" /></FormField>
          <FormField label={t('Name (English)')} required><input value={typeForm.nameEn} onChange={(e) => setTypeForm(x => ({ ...x, nameEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label={t('Name (Arabic)')}>
            <div className="flex items-stretch gap-1.5">
              <input value={typeForm.nameAr} onChange={(e) => setTypeForm(x => ({ ...x, nameAr: e.target.value }))} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={typeForm.nameEn} kind="organization-label" onSuggest={(s) => setTypeForm(x => ({ ...x, nameAr: s }))} />
            </div>
          </FormField>
          <FormField label={t('Description')}><input value={typeForm.description} onChange={(e) => setTypeForm(x => ({ ...x, description: e.target.value }))} className="input w-full" /></FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="allowCustom" checked={typeForm.allowCustomValues} onChange={(e) => setTypeForm(x => ({ ...x, allowCustomValues: e.target.checked }))} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="allowCustom" className="text-sm text-slate-700 dark:text-slate-300">{t('Allow custom values')}</label>
          </div>
          {editingType && <FormField label={t('Status')}><select aria-label={t('Status')} value={typeForm.isActive ? 'true' : 'false'} onChange={(e) => setTypeForm(x => ({ ...x, isActive: e.target.value === 'true' }))} className="select w-full"><option value="true">{t('Active')}</option><option value="false">{t('Inactive')}</option></select></FormField>}
        </div>
      </Modal>

      {/* Value Modal */}
      <Modal isOpen={valueModal} title={editingValue ? t('Edit value') : t('New value')} onClose={() => setValueModal(false)}
        footer={<><button type="button" onClick={() => setValueModal(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={saveValue} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Code')} required>
            <input value={valueForm.code} onChange={(e) => setValueForm(x => ({ ...x, code: e.target.value }))} className="input w-full disabled:bg-slate-50 disabled:text-slate-500 dark:disabled:bg-white/5" placeholder="UAE" disabled={!!editingValue} aria-describedby="value-code-hint" />
            <p id="value-code-hint" className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              {editingValue
                ? t('Employee records store this code, so it cannot change. Rename the value instead; existing records follow the new name.')
                : t('Employee records store this code. Choose it carefully: it cannot be changed later.')}
            </p>
          </FormField>
          <FormField label={t('Value (English)')} required><input value={valueForm.valueEn} onChange={(e) => setValueForm(x => ({ ...x, valueEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label={t('Value (Arabic)')}>
            <div className="flex items-stretch gap-1.5">
              <input value={valueForm.valueAr} onChange={(e) => setValueForm(x => ({ ...x, valueAr: e.target.value }))} className="input w-full flex-1" dir="rtl" />
              <TransliterateButton source={valueForm.valueEn} kind="organization-label" onSuggest={(s) => setValueForm(x => ({ ...x, valueAr: s }))} />
            </div>
          </FormField>
          <FormField label={t('Sort order')}><input type="number" value={valueForm.sortOrder} onChange={(e) => setValueForm(x => ({ ...x, sortOrder: Number(e.target.value) }))} className="input w-full" /></FormField>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="isDefault" checked={valueForm.isDefault} onChange={(e) => setValueForm(x => ({ ...x, isDefault: e.target.checked }))} className="h-4 w-4 accent-sapphire" />
            <label htmlFor="isDefault" className="text-sm text-slate-700 dark:text-slate-300">{t('Default value')}</label>
          </div>
          {editingValue && <FormField label={t('Status')}><select aria-label={t('Status')} value={valueForm.isActive ? 'true' : 'false'} onChange={(e) => setValueForm(x => ({ ...x, isActive: e.target.value === 'true' }))} className="select w-full"><option value="true">{t('Active')}</option><option value="false">{t('Inactive')}</option></select></FormField>}
        </div>
      </Modal>
      {confirmDialog}
    </div>
  );
}

// ─── Numbering Rules ──────────────────────────────────────────────────────────

function NumberingRulesTab() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<NumberingRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<NumberingRule | null>(null);
  useSetupEditorState(modalOpen);
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
    if (!form.entityType.trim() || !form.prefix.trim()) { setError(t('Entity type and prefix are required')); return; }
    setSaving(true); setError('');
    try { await numberingRulesApi.upsert(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string | boolean | number) => setForm(x => ({ ...x, [key]: v }));
  const deleteRule = async (id: string) => {
    if (!(await confirm({ title: t('Delete numbering rule'), message: t('Delete this numbering rule?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
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
      <TableShell columns={[msg('Entity Type'), msg('Prefix'), msg('Preview'), msg('Padding'), msg('Reset Yearly'), '']}
        onAdd={openNew} addLabel={msg('New Rule')} loading={loading} empty={items.length === 0} emptyLabel={msg('No rules defined')}>
        {items.map((r) => (
          <tr key={r.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{r.entityType}</td>
            <td className="px-4 py-3 font-mono text-xs text-slate-500">{r.prefix}</td>
            <td className="px-4 py-3 font-mono text-xs text-sapphire">{preview(r)}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.paddingLength}</td>
            <td className="px-4 py-3">{r.resetYearly ? <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-xs font-semibold text-emerald-600">{t('Yes')}</span> : '—'}</td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(r)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteRule(r.id)} disabled={deleting === r.id} aria-label={t('Delete rule')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? `Edit Rule — ${editing.entityType}` : 'New Numbering Rule'} onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label={t('Entity Type')} required>
            <input value={form.entityType} onChange={(e) => f('entityType', e.target.value)} className="input w-full" placeholder="Employee" disabled={!!editing} />
          </FormField>
          <FormField label={t('Prefix')} required>
            <input value={form.prefix} onChange={(e) => f('prefix', e.target.value)} className="input w-full" placeholder="EMP" />
          </FormField>
          <FormField label={t('Suffix')}>
            <input value={form.suffix} onChange={(e) => f('suffix', e.target.value)} className="input w-full" />
          </FormField>
          <FormField label={t('Separator')}>
            <input value={form.separator} onChange={(e) => f('separator', e.target.value)} className="input w-full" placeholder="-" maxLength={3} />
          </FormField>
          <FormField label={t('Padding Length')}>
            <input type="number" value={form.paddingLength} onChange={(e) => f('paddingLength', Number(e.target.value))} className="input w-full" min={1} max={10} />
          </FormField>
          <div className="col-span-2 flex gap-6">
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.includeYear} onChange={(e) => f('includeYear', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Include Year')}</label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.includeMonth} onChange={(e) => f('includeMonth', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Include Month')}</label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={form.resetYearly} onChange={(e) => f('resetYearly', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Reset Yearly')}</label>
          </div>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── System Settings ──────────────────────────────────────────────────────────

function SystemSettingsTab() {
  const t = useT();
  const [items, setItems] = useState<SystemSetting[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterCat, setFilterCat] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<SystemSetting | null>(null);
  useSetupEditorState(modalOpen);
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
    if (!form.category.trim() || !form.settingKey.trim()) { setError(t('Category and key are required')); return; }
    setSaving(true); setError('');
    try { await systemSettingsApi.upsert(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };

  return (
    <>
      <TableShell columns={[msg('Category'), msg('Key'), msg('Value'), msg('Type'), '']} onAdd={openNew} addLabel={msg('New Setting')}
        loading={loading} empty={items.length === 0} emptyLabel={msg('No settings')}
        filter={
          <select value={filterCat} onChange={(e) => setFilterCat(e.target.value)} className="select">
            <option value="">{t('All Categories')}</option>
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
              {!s.isReadOnly && <button type="button" onClick={() => openEdit(s)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>}
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? t('Edit Setting') : t('New Setting')} onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Category')} required><input value={form.category} onChange={(e) => setForm(x => ({ ...x, category: e.target.value }))} className="input w-full" placeholder="HR" /></FormField>
          <FormField label={t('Key')} required><input value={form.settingKey} onChange={(e) => setForm(x => ({ ...x, settingKey: e.target.value }))} className="input w-full" placeholder="PROBATION_DAYS" /></FormField>
          <FormField label={t('Value')}><input value={form.settingValue} onChange={(e) => setForm(x => ({ ...x, settingValue: e.target.value }))} className="input w-full" /></FormField>
          <FormField label={t('Data Type')}>
            <select value={form.dataType} onChange={(e) => setForm(x => ({ ...x, dataType: e.target.value }))} className="select w-full">
              {['string', 'number', 'boolean', 'json'].map((t) => <option key={t}>{t}</option>)}
            </select>
          </FormField>
          <FormField label={t('Description')}><input value={form.description} onChange={(e) => setForm(x => ({ ...x, description: e.target.value }))} className="input w-full" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── GCC Settings ────────────────────────────────────────────────────────────

/** `countryCode` (the company setup wizard) shows only that country's rules and starts a new one on it. */
export function GCCSettingsTab({ countryCode, onSaved }: { countryCode?: string; onSaved?: (change: 'added' | 'updated') => void } = {}) {
  const t = useT();
  const [items, setItems] = useState<GCCComplianceSetting[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GCCComplianceSetting | null>(null);
  useSetupEditorState(modalOpen);
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
    setForm({ countryCode: countryCode ?? 'US', wpsEnabled: false, wpsAgentId: '', wpsMolCode: '', sifEnabled: false, eosbEnabled: true, eosbYears1To5Rate: 0.5, eosbYearsAbove5Rate: 1.0, eosbMinYears: 1, workWeek: 'Mon-Fri', weekendDays: 'Sat,Sun', visaTrackingEnabled: true, visaAlertDays: 30, iqamaRequired: false, iqamaAlertDays: 30, emiratesIdRequired: false, ramadanHoursEnabled: false, ramadanReducedHoursPerDay: 2 });
    setError(''); setModalOpen(true);
  };
  const openEdit = (s: GCCComplianceSetting) => {
    setEditing(s);
    setForm({ ...s });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    setSaving(true); setError('');
    try { await gccSettingsApi.upsert(form as GCCComplianceSetting & { countryCode: string }); setModalOpen(false); onSaved?.(editing ? 'updated' : 'added'); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string | boolean | number) => setForm(x => ({ ...x, [key]: v }));
  // Rows were saved as ISO-2 or ISO-3 over time (SA / SAU); compare both as ISO-2.
  const shown = countryCode ? items.filter(s => setupCountryCode(s.countryCode) === setupCountryCode(countryCode)) : items;

  return (
    <>
      <div className="space-y-4">
        {!(countryCode && (loading || shown.length > 0)) && <div className="flex justify-end">
          <button type="button" onClick={openNew} className={countryCode ? 'btn-secondary' : 'btn-primary'}><Plus className="h-4 w-4" /> {t('Add Country Config')}</button>
        </div>}
        {loading ? (countryCode ? <div aria-busy="true" className="h-[148px] rounded-xl bg-slate-200/50 dark:bg-white/[0.05]" /> : <div className="flex justify-center py-12"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>)
          : shown.length === 0 ? <div className="surface py-12 text-center text-sm text-slate-500 dark:text-slate-400">{t('No country rules yet')}</div>
          : (
            <div className={countryCode ? 'grid gap-4' : 'grid gap-4 sm:grid-cols-2'}>
              {shown.map((s) => (
                <div key={s.id} className="surface p-4 space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-bold text-slate-900 dark:text-white text-lg">{s.countryCode}</span>
                    <button type="button" onClick={() => openEdit(s)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                  </div>
                  <div className="grid grid-cols-2 gap-1 text-xs">
                    <span className="text-slate-400">EOSB</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.eosbEnabled ? `${s.eosbYears1To5Rate}× / ${s.eosbYearsAbove5Rate}×` : 'Disabled'}</span>
                    <span className="text-slate-400">WPS</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.wpsEnabled ? s.wpsAgentId || 'Enabled' : 'Disabled'}</span>
                    <span className="text-slate-400">{t('Work Week')}</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.workWeek}</span>
                    <span className="text-slate-400">{t('Weekend')}</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.weekendDays}</span>
                    <span className="text-slate-400">{t('Visa Alert')}</span><span className="font-medium text-slate-700 dark:text-slate-300">{s.visaTrackingEnabled ? `${s.visaAlertDays} days` : 'Off'}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
      </div>

      <Modal isOpen={modalOpen} title={editing ? `GCC Settings — ${editing.countryCode}` : 'New Country Config'} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3 text-sm">
          <FormField label={t('Country')} required><CountrySelect value={form.countryCode} onChange={(cc) => f('countryCode', cc)} ariaLabel="Country" disabled={!!editing} /></FormField>
          <FormField label={t('Work Week')}><input value={form.workWeek ?? ''} onChange={(e) => f('workWeek', e.target.value)} className="input w-full" placeholder="Mon-Fri" /></FormField>
          <FormField label={t('Weekend Days')}><input value={form.weekendDays ?? ''} onChange={(e) => f('weekendDays', e.target.value)} className="input w-full" placeholder="Fri,Sat" /></FormField>
          <div />

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">EOSB</div>
          <label className="col-span-2 flex items-center gap-2"><input type="checkbox" checked={form.eosbEnabled ?? false} onChange={(e) => f('eosbEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Enable EOSB')}</label>
          <FormField label={t('Years 1-5 Rate')}><input type="number" step="0.1" value={form.eosbYears1To5Rate ?? 0.5} onChange={(e) => f('eosbYears1To5Rate', Number(e.target.value))} className="input w-full" /></FormField>
          <FormField label={t('Years 5+ Rate')}><input type="number" step="0.1" value={form.eosbYearsAbove5Rate ?? 1} onChange={(e) => f('eosbYearsAbove5Rate', Number(e.target.value))} className="input w-full" /></FormField>
          <FormField label={t('Min Years')}><input type="number" value={form.eosbMinYears ?? 1} onChange={(e) => f('eosbMinYears', Number(e.target.value))} className="input w-full" /></FormField>

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">{t('WPS / SIF')}</div>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.wpsEnabled ?? false} onChange={(e) => f('wpsEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Enable WPS')}</label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.sifEnabled ?? false} onChange={(e) => f('sifEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Enable SIF')}</label>
          <FormField label={t('WPS Agent ID')}><input value={form.wpsAgentId ?? ''} onChange={(e) => f('wpsAgentId', e.target.value)} className="input w-full" /></FormField>
          <FormField label={t('MOL Code')}><input value={form.wpsMolCode ?? ''} onChange={(e) => f('wpsMolCode', e.target.value)} className="input w-full" /></FormField>

          <div className="col-span-2 border-t border-slate-100 dark:border-white/10 pt-2 text-xs font-bold uppercase tracking-wide text-slate-400">{t('Documents & Compliance')}</div>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.visaTrackingEnabled ?? false} onChange={(e) => f('visaTrackingEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Visa Tracking')}</label>
          <FormField label={t('Visa Alert Days')}><input type="number" value={form.visaAlertDays ?? 30} onChange={(e) => f('visaAlertDays', Number(e.target.value))} className="input w-full" /></FormField>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.iqamaRequired ?? false} onChange={(e) => f('iqamaRequired', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Iqama Required')}</label>
          <FormField label={t('Iqama Alert Days')}><input type="number" value={form.iqamaAlertDays ?? 30} onChange={(e) => f('iqamaAlertDays', Number(e.target.value))} className="input w-full" /></FormField>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.emiratesIdRequired ?? false} onChange={(e) => f('emiratesIdRequired', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Emirates ID Required')}</label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.ramadanHoursEnabled ?? false} onChange={(e) => f('ramadanHoursEnabled', e.target.checked)} className="h-4 w-4 accent-sapphire" /> {t('Ramadan Hours')}</label>
          <FormField label={t('Ramadan Reduced Hrs/Day')}><input type="number" value={form.ramadanReducedHoursPerDay ?? 2} onChange={(e) => f('ramadanReducedHoursPerDay', Number(e.target.value))} className="input w-full" /></FormField>
        </div>
      </Modal>
    </>
  );
}

// ─── Fiscal Years ─────────────────────────────────────────────────────────────

function FiscalYearsTab() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<FiscalYear[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [form, setForm] = useState({ year: new Date().getFullYear(), startDate: '', endDate: '' });
  useSetupEditorState(modalOpen);
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
    if (!form.startDate || !form.endDate) { setError(t('Start and end dates are required')); return; }
    setSaving(true); setError('');
    try { await fiscalYearsApi.create(form); setModalOpen(false); load(); }
    catch (err: unknown) { setError((err as any)?.response?.data?.message ?? 'Failed to create fiscal year.'); }
    finally { setSaving(false); }
  };
  const closeFY = async (id: string) => {
    if (!(await confirm({ title: t('Close fiscal year'), message: t('Close this fiscal year? This cannot be undone.'), confirmLabel: t('Close year'), tone: 'danger' }))) return;
    setClosing(id);
    try { await fiscalYearsApi.close(id); load(); } catch { /**/ }
    finally { setClosing(null); }
  };
  const deleteFY = async (id: string) => {
    if (!(await confirm({ title: t('Delete fiscal year'), message: t('Delete this fiscal year?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await fiscalYearsApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  const statusColor = (s: string) => s === 'Open' ? 'bg-emerald-500/10 text-emerald-600' : s === 'Closed' ? 'bg-slate-100 text-slate-400 dark:bg-white/10 dark:text-slate-500' : 'bg-amber-500/10 text-amber-600';

  return (
    <>
      <TableShell columns={[msg('Year'), msg('Start'), msg('End'), msg('Status'), msg('Current'), '']} onAdd={() => { setForm({ year: new Date().getFullYear(), startDate: '', endDate: '' }); setError(''); setModalOpen(true); }}
        addLabel={msg('New Fiscal Year')} loading={loading} empty={items.length === 0} emptyLabel={msg('No fiscal years')}>
        {items.map((fy) => (
          <tr key={fy.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-semibold text-slate-900 dark:text-white">{fy.year}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fy.startDate}</td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fy.endDate}</td>
            <td className="px-4 py-3"><span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusColor(fy.status)}`}>{fy.status}</span></td>
            <td className="px-4 py-3">{fy.isCurrent ? <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">{t('Current')}</span> : '—'}</td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                {fy.status === 'Open' && (
                  <button type="button" onClick={() => closeFY(fy.id)} disabled={closing === fy.id} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                    {closing === fy.id ? '…' : t('Close year')}
                  </button>
                )}
                {!fy.isCurrent && (
                  <button type="button" onClick={() => deleteFY(fy.id)} disabled={deleting === fy.id} aria-label={t('Delete fiscal year')} className={DELETE_ROW_BUTTON}>
                    <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                  </button>
                )}
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={t('New Fiscal Year')} onClose={() => setModalOpen(false)}
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Create'}</button></>}>
        <FormError error={error} />
        <div className="space-y-3">
          <FormField label={t('Year')} required><input type="number" value={form.year} onChange={(e) => setForm(x => ({ ...x, year: Number(e.target.value) }))} className="input w-full" /></FormField>
          <FormField label={t('Start Date')} required><input type="date" value={form.startDate} onChange={(e) => setForm(x => ({ ...x, startDate: e.target.value }))} className="input w-full" /></FormField>
          <FormField label={t('End Date')} required><input type="date" value={form.endDate} onChange={(e) => setForm(x => ({ ...x, endDate: e.target.value }))} className="input w-full" /></FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Locations ────────────────────────────────────────────────────────────────

function LocationsTab() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<AdminLocation[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<AdminLocation | null>(null);
  useSetupEditorState(modalOpen);
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
    if (!form.code.trim() || !form.nameEn.trim()) { setError(t('Code and name are required')); return; }
    setSaving(true); setError('');
    const body = { ...form, latitude: form.latitude ? Number(form.latitude) : undefined, longitude: form.longitude ? Number(form.longitude) : undefined, geofenceRadiusMeters: form.geofenceRadiusMeters ? Number(form.geofenceRadiusMeters) : undefined };
    try {
      if (editing) await locationsApi.update(editing.id, body);
      else await locationsApi.create(body);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };
  const f = (key: string, v: string) => setForm(x => ({ ...x, [key]: v }));
  const deleteLocation = async (id: string) => {
    if (!(await confirm({ title: t('Delete location'), message: t('Delete this location?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await locationsApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell columns={[msg('Code'), msg('Name'), msg('City'), msg('Country'), msg('Geofence'), msg('Status'), '']} onAdd={openNew} addLabel={msg('Add Location')}
        loading={loading} empty={items.length === 0} emptyLabel={msg('No locations yet')} emptyHint={msg('Add your first location, or import a CSV.')}
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
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(l)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteLocation(l.id)} disabled={deleting === l.id} aria-label={t('Delete location')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? t('Edit Location') : t('Add Location')} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label={t('Code')} required><input value={form.code} onChange={(e) => f('code', e.target.value)} className="input w-full" placeholder="DXB-HQ" /></FormField>
          <FormField label={t('Name (EN)')} required><input value={form.nameEn} onChange={(e) => f('nameEn', e.target.value)} className="input w-full" /></FormField>
          <FormField label={t('Name (AR)')}><input value={form.nameAr} onChange={(e) => f('nameAr', e.target.value)} className="input w-full" dir="rtl" /></FormField>
          <FormField label={t('Country')} required><CountrySelect value={form.countryCode} onChange={(cc) => f('countryCode', cc)} ariaLabel="Location country" /></FormField>
          <FormField label={t('City')}><input value={form.city} onChange={(e) => f('city', e.target.value)} className="input w-full" /></FormField>
          <FormField label={t('Postal Code')}><input value={form.postalCode} onChange={(e) => f('postalCode', e.target.value)} className="input w-full" /></FormField>
          <FormField label={t('Address')}><input value={form.addressLine1} onChange={(e) => f('addressLine1', e.target.value)} className="input w-full" /></FormField>
          <div />
          <FormField label={t('Latitude')}><input type="number" step="any" value={form.latitude} onChange={(e) => f('latitude', e.target.value)} className="input w-full" placeholder="25.2048" /></FormField>
          <FormField label={t('Longitude')}><input type="number" step="any" value={form.longitude} onChange={(e) => f('longitude', e.target.value)} className="input w-full" placeholder="55.2708" /></FormField>
          <FormField label={t('Geofence Radius (m)')}><input type="number" value={form.geofenceRadiusMeters} onChange={(e) => f('geofenceRadiusMeters', e.target.value)} className="input w-full" placeholder="100" /></FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Notification Templates ───────────────────────────────────────────────────

function NotificationTemplatesTab() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [items, setItems] = useState<NotificationTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [filterChannel, setFilterChannel] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<NotificationTemplate | null>(null);
  useSetupEditorState(modalOpen);
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
  const openEdit = (tpl: NotificationTemplate) => {
    setEditing(tpl);
    setForm({ code: tpl.code, eventType: tpl.eventType, channel: tpl.channel, subjectEn: tpl.subjectEn, subjectAr: tpl.subjectAr, bodyEn: tpl.bodyEn, bodyAr: tpl.bodyAr, variables: tpl.variables });
    setError(''); setModalOpen(true);
  };
  const save = async () => {
    if (!form.code.trim() || !form.eventType.trim() || !form.bodyEn.trim()) { setError(t('Code, event type and body (EN) are required')); return; }
    setSaving(true); setError('');
    try {
      if (editing) await notificationTemplatesApi.update(editing.id, form);
      else await notificationTemplatesApi.create(form);
      setModalOpen(false); load();
    } catch (err: unknown) { setError((err as any)?.response?.data?.message ?? t('Failed to save.')); }
    finally { setSaving(false); }
  };
  const deleteTemplate = async (id: string) => {
    if (!(await confirm({ title: t('Delete notification template'), message: t('Delete this notification template?'), confirmLabel: t('Delete'), tone: 'danger' }))) return;
    setDeleting(id);
    try { await notificationTemplatesApi.delete(id); load(); } catch (e) { notifyApiError(e); }
    finally { setDeleting(null); }
  };

  return (
    <>
      <TableShell columns={[msg('Code'), msg('Event Type'), msg('Channel'), msg('Subject (EN)'), msg('Status'), '']} onAdd={openNew} addLabel={msg('New Template')}
        loading={loading} empty={items.length === 0} emptyLabel={msg('No templates')}
        filter={
          <select value={filterChannel} onChange={(e) => setFilterChannel(e.target.value)} className="select">
            <option value="">{t('All Channels')}</option>
            {['Email', 'SMS', 'InApp', 'WhatsApp'].map((c) => <option key={c}>{c}</option>)}
          </select>
        }>
        {items.map((tpl) => (
          <tr key={tpl.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
            <td className="px-4 py-3 font-mono text-xs text-slate-500">{tpl.code}</td>
            <td className="px-4 py-3 text-slate-700 dark:text-slate-300">{tpl.eventType}</td>
            <td className="px-4 py-3"><span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-xs font-semibold text-sapphire">{tpl.channel}</span></td>
            <td className="px-4 py-3 text-slate-600 dark:text-slate-300 max-w-xs truncate">{tpl.subjectEn || '—'}</td>
            <td className="px-4 py-3"><ActiveBadge active={tpl.isActive} /></td>
            <td className="px-4 py-3">
              <div className="flex items-center gap-1">
                <button type="button" onClick={() => openEdit(tpl)} className="btn-secondary h-7 px-2 text-xs"><Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}</button>
                <button type="button" onClick={() => deleteTemplate(tpl.id)} disabled={deleting === tpl.id} aria-label={t('Delete template')} className={DELETE_ROW_BUTTON}>
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Delete')}
                </button>
              </div>
            </td>
          </tr>
        ))}
      </TableShell>
      <Modal isOpen={modalOpen} title={editing ? t('Edit Template') : t('New Template')} onClose={() => setModalOpen(false)} size="lg"
        footer={<><button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">{t('Cancel')}</button><button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button></>}>
        <FormError error={error} />
        <div className="grid grid-cols-2 gap-3">
          <FormField label={t('Code')} required><input value={form.code} onChange={(e) => setForm(x => ({ ...x, code: e.target.value }))} className="input w-full" placeholder="LEAVE_APPROVED" /></FormField>
          <FormField label={t('Event Type')} required><input value={form.eventType} onChange={(e) => setForm(x => ({ ...x, eventType: e.target.value }))} className="input w-full" placeholder="LeaveApproved" /></FormField>
          <FormField label={t('Channel')} required>
            <select value={form.channel} onChange={(e) => setForm(x => ({ ...x, channel: e.target.value }))} className="select w-full">
              {['Email', 'SMS', 'InApp', 'WhatsApp'].map((c) => <option key={c}>{c}</option>)}
            </select>
          </FormField>
          <FormField label={t('Variables')}><input value={form.variables} onChange={(e) => setForm(x => ({ ...x, variables: e.target.value }))} className="input w-full" placeholder="{{employeeName}},{{leaveType}}" /></FormField>
          <FormField label={t('Subject (EN)')}><input value={form.subjectEn} onChange={(e) => setForm(x => ({ ...x, subjectEn: e.target.value }))} className="input w-full" /></FormField>
          <FormField label={t('Subject (AR)')}><input value={form.subjectAr} onChange={(e) => setForm(x => ({ ...x, subjectAr: e.target.value }))} className="input w-full" dir="rtl" /></FormField>
          <FormField label={t('Body (EN)')} required><textarea value={form.bodyEn} onChange={(e) => setForm(x => ({ ...x, bodyEn: e.target.value }))} className="input w-full min-h-28" /></FormField>
          <FormField label={t('Body (AR)')}><textarea value={form.bodyAr} onChange={(e) => setForm(x => ({ ...x, bodyAr: e.target.value }))} className="input w-full min-h-28" dir="rtl" /></FormField>
        </div>
      </Modal>
      {confirmDialog}
    </>
  );
}

// ─── Admin Audit Logs ─────────────────────────────────────────────────────────

function AdminAuditLogsTab() {
  const t = useT();
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
                <th key={h} className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {loading ? (
              <tr><td colSpan={5} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={5} className="py-12 text-center text-sm text-slate-500 dark:text-slate-400">{t('No audit log entries yet')}</td></tr>
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
  const [showSmtpPassword, setShowSmtpPassword] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [saved, setSaved] = useState(false);
  const [savedConfig, setSavedConfig] = useState(() => JSON.stringify(cfg));
  useSetupEditorState(JSON.stringify(cfg) !== savedConfig);

  useEffect(() => {
    systemSettingsApi.list('Email').then((settings) => {
      const merged = { ...cfg };
      for (const s of settings) {
        if (KEYS.includes(s.settingKey as SmtpKey)) merged[s.settingKey as SmtpKey] = s.settingValue;
      }
      setCfg(merged);
      setSavedConfig(JSON.stringify(merged));
    }).catch(() => {}).finally(() => setLoading(false));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async () => {
    setSaving(true); setSaved(false);
    try {
      for (const key of KEYS) {
        await systemSettingsApi.upsert({ category: 'Email', settingKey: key, settingValue: cfg[key], dataType: 'string', description: key });
      }
      setSavedConfig(JSON.stringify(cfg));
      setSaved(true); setTimeout(() => setSaved(false), 3000);
    } catch { /**/ } finally { setSaving(false); }
  };

  const testConnection = async () => {
    setTesting(true); setTestResult(null);
    try {
      // Persist all fields first so the backend tests against the latest values…
      for (const key of KEYS) {
        await systemSettingsApi.upsert({ category: 'Email', settingKey: key, settingValue: cfg[key], dataType: 'string', description: key });
      }
      // …then send a real test email through the configured SMTP server.
      setSavedConfig(JSON.stringify(cfg));
      const result = await systemSettingsApi.testSmtp();
      setTestResult(result);
    } catch { setTestResult({ ok: false, message: 'Failed to save settings or reach the server.' }); } finally { setTesting(false); }
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
            {/* Reveal toggle. An SMTP password is pasted from a mail provider's console and is
                wrong far more often than it is read over the operator's shoulder — and a wrong
                one fails as a 20-second timeout that names no cause. Seeing what was pasted is
                what makes that diagnosable. */}
            <div className="relative">
              <input
                type={showSmtpPassword ? 'text' : 'password'}
                className="input w-full pe-10"
                placeholder="••••••••"
                autoComplete="off"
                spellCheck={false}
                value={cfg['Smtp.Password']}
                onChange={e => setCfg(c => ({ ...c, 'Smtp.Password': e.target.value }))}
              />
              <button
                type="button"
                onClick={() => setShowSmtpPassword(v => !v)}
                aria-label={showSmtpPassword ? 'Hide the SMTP password' : 'Show the SMTP password'}
                aria-pressed={showSmtpPassword}
                title={showSmtpPassword ? 'Hide password' : 'Show password'}
                className="absolute inset-y-0 end-0 flex items-center px-3 text-slate-400 transition-colors hover:text-slate-600 dark:hover:text-slate-200"
              >
                {showSmtpPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
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
        <button type="button" className="btn-secondary" onClick={testConnection} disabled={testing}>{testing ? 'Sending test…' : 'Save & Send Test Email'}</button>
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

/** Row actions are always visible and always say what they do (never an icon alone, never hover-only). */
const DELETE_ROW_BUTTON = 'inline-flex h-7 items-center gap-1 rounded-md border border-slate-200 px-2 text-xs font-medium text-slate-600 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-600 disabled:opacity-40 dark:border-white/10 dark:text-slate-300 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400';

function TableShell({
  columns, onAdd, addLabel, loading, empty, emptyLabel, emptyHint, filter, actions, children,
}: {
  columns: string[];
  // Optional so read-only surfaces (e.g. organization.read viewers on the Companies
  // tab) can render the list without an inert Add button. All write-capable callers
  // still pass onAdd, so their behaviour is unchanged.
  onAdd?: () => void;
  addLabel?: string;
  loading: boolean;
  empty: boolean;
  emptyLabel: string;
  /** A second line for the empty table, naming the first step (e.g. "Add your first branch, or import a CSV."). Shown to writers only. */
  emptyHint?: string;
  filter?: React.ReactNode;
  actions?: React.ReactNode;
  children: React.ReactNode;
}) {
  // Callers pass msg('…') keys; translated here so every Setup table reads in the user's language.
  const t = useT();
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-2">{filter}</div>
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          {actions}
          {onAdd && (
            <button type="button" onClick={onAdd} className="btn-primary">
              <Plus className="h-4 w-4" aria-hidden="true" />
              {addLabel ? t(addLabel) : null}
            </button>
          )}
        </div>
      </div>
      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {columns.map((col, index) => (
                  <th key={col || index} className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                    {col ? t(col) : <span className="sr-only">{t('Actions')}</span>}
                  </th>
                ))}
                <th className="w-20 px-4 py-3"><span className="sr-only">{t('Actions')}</span></th>
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
                  <td colSpan={columns.length + 1} className="px-4 py-12 text-center text-sm text-slate-500 dark:text-slate-400">
                    <p className="font-medium text-slate-600 dark:text-slate-300">{t(emptyLabel)}</p>
                    {emptyHint && onAdd && <p className="mt-1">{t(emptyHint)}</p>}
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
  const t = useT();
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-semibold ${active ? 'bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20' : 'bg-slate-100 text-slate-400 dark:bg-white/10 dark:text-slate-500'}`}>
      <span className={`h-1.5 w-1.5 rounded-full ${active ? 'bg-emeraldZ' : 'bg-slate-400'}`} />
      {active ? t('Active') : t('Inactive')}
    </span>
  );
}

function FormField({ label, required, children, info, error, errorId }: {
  label: string; required?: boolean; children: React.ReactNode; info?: string;
  /** Shown under the control. Give the control aria-invalid and aria-describedby={errorId}. */
  error?: string; errorId?: string;
}) {
  return (
    <div>
      <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label} {required && <span className="text-red-500">*</span>}
        {info && <InfoTip text={info} className="ms-1" />}
      </label>
      {children}
      {error && (
        <p id={errorId} role="alert" className="mt-1 text-xs font-medium text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
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
  qiwaEstablishmentId: '', defaultCurrency: '',
  emailDomain: '', workEmailPattern: DEFAULT_WORK_EMAIL_PATTERN,
  isActive: true,
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

// currency '' = the owning company's default currency (Addendum B), filled in by the API.
const emptyGrade = (): GradeRequest => ({
  code: '', name: '', nameAr: '', band: '', level: 0,
  minSalary: 0, midSalary: 0, maxSalary: 0, currency: '', isActive: true,
});

const emptyCostCenter = (companyId?: string): CostCenterRequest => ({
  companyId, code: '', name: '', nameAr: '', isActive: true,
});

// ─── SetupPage ───────────────────────────────────────────────────────────────

/** A revalidation shorter than this freezes the page silently; a longer one explains the freeze. */
export const AUTHORIZATION_BANNER_DELAY_MS = 800;

/** Set once Setup has mounted in this document: later visits arrive by in-app navigation, not by a document load. */
let setupDocumentLandingSeen = false;

/**
 * The address the browser's own Back/Forward last restored inside this document (popstate). In-app routes are
 * client-side, so leaving Setup for another screen and pressing Back remounts Setup without a document load: the
 * document's navigation timing still says "navigate", and only this tells a history restore from a followed link.
 */
let lastTraversedHref: string | null = null;
if (typeof window !== 'undefined') window.addEventListener('popstate', () => { lastTraversedHref = window.location.href; });

/**
 * How this Setup visit arrived: 'back_forward' when the browser's history restored it (inside this document, or as
 * the document's own load), 'reload' for a reloaded Setup document, otherwise (and on the server) 'navigate'.
 */
function setupArrivalNavigation(): string {
  if (typeof window === 'undefined') return 'navigate';
  if (lastTraversedHref === window.location.href) return 'back_forward';
  if (setupDocumentLandingSeen) return 'navigate';
  try {
    const entry = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
    if (!entry || new URL(entry.name).pathname !== '/setup') return 'navigate';
    return entry.type;
  } catch {
    return 'navigate';
  }
}

export function SetupPage() {
  const router = useRouter();
  const t = useT();
  // Keep the existing tab URLs, including staffing-budget department/level links.
  const searchParams = useSearchParams();
  const { hasPermission, hasRole, authorizationRefreshStatus, retryAuthorization } = useAuth();
  const { accountType, isGroupScope, companyVersion, companies: authorizedCompanies, selectedCompanyId, setSelectedCompany } = useCompany();
  const flags = useFeatureFlags();
  const { locale } = useLocale();
  const isCompanyScopedGroup = accountType === 'Group' && !isGroupScope;
  // Readers entering from /companies must only see the Companies list. Hiding a
  // mode also prevents its component from mounting or requesting restricted data.
  // The policy library exists only while the tenant has policy intake switched on (off by the owner's decision).
  const policyIntake = usePolicyIntake();
  // One model decides who sees which section: the navigation, "Find a setting" and the ⌘K palette all read it —
  // including the readers' rules (Companies only when the server lists them for the role, GL & rates for any GL or
  // rates permission), so Payroll Manager and Finance get their section everywhere, not only in the nav.
  const navigationModel = setupNavigationModel({ hasPermission, hasRole, isCompanyScopedGroup, policyIntake, isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath, flagStatus: flags.status });
  const { canManagePolicies, canWrite, canReadCompanies, canReadStatutoryRules, canReadGlAndRates } = navigationModel;
  const visibleTabs = tabs.filter(tab => navigationModel.settings.some(section => section.id === tab.id));
  const tabParam = searchParams?.get('tab');
  const fallbackTab: Tab = canWrite ? 'aiSetup' : canReadStatutoryRules ? 'statutoryRules' : canReadGlAndRates ? 'glMapping' : 'companies';
  const requestedTab: Tab = canWrite && (tabParam === 'aiSetup' || (!isCompanyScopedGroup && tabParam === 'importOrganization') || (tabParam === 'policyLibrary' && canManagePolicies))
    ? tabParam
    : visibleTabs.some((tab) => tab.id === tabParam)
      ? (tabParam as SettingsTab)
      : fallbackTab;
  const [activeTab, setActiveTab] = useState<Tab>(requestedTab);
  const focusDepartmentId = searchParams?.get('department') ?? undefined;
  const focusLevelId = searchParams?.get('level') ?? undefined;
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);
  const [editorDirty, setEditorDirty] = useState(false);
  const [pendingStarterFiles, setPendingStarterFiles] = useState(false);
  const clearDirty = useCallback(() => setEditorDirty(false), []);
  // "Discard and leave" covers the selected-but-unuploaded files as well. Leaving the
  // pending-file flag set kept the guard armed after the user had confirmed, so the
  // company sync that follows a confirmed Back/Forward asked the same question again.
  const discardSetupWork = useCallback(() => {
    setEditorDirty(false);
    setPendingStarterFiles(false);
  }, []);
  // A Guided setup deep link is the address this Setup visit arrived with, read once here, before the company scope is
  // reconciled into the URL. Every later return to Guided setup (navigation, search, Back) keeps `companyId` only as
  // the scope and opens the chooser.
  const landingCompanyLink = useRef<string | null | undefined>(undefined);
  if (landingCompanyLink.current === undefined) landingCompanyLink.current = setupLandingCompanyLink(new URLSearchParams(searchParams?.toString()), setupArrivalNavigation());
  // This arrival has been read: a later visit is classified afresh (a followed link after a Back is a link again).
  useEffect(() => { setupDocumentLandingSeen = true; lastTraversedHref = null; }, []);
  const authorizedCompany = useSetupCompanyQuery();
  const confirmLeave = useSetupNavigationGuard(editorDirty, discardSetupWork, pendingStarterFiles, authorizedCompany);
  const setupPath = resolveSetupPath(new URLSearchParams(searchParams?.toString()), {
    authorizedCompanyIds: new Set(authorizedCompanies.map(company => company.id)),
    selectedCompanyId: authorizedCompany,
  });
  const configurationScopeBlocked = setupPath.path === 'configuration' && setupPath.notice === 'unauthorized-company';
  const configurationScopePending = setupPath.path === 'configuration' && setupPath.companyId !== null && setupPath.companyId !== authorizedCompany;
  const configurationScopeUnavailable = configurationScopeBlocked || configurationScopePending;

  // Also follow links and browser navigation after the page has already mounted.
  useEffect(() => {
    setActiveTab(requestedTab);
  }, [requestedTab]);

  useEffect(() => {
    if (configurationScopeUnavailable) {
      setCompanies([]);
      setGrades([]);
      setCostCenters([]);
      return;
    }
    let active = true;
    let companiesRequest = 0;
    setCompanies([]);
    setGrades([]);
    setCostCenters([]);
    const refreshCompanies = () => {
      if (!canReadCompanies) return;
      const request = ++companiesRequest;
      companiesApi.listAll()
        .then(rows => { if (active && request === companiesRequest) setCompanies(rows); })
        .catch(() => { if (active && request === companiesRequest) setCompanies([]); });
    };
    refreshCompanies();
    window.addEventListener('kynexone:companies-changed', refreshCompanies);
    if (canWrite) {
      gradesApi.listAll().then(rows => { if (active) setGrades(rows); }).catch(() => { if (active) setGrades([]); });
      costCentersApi.listAll().then(rows => { if (active) setCostCenters(rows); }).catch(() => { if (active) setCostCenters([]); });
    }
    return () => { active = false; window.removeEventListener('kynexone:companies-changed', refreshCompanies); };
  }, [canReadCompanies, canWrite, companyVersion, configurationScopeUnavailable]);

  const tabHref = (tab: Tab) => {
    const params = new URLSearchParams(searchParams?.toString());
    params.set('tab', tab);
    // Area and setting belong to All setup areas. A settings section's address does not carry them: an area left in
    // the URL would reopen All setup areas on re-entry even after the user chose Guided setup (the path is kept).
    if (tab === 'aiSetup') params.set('area', resolveSetupArea(params.get('area')));
    else { params.delete('area'); params.delete('setting'); }
    // Payroll accounting's tab request belongs to the link that made it, never to the next section's address.
    params.delete('glTab');
    if (authorizedCompany) params.set('companyId', authorizedCompany); else params.delete('companyId');
    return `/setup?${params.toString()}`;
  };
  const selectTab = (tab: Tab) => {
    landingCompanyLink.current = null;
    if (tab === activeTab) return;
    if (!confirmLeave()) return;
    setActiveTab(tab);
    // The tab is shown immediately, so its history entry must exist immediately too.
    // router.push only writes the entry when its server round trip commits; a second
    // tab chosen before then superseded it, and Back skipped a tab the user had seen.
    // The page is client-rendered from the query, so a native push (which Next's
    // router adopts without a fetch) is enough.
    window.history.pushState(null, '', tabHref(tab));
  };

  // Resolve permissions on every render so a permission change cannot leave a
  // formerly available settings panel visible until the synchronization effect.
  const selectedTab = canWrite
    ? (activeTab === 'policyLibrary' && !canManagePolicies ? 'aiSetup' : activeTab)
    : visibleTabs.some((tab) => tab.id === activeTab) ? activeTab : fallbackTab;
  const isSettings = selectedTab !== 'aiSetup' && selectedTab !== 'importOrganization' && selectedTab !== 'policyLibrary';
  const isWorkspace = selectedTab === 'aiSetup' && setupPath.path === 'configuration' && !configurationScopeUnavailable;
  // Guided setup is the company setup chooser (create, or complete an existing company — Addendum A). The old
  // studio stays reachable only as the "Policy starter", for what has no screen of its own (A7).
  const isGuided = canWrite && selectedTab === 'aiSetup' && setupPath.path === 'guided';
  const isStarter = canWrite && selectedTab === 'aiSetup' && setupPath.path === 'policyStarter';
  const starterScope = authorizedCompany ?? 'group';
  const setupPathHref = (path: SetupStudioPath) => {
    const next = serializeSetupPath({ ...setupPath, path, needsReplace: false }, new URLSearchParams(searchParams?.toString()));
    return `/setup?${next.toString()}`;
  };
  const recoverConfigurationScope = (companyId: string | null) => {
    setSelectedCompany(companyId);
    const next = serializeSetupPath({
      ...setupPath,
      companyId,
      notice: undefined,
      needsReplace: false,
    }, new URLSearchParams(searchParams?.toString()));
    router.replace(`/setup?${next.toString()}`, { scroll: false });
  };
  const selectSetupPath = (path: SetupStudioPath) => {
    if (path === setupPath.path) return;
    const href = setupPathHref(path);
    confirmLeave(href, () => {
      setActiveTab('aiSetup');
      router.push(href, { scroll: false });
    });
  };
  const returnToWorkspace = () => {
    if (pendingStarterFiles) {
      selectSetupPath('configuration');
      return;
    }
    // AiSetupAssistant invokes this callback only after saveForLater succeeds.
    // The persisted draft no longer needs the dirty-value blocker.
    clearDirty();
    setActiveTab('aiSetup');
    router.push(setupPathHref('configuration'), { scroll: false });
  };
  const selectedSetting = tabs.find((tab) => tab.id === selectedTab) ?? tabs[0];
  // Guided setup, All setup areas and the Policy starter share tab=aiSetup and differ by path. Guided setup and All
  // setup areas open at their start (no area or setting), which is also where a plain click on the current entry
  // returns to. The Policy starter keeps the area on screen, so its "Return to workspace" comes back to it.
  const studioHref = (entry: keyof typeof studioPaths) => {
    const area = entry === 'policyStarter' ? setupPath.area : 'overview';
    const next = serializeSetupPath({ ...setupPath, path: studioPaths[entry], area, setting: null, notice: undefined, needsReplace: false }, new URLSearchParams(searchParams?.toString()));
    return `/setup?${next.toString()}`;
  };
  const selectedNavId: SetupNavId = selectedTab === 'aiSetup'
    ? (setupPath.path === 'guided' ? 'guidedSetup' : setupPath.path === 'policyStarter' ? 'policyStarter' : 'allSetupAreas')
    : selectedTab;
  const selectNavigation = (id: SetupNavId) => {
    // An in-page move: whatever the arrival named is no longer a deep link.
    landingCompanyLink.current = null;
    if (id !== 'guidedSetup' && id !== 'allSetupAreas' && id !== 'policyStarter') {
      selectTab(id);
      return;
    }
    if (id === selectedNavId && (id === 'policyStarter' || (setupPath.area === 'overview' && !setupPath.setting))) return;
    const href = studioHref(id);
    // The tab follows the address (requestedTab), so no in-between section is shown or announced on the way.
    confirmLeave(href, () => { router.push(href, { scroll: false }); });
  };
  // Guided setup's own progress (its chooser's "N of M done"), refreshed on a company change, each time Guided setup
  // or All setup areas is opened, and on every move inside All setup areas (an area, an embedded setting and back), the
  // same moves that re-read its checklist, so a record saved in an embedded setting counts in both at once.
  const progressRefresh = `${companyVersion}:${selectedNavId === 'guidedSetup' ? 'guided'
    : selectedNavId === 'allSetupAreas' ? `areas:${setupPath.area}:${setupPath.setting ?? ''}` : 'section'}`;
  const guidedCompanies = useGuidedSetupCompanies(canWrite, progressRefresh);
  const guidedCompany = progressCompany(guidedCompanies.companies, authorizedCompany);
  const guidedProgress = guidedCompany ? readinessProgress(guidedCompany) : null;
  const guidedCompanyName = guidedCompany ? ((locale === 'ar' && guidedCompany.legalNameAr?.trim()) || guidedCompany.legalNameEn) : '';
  const toolHref = (id: Exclude<SetupNavId, SettingsTab>) => (id === 'importOrganization' || id === 'policyLibrary' ? tabHref(id) : studioHref(id));
  const navItem = (id: Exclude<SetupNavId, SettingsTab>): SetupSectionNavItem<SetupNavId> => ({
    id,
    label: t(toolSections[id].label),
    icon: toolSections[id].icon,
    href: toolHref(id),
    // A quiet count while the company's checklist is incomplete; nothing once it is done (or when there is none).
    status: id === 'guidedSetup' && guidedProgress && guidedProgress.total > 0 && !guidedProgress.complete ? {
      text: t('{done} of {total}', { done: guidedProgress.done, total: guidedProgress.total }),
      description: t('{company}: {done} of {total} setup items done', { company: guidedCompanyName, done: guidedProgress.done, total: guidedProgress.total }),
    } : undefined,
  });
  // Get started: the setup paths and tools. The Policy starter is for setup writers, like the rest of this group.
  const setupTools: SetupSectionNavItem<SetupNavId>[] = navigationModel.tools.map(navItem);
  const navigationSettings = visibleTabs;
  const navigationGroups: SetupSectionNavGroup<SetupNavId>[] = SETUP_SECTION_GROUPS.map(group => ({
    id: group.id,
    label: t(group.label),
    items: group.id === 'getStarted'
      ? setupTools
      : navigationSettings.filter(tab => tab.group === group.id).map(tab => ({ id: tab.id, label: t(tab.label), icon: tab.icon, href: tabHref(tab.id) })),
  })).filter(group => group.items.length > 0);
  // "Find a setting": the navigation's own sections, All setup areas' destinations and Payroll accounting's tabs, from
  // the same model as the navigation (and the ⌘K palette), so nothing is found that this user cannot open.
  const areaPageHref = (areaId: string, setting?: string) => {
    const next = new URLSearchParams({ tab: 'aiSetup', path: 'configuration', area: areaId });
    if (setting) next.set('setting', setting);
    if (authorizedCompany) next.set('companyId', authorizedCompany);
    return `/setup?${next.toString()}`;
  };
  const searchEntries = buildSetupSearchIndex({
    model: navigationModel,
    access: { hasPermission, hasRole, isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath, flagStatus: flags.status, isCompanyScopedGroup },
    sectionHref: id => (id in toolSections ? toolHref(id as Exclude<SetupNavId, SettingsTab>) : tabHref(id as SettingsTab)),
    areaHref: areaPageHref,
    routeHref: destination => {
      const [pathname, query] = destination.path.split('?');
      const target = new URLSearchParams(query);
      if (authorizedCompany) target.set('companyId', authorizedCompany);
      target.set('setupArea', destination.areaId);
      return `${pathname}?${target.toString()}`;
    },
  });
  const openSearchResult = (entry: SetupSearchEntry) => {
    if (entry.navId) { selectNavigation(entry.navId as SetupNavId); return; }
    confirmLeave(entry.href, () => {
      setEditorDirty(false);
      router.push(entry.href, { scroll: false });
    });
  };
  const currentSection = isSettings ? { label: selectedSetting.label, icon: selectedSetting.icon } : toolSections[selectedNavId as Exclude<SetupNavId, SettingsTab>];
  const currentSectionLabel = t(currentSection.label);
  const [sectionListOpen, setSectionListOpen] = useState(false);
  const sectionToggle = useRef<HTMLButtonElement>(null);
  const showNavigation = !configurationScopeUnavailable;
  const sectionDescription = isSettings
    ? t(selectedSetting.description)
    : selectedNavId === 'allSetupAreas' && setupPath.area === 'overview' && !setupPath.setting
      ? t('Choose an area to review settings, templates and related operations. You can start anywhere.')
      : undefined;

  const renderExistingPanel = (id: SettingsTab): React.ReactNode => {
    if (!visibleTabs.some(tab => tab.id === id)) return null;
    switch (id) {
      case 'establishment': return <EstablishmentPanel focusDepartmentId={focusDepartmentId} focusLevelId={focusLevelId} />;
      case 'companies': return <CompaniesTab />;
      case 'branches': return <BranchesTab companies={companies} />;
      case 'departments': return <DepartmentsTab costCenters={costCenters} />;
      case 'designations': return <DesignationsTab grades={grades} />;
      case 'grades': return <GradesTab />;
      case 'costCenters': return <CostCentersTab companies={companies} />;
      case 'masterData': return <MasterDataTab />;
      case 'numberingRules': return <NumberingRulesTab />;
      case 'systemSettings': return <SystemSettingsTab />;
      case 'gccSettings': return <GCCSettingsTab />;
      case 'statutoryRules': return <StatutoryRulesPanel />;
      case 'fiscalYears': return <FiscalYearsTab />;
      case 'locations': return <LocationsTab />;
      case 'glMapping': return <div onChangeCapture={event => {
        // Incumbent GL subeditors have no save-state callback yet. Protect their
        // authored forms/rows conservatively, excluding the surrounding filters.
        if (!(event.target as Element).closest('[data-setup-filter]')) setEditorDirty(true);
      }}><GlSetupPanel companies={glCompanies(companies, authorizedCompanies, selectedCompanyId)} costCenters={costCenters} confirmLeave={confirmLeave} requestedTab={searchParams?.get('glTab')} /></div>;
      case 'notificationTemplates': return <NotificationTemplatesTab />;
      case 'emailConfig': return <EmailConfigTab />;
      case 'adminAuditLogs': return <AdminAuditLogsTab />;
    }
  };
  const renderExistingSettings = (id: SettingsTab) => <SetupEditorState.Provider value={setEditorDirty}>{renderExistingPanel(id)}</SetupEditorState.Provider>;
  // The page is frozen (inert) for the whole check: a deliberate safety stop while access is re-read. The banner that
  // explains it waits: /me usually answers well under a second on every focus change, and a banner flashing each
  // time reads as an error. A slow check shows it after AUTHORIZATION_BANNER_DELAY_MS; a failure shows at once.
  const authorizationBlocked = authorizationRefreshStatus !== 'idle';
  const [slowAuthorizationCheck, setSlowAuthorizationCheck] = useState(false);
  useEffect(() => {
    if (authorizationRefreshStatus !== 'checking') { setSlowAuthorizationCheck(false); return; }
    const timer = window.setTimeout(() => setSlowAuthorizationCheck(true), AUTHORIZATION_BANNER_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [authorizationRefreshStatus]);
  const authorizationBannerShown = authorizationRefreshStatus === 'unavailable' || (authorizationRefreshStatus === 'checking' && slowAuthorizationCheck);
  const authorizationStatusRef = useRef<HTMLElement | null>(null);
  const authorizationRetryRef = useRef<HTMLButtonElement | null>(null);
  const authorizationRestoreFocusRef = useRef<HTMLElement | null>(null);
  const previousAuthorizationStatusRef = useRef(authorizationRefreshStatus);

  useEffect(() => {
    const previous = previousAuthorizationStatusRef.current;
    previousAuthorizationStatusRef.current = authorizationRefreshStatus;
    if (authorizationRefreshStatus !== 'idle') {
      if (previous === 'idle') {
        const active = document.activeElement;
        authorizationRestoreFocusRef.current = active instanceof HTMLElement ? active : null;
      }
      // Focus moves to the banner only once it is shown (a quick check never shows it).
      if (!authorizationBannerShown) return;
      const frame = window.requestAnimationFrame(() => {
        (authorizationRefreshStatus === 'unavailable' ? authorizationRetryRef.current : authorizationStatusRef.current)?.focus();
      });
      return () => window.cancelAnimationFrame(frame);
    }
    if (previous === 'idle') return;
    const restore = authorizationRestoreFocusRef.current;
    authorizationRestoreFocusRef.current = null;
    const frame = window.requestAnimationFrame(() => {
      if (restore?.isConnected && !restore.closest('[inert]')) restore.focus();
    });
    return () => window.cancelAnimationFrame(frame);
  }, [authorizationRefreshStatus, authorizationBannerShown]);

  // A reader whose role opens none of the sections is told so, not shown a section heading over an empty page.
  if (!canWrite && visibleTabs.length === 0) {
    return (
      <div className="setup-page space-y-4">
        <header className="setup-page-header min-w-0">
          <h1 className="text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{t('Setup & Administration')}</h1>
        </header>
        <p role="status" className="surface p-6 text-sm text-slate-600 dark:text-slate-300" data-testid="setup-no-sections">
          {t('Your role has no Setup sections to open. Ask an administrator if you need one.')}
        </p>
      </div>
    );
  }

  return (
    <>
      {authorizationBannerShown && typeof document !== 'undefined' && createPortal(<section
        data-testid="authorization-revalidation-status"
        role="status"
        aria-live="polite"
        tabIndex={-1}
        ref={authorizationStatusRef}
        // Portalled to <body>: <main> animates in with a transform, which made it the containing block of this
        // fixed banner and slid it under the sticky top bar (60px, TopBar.tsx). Placed just below that bar.
        className="fixed inset-x-4 top-[72px] z-[70] mx-auto max-w-2xl rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 shadow-lg dark:border-amber-500/40 dark:bg-amber-950 dark:text-amber-100"
      >
        <p className="font-semibold">
          {authorizationRefreshStatus === 'checking'
            ? t('Revalidating your access…')
            : t('Authorization could not be revalidated.')}
        </p>
        <p className="mt-1">
          {authorizationRefreshStatus === 'checking'
            ? t('Changes and navigation are temporarily paused.')
            : t('Your unsaved changes are preserved. Retry before continuing.')}
        </p>
        {authorizationRefreshStatus === 'unavailable' && <button ref={authorizationRetryRef} type="button" className="btn-secondary mt-3" onClick={() => void retryAuthorization()}>{t('Retry authorization')}</button>}
      </section>, document.body)}
      <div
        data-testid="setup-authorization-boundary"
        inert={authorizationBlocked ? true : undefined}
        aria-busy={authorizationRefreshStatus === 'checking'}
        aria-disabled={authorizationBlocked || undefined}
        onClickCapture={event => {
          if (!authorizationBlocked) return;
          event.preventDefault();
          event.stopPropagation();
        }}
        onSubmitCapture={event => {
          if (!authorizationBlocked) return;
          event.preventDefault();
          event.stopPropagation();
        }}
      >
      <div className="setup-page space-y-4">
      <header className="setup-page-header min-w-0">
        <h1 className="text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{t('Setup & Administration')}</h1>
        <p className="mt-1 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          {canWrite
            ? t('Company structure, jobs and grades, payroll and country rules, and workspace settings.')
            : t('Review the setup areas available to your role.')}
        </p>
      </header>

      {/* One navigation for the page: the grouped section column beside the selected section from 1280px. Below that
          (and for the Policy starter, which brings its own step rail) the list folds away under the section header's
          "All sections" button, so the header comes first, then the list when it is open, then the section. */}
      <div className={showNavigation && !isStarter
        ? 'setup-page-layout grid min-w-0 xl:grid-cols-[15rem_minmax(0,1fr)] xl:grid-rows-[auto_1fr] xl:gap-x-6'
        : 'setup-page-layout grid min-w-0'}>
      {showNavigation && (
        <div className={`min-w-0 ${isStarter ? '' : 'xl:col-start-1 xl:row-span-2 xl:row-start-1'}`}>
          <SetupSectionNav
            groups={navigationGroups}
            selectedId={selectedNavId}
            currentLabel={currentSectionLabel}
            collapsed={isStarter}
            open={sectionListOpen}
            onOpenChange={setSectionListOpen}
            toggleRef={sectionToggle}
            onSelect={selectNavigation}
            locationKey={searchParams?.toString() ?? ''}
            search={{
              entries: searchEntries,
              onOpen: openSearchResult,
              allAreas: canWrite ? { href: studioHref('allSetupAreas'), onOpen: () => selectNavigation('allSetupAreas') } : undefined,
            }}
          />
        </div>
      )}
      <div className={`order-first min-w-0 ${showNavigation && !isStarter ? 'xl:order-none xl:col-start-2 xl:row-start-1' : ''}`}>
        <SetupSectionHeader
          icon={currentSection.icon}
          title={currentSectionLabel}
          description={sectionDescription}
          divider={isSettings}
          action={showNavigation ? <SetupSectionsToggle open={sectionListOpen} onToggle={() => setSectionListOpen(value => !value)} collapsed={isStarter} toggleRef={sectionToggle} /> : undefined}
        />
      </div>
      <div className={`setup-page-content mt-4 min-w-0 space-y-4 ${showNavigation && !isStarter ? 'xl:col-start-2 xl:row-start-2' : ''}`}>

      {/* Each path owns its mounted subtree; browser drafts plus the guard preserve authored work. */}
      {canWrite && (
        <>
          <section id="setup-aiSetup" aria-label={selectedTab === 'aiSetup' ? currentSectionLabel : t('Guided setup')} hidden={selectedTab !== 'aiSetup'}>
            {configurationScopeBlocked && <section role="alert" className="rounded-xl border border-amber-200 bg-amber-50 p-6 dark:border-amber-500/30 dark:bg-amber-500/10">
              <h2 className="text-lg font-semibold text-amber-950 dark:text-amber-100">{t('Company access required')}</h2>
              <p className="mt-2 max-w-2xl text-sm leading-6 text-amber-800 dark:text-amber-200">{t('This link names a company you cannot access. All setup areas has not opened and no broader company scope was substituted.')}</p>
              <div className="mt-4 flex flex-wrap gap-2">
                {authorizedCompanies.map(company => <button key={company.id} type="button" className="btn-secondary" onClick={() => recoverConfigurationScope(company.id)}>{t('Use {company}', { company: company.name })}</button>)}
                {isGroupScope && <button type="button" className="btn-secondary" onClick={() => recoverConfigurationScope(null)}>{t('Use all authorized companies')}</button>}
              </div>
            </section>}
            {configurationScopePending && !configurationScopeBlocked && <p role="status" className="rounded-xl border border-slate-200 bg-white p-5 text-sm text-slate-600 dark:border-white/10 dark:bg-[#0d1225] dark:text-slate-300">{t('Opening the selected company scope…')}</p>}
            {isGuided && <GuidedSetupChooser showHeading={false} linkedCompanyId={landingCompanyLink.current} onLinkedCompanyUsed={() => { landingCompanyLink.current = null; }} />}
            {isStarter && <div className="flex flex-col">
              {/* One compact line. On a phone it follows the starter, so the starter's first screen keeps its
                  own controls (setup-experience: policy entry in the first viewport). */}
              <p className="order-last mt-3 flex flex-wrap sm:order-first sm:mb-2 sm:mt-0 items-center gap-x-2 gap-y-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400" data-testid="policy-starter-banner">
                <span className="font-semibold text-slate-900 dark:text-white">{t('Attendance, overtime, benefits and other policies.')}</span>
                <span>{t('For a company’s structure, use Guided setup.')}</span>
                <button type="button" className="font-semibold text-sapphire underline-offset-2 hover:underline dark:text-cyanAccent" onClick={() => selectSetupPath('configuration')}>{t('Back to All setup areas')}</button>
              </p>
              <div className="min-w-0" onChangeCapture={event => {
              setEditorDirty(true);
              const target = event.target;
              if (target instanceof HTMLInputElement && target.type === 'file') setPendingStarterFiles(Boolean(target.files?.length));
            }}><SetupPendingFilesState.Provider value={setPendingStarterFiles}><AiSetupAssistant key={starterScope} companies={companies} onOpenLibrary={() => selectTab('policyLibrary')} onReturnToWorkspace={returnToWorkspace} /></SetupPendingFilesState.Provider></div>
            </div>}
            {isWorkspace && <SetupStudioWorkspace
              companies={companies}
              guidedCompanies={guidedCompanies}
              confirmLeave={confirmLeave}
              onDirtyChange={setEditorDirty}
              renderSettings={(id, onDirtyChange) => {
                if (isSettingsTab(id)) return renderExistingSettings(id);
                // Catalogue checks route, feature, scope and editor access before calling this.
                // Retain the mutation role intersection here as a second composition boundary.
                if (isCompanyScopedGroup || !(hasRole('Admin') || hasRole('HR Manager'))) return null;
                if (id === 'onboarding' && hasPermission('recruitment.write') && (hasPermission('recruitment.read') || hasPermission('recruitment.write'))) return <OnboardingPolicyWorkspace companies={companies} onDirtyChange={onDirtyChange} />;
                if (!(hasPermission('leave.read') || hasPermission('leave.write'))) return null;
                if (id === 'leaveTypes') return <LeaveTypesTab onDirtyChange={onDirtyChange} />;
                if (id === 'leavePolicies') return <PoliciesTab onDirtyChange={onDirtyChange} />;
                if (id === 'holidayCalendars') return <HolidayCalendarTab onDirtyChange={onDirtyChange} />;
                return null;
              }}
            />}
          </section>
          {!isCompanyScopedGroup && selectedTab === 'importOrganization' && <section id="setup-importOrganization" aria-label={t('Import organization')}>
            <OrgStructureImportPanel />
          </section>}
        </>
      )}

      {canManagePolicies && selectedTab === 'policyLibrary' && <section id="setup-policyLibrary" aria-label={t('Policy library')}><PolicyDocumentManager showHeading={false} onOpenSetup={canWrite ? () => selectTab('aiSetup') : undefined} /></section>}

      {isSettings && (
        <section id="setup-setting-content" aria-label={t(selectedSetting.label)} className="min-w-0 space-y-4">
          <div className="min-w-0" key={companyVersion}>
            {isSettingsTab(selectedTab) && renderExistingSettings(selectedTab)}
          </div>
        </section>
      )}
      </div>
      </div>
      </div>
      </div>
    </>
  );
}
