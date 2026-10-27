'use client';

import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useState } from 'react';
import { Plus } from 'lucide-react';
import {
  branchesApi, companiesApi, departmentsApi, designationsApi, gradesApi,
  type BranchDto, type CompanyDto, type DepartmentDto, type DesignationDto, type GradeDto,
} from '@/src/api/organization';
import { LINK_SECTIONS, onboardingTemplatesApi, type ChecklistSummary, type HireTemplate, type HireTemplateInput } from '@/src/api/onboardingTemplates';
import { useLocale } from '@/src/contexts/LocaleContext';
import { onboardingProblemKey } from '@/src/lib/onboardingProblem';
import { ConfirmDialog, type ConfirmState } from '@/src/components/ui/ConfirmDialog';
import { LoadFailedNotice } from '@/src/components/ui/LoadFailedRow';
import { StatusChip } from '@/src/components/StatusChip';
import { SettingsDrawer } from '@/src/components/companySetup/rules/SettingsDrawer';

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';

interface Form {
  id?: string; name: string; nameAr: string; designationId: string; gradeId: string; branchId: string; checklistId: string; sections: string[];
  departmentId: string; employmentType: string; contractType: string; probationDays: string; workLocation: string; managerRule: 'none' | 'department-head'; isActive: boolean;
}
const blank = (): Form => ({ name: '', nameAr: '', designationId: '', gradeId: '', branchId: '', checklistId: '', sections: [], departmentId: '', employmentType: '', contractType: '', probationDays: '', workLocation: '', managerRule: 'none', isActive: true });
const fromTemplate = (h: HireTemplate): Form => ({
  id: h.id, name: h.name, nameAr: h.nameAr ?? '', designationId: h.matchDesignationId ?? '', gradeId: h.matchGradeId ?? '', branchId: h.matchBranchId ?? '', checklistId: h.checklistId ?? '',
  sections: h.essCompletionSections, departmentId: h.defaults.departmentId ?? '', employmentType: h.defaults.employmentType ?? '', contractType: h.defaults.contractType ?? '',
  probationDays: h.defaults.probationDays != null ? String(h.defaults.probationDays) : '', workLocation: h.defaults.workLocation ?? '', managerRule: h.defaults.managerRule ?? 'none', isActive: h.isActive,
});
const toInput = (f: Form, companyId: string): HireTemplateInput => ({
  name: f.name.trim(), nameAr: f.nameAr.trim() || null, companyId, matchDesignationId: f.designationId || null, matchGradeId: f.gradeId || null, matchBranchId: f.branchId || null,
  checklistId: f.checklistId || null, essCompletionSections: f.sections, isActive: f.isActive,
  defaults: {
    ...(f.departmentId ? { departmentId: f.departmentId } : {}), ...(f.employmentType.trim() ? { employmentType: f.employmentType.trim() } : {}),
    ...(f.contractType.trim() ? { contractType: f.contractType.trim() } : {}), ...(f.probationDays.trim() ? { probationDays: Number(f.probationDays) } : {}),
    ...(f.workLocation.trim() ? { workLocation: f.workLocation.trim() } : {}), ...(f.managerRule !== 'none' ? { managerRule: f.managerRule } : {}),
  },
});

/** Setup > Hire templates: per company presets the Add Employee wizard applies. A template holds no pay: the grade stays the pay source. */
export function HireTemplatesPanel({ companyId: scopeCompany, companies: given, onDirtyChange }: {
  companyId?: string | null; companies?: CompanyDto[]; onDirtyChange?: (dirty: boolean) => void;
}) {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const uid = useId();
  const [companies, setCompanies] = useState<CompanyDto[]>(given ?? []);
  const [companyId, setCompanyId] = useState(scopeCompany ?? '');
  const [rows, setRows] = useState<HireTemplate[]>([]);
  const [showRemoved, setShowRemoved] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [lookups, setLookups] = useState<{ designations: DesignationDto[]; grades: GradeDto[]; branches: BranchDto[]; departments: DepartmentDto[]; checklists: ChecklistSummary[] }>({ designations: [], grades: [], branches: [], departments: [], checklists: [] });
  const [form, setForm] = useState<Form | null>(null);
  const [dirty, setDirty] = useState(false);
  const [tried, setTried] = useState(false);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [copyTarget, setCopyTarget] = useState<Record<string, string>>({});

  useLayoutEffect(() => { onDirtyChange?.(form !== null && dirty); return () => onDirtyChange?.(false); }, [form, dirty, onDirtyChange]);
  useEffect(() => {
    if (given && given.length > 0) { setCompanies(given); return; }
    let live = true;
    companiesApi.listAll().then(list => { if (live) setCompanies(list); }).catch(() => undefined);
    return () => { live = false; };
  }, [given]);
  const active = useMemo(() => companies.filter(c => c.isActive), [companies]);
  useEffect(() => { if (!companyId && active.length > 0) setCompanyId(scopeCompany && active.some(c => c.id === scopeCompany) ? scopeCompany : active[0].id); }, [active, companyId, scopeCompany]);

  const load = useCallback(async () => {
    if (!companyId) { setLoading(false); return; }
    setLoading(true);
    try { setRows(await onboardingTemplatesApi.hire.list(companyId, showRemoved)); setLoadError(null); }
    catch (e) { setRows([]); setLoadError(e); }
    finally { setLoading(false); }
  }, [companyId, showRemoved]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (!companyId) return;
    let live = true;
    void Promise.allSettled([designationsApi.listAll(), gradesApi.listAll(), branchesApi.listAll(), departmentsApi.listAll(), onboardingTemplatesApi.checklists.list(companyId)]).then(([d, g, b, dep, c]) => {
      if (!live) return;
      const mine = <T extends { companyId?: string | null }>(x: T[]) => x.filter(r => !r.companyId || r.companyId === companyId);
      setLookups({
        designations: d.status === 'fulfilled' ? mine(d.value).filter(x => x.isActive) : [], grades: g.status === 'fulfilled' ? mine(g.value).filter(x => x.isActive) : [],
        branches: b.status === 'fulfilled' ? b.value.filter(x => x.isActive && x.companyId === companyId) : [], departments: dep.status === 'fulfilled' ? mine(dep.value).filter(x => x.isActive) : [],
        checklists: c.status === 'fulfilled' ? c.value.filter(x => x.isActive && !x.isDeleted) : [],
      });
    });
    return () => { live = false; };
  }, [companyId]);

  const fail = (e: unknown) => setNotice({ kind: 'error', text: t(onboardingProblemKey(e)) });
  const change = (patch: Partial<Form>) => { setForm(f => (f ? { ...f, ...patch } : f)); setDirty(true); };
  const companyName = (c: CompanyDto) => (ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn);
  const nameBad = !!form && !form.name.trim();
  const probationBad = !!form && form.probationDays.trim() !== '' && !(Number.isInteger(Number(form.probationDays)) && Number(form.probationDays) >= 0 && Number(form.probationDays) <= 180);

  const save = async () => {
    if (!form) return;
    setTried(true);
    if (nameBad || probationBad) return;
    setSaving(true); setNotice(null);
    try {
      if (form.id) await onboardingTemplatesApi.hire.update(form.id, toInput(form, companyId)); else await onboardingTemplatesApi.hire.create(toInput(form, companyId));
      setForm(null); setDirty(false); setTried(false); setNotice({ kind: 'ok', text: t('Saved. New hires can pick this template in the Add Employee wizard.') }); await load();
    } catch (e) { fail(e); } finally { setSaving(false); }
  };
  const remove = (h: HireTemplate) => setConfirm({
    title: t('Remove this hire template?'), message: t('Hires already created from it are not changed. If it was used, it is turned off instead of deleted, and you can restore it.'),
    confirmLabel: t('Remove'), closeOnConfirm: false,
    onConfirm: async () => {
      try {
        const r = await onboardingTemplatesApi.hire.remove(h.id);
        setNotice({ kind: 'ok', text: r.outcome === 'deleted' ? t('Removed.') : t('Turned off, not deleted: {count, plural, one {# item already uses it} other {# items already use it}}. You can restore it.', { count: Number(r.used) }) });
      } catch (e) { fail(e); }
      setConfirm(null); await load();
    },
  });
  const restore = async (h: HireTemplate) => { try { await onboardingTemplatesApi.hire.restore(h.id); setNotice({ kind: 'ok', text: t('Restored.') }); await load(); } catch (e) { fail(e); } };
  const copy = async (h: HireTemplate) => {
    const target = copyTarget[h.id];
    if (!target) return;
    try {
      const r = await onboardingTemplatesApi.hire.copyTo(h.id, target);
      setNotice({ kind: 'ok', text: r.dropped.length > 0 ? t('Copied. {count, plural, one {# setting has} other {# settings have}} no match in the other company and were left empty. Check the copy.', { count: Number(r.dropped.length) }) : t('Copied to the other company. The two lists are independent from now on.') });
    } catch (e) { fail(e); }
  };

  const keyText = (h: HireTemplate) => {
    const parts: string[] = [];
    const d = lookups.designations.find(x => x.id === h.matchDesignationId); if (d) parts.push(ar && d.titleAr ? d.titleAr : d.titleEn);
    const g = lookups.grades.find(x => x.id === h.matchGradeId); if (g) parts.push(`${g.code}`);
    const b = lookups.branches.find(x => x.id === h.matchBranchId); if (b) parts.push(ar && b.nameAr ? b.nameAr : b.nameEn);
    return parts.length ? parts.join(' / ') : t('Chosen by hand');
  };

  return (
    <div className="space-y-4" data-testid="hire-templates">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <p className="max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t('A hire template pre-fills the Add Employee form for a kind of hire and says which onboarding checklist the hire gets. It never holds pay: the grade does.')}
        </p>
        <button type="button" className="btn-primary" disabled={!companyId} onClick={() => { setNotice(null); setTried(false); setForm(blank()); setDirty(false); }}><Plus className="h-4 w-4" aria-hidden="true" />{t('New hire template')}</button>
      </div>
      <div className="flex flex-wrap items-center gap-4">
        <div>
          <label htmlFor={`${uid}-co`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Company')}</label>
          <select id={`${uid}-co`} className={`${input} mt-1 min-w-56`} value={companyId} onChange={e => setCompanyId(e.target.value)}>{active.map(c => <option key={c.id} value={c.id}>{companyName(c)}</option>)}</select>
        </div>
        <label className="mt-5 inline-flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300"><input type="checkbox" checked={showRemoved} onChange={e => setShowRemoved(e.target.checked)} />{t('Show removed hire templates')}</label>
      </div>
      {notice && <div role={notice.kind === 'error' ? 'alert' : 'status'} className={`rounded-lg border px-4 py-3 text-sm ${notice.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>{notice.text}</div>}

      {loading ? <p role="status" className="text-sm text-slate-500">{t('Loading…')}</p>
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
        : rows.length === 0 ? <p className="surface p-6 text-sm text-slate-600 dark:text-slate-300">{t('No hire templates for this company yet.')}</p>
        : (
          <ul className="grid gap-4 md:grid-cols-2" data-testid="hire-template-list">
            {rows.map(h => {
              const off = h.isDeleted || !h.isActive;
              return (
                <li key={h.id} className={`surface p-4 ${off ? 'opacity-70' : ''}`} data-testid={`hire-template-${h.name}`}>
                  <div className="flex flex-wrap items-center gap-2">
                    <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{ar && h.nameAr ? h.nameAr : h.name}</h3>
                    {off && <StatusChip label={h.isDeleted ? t('Removed') : t('Turned off')} tone="slate" />}
                  </div>
                  <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Matches: {keys}', { keys: keyText(h) })}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{t('{count, plural, one {# task} other {# tasks}}', { count: Number(h.checklistTaskCount ?? 0) })}</p>
                  <div className="mt-3 flex flex-wrap items-end gap-2">
                    {!off && <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { setNotice(null); setTried(false); setForm(fromTemplate(h)); setDirty(false); }}>{t('Edit')}</button>}
                    {off ? <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { void restore(h); }}>{t('Restore')}</button> : <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => remove(h)}>{t('Remove')}</button>}
                    {!off && active.length > 1 && (
                      <span className="flex items-end gap-1">
                        <span><label htmlFor={`${uid}-cp${h.id}`} className="block text-xs text-slate-500">{t('Copy to')}</label>
                          <select id={`${uid}-cp${h.id}`} className={`${input} h-8 py-0 text-xs`} value={copyTarget[h.id] ?? ''} onChange={e => setCopyTarget(c => ({ ...c, [h.id]: e.target.value }))}>
                            <option value="">{t('Choose a company')}</option>{active.filter(c => c.id !== companyId).map(c => <option key={c.id} value={c.id}>{companyName(c)}</option>)}
                          </select></span>
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!copyTarget[h.id]} onClick={() => { void copy(h); }}>{t('Copy')}</button>
                      </span>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}

      <SettingsDrawer open={form !== null} title={form?.id ? t('Edit hire template') : t('New hire template')} onClose={() => setForm(null)} testId="hire-template-editor">
        {form && (
          <div className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <label htmlFor={`${uid}-n`} className="block text-sm font-medium">{t('Name')} <span className="text-xs font-normal text-slate-500">{t('(required)')}</span></label>
                <input id={`${uid}-n`} className={input} dir="ltr" value={form.name} aria-invalid={tried && nameBad ? true : undefined} aria-describedby={tried && nameBad ? `${uid}-n-err` : undefined} onChange={e => change({ name: e.target.value })} />
                {tried && nameBad && <p id={`${uid}-n-err`} className="mt-1 text-xs font-medium text-rose-700">{t('Enter a name.')}</p>}
              </div>
              <div><label htmlFor={`${uid}-na`} className="block text-sm font-medium">{t('Name in Arabic')}</label><input id={`${uid}-na`} className={input} dir="rtl" value={form.nameAr} onChange={e => change({ nameAr: e.target.value })} /></div>
            </div>
            <fieldset className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
              <legend className="px-1 text-sm font-semibold">{t('Suggest this template when the hire has')}</legend>
              <p className="text-xs text-slate-500">{t('Leave all three empty to pick this template by hand only. A designation outranks a grade, and a grade outranks a branch.')}</p>
              <div className="mt-2 grid gap-3 sm:grid-cols-3">
                <div><label htmlFor={`${uid}-md`} className="block text-xs font-medium">{t('Designation')}</label>
                  <select id={`${uid}-md`} className={input} value={form.designationId} onChange={e => change({ designationId: e.target.value })}><option value="">{t('Not chosen')}</option>{lookups.designations.map(d => <option key={d.id} value={d.id}>{ar && d.titleAr ? d.titleAr : d.titleEn}</option>)}</select></div>
                <div><label htmlFor={`${uid}-mg`} className="block text-xs font-medium">{t('Grade')}</label>
                  <select id={`${uid}-mg`} className={input} value={form.gradeId} onChange={e => change({ gradeId: e.target.value })}><option value="">{t('Not chosen')}</option>{lookups.grades.map(g => <option key={g.id} value={g.id}>{`${g.code} - ${g.name}`}</option>)}</select></div>
                <div><label htmlFor={`${uid}-mb`} className="block text-xs font-medium">{t('Branch')}</label>
                  <select id={`${uid}-mb`} className={input} value={form.branchId} onChange={e => change({ branchId: e.target.value })}><option value="">{t('Not chosen')}</option>{lookups.branches.map(b => <option key={b.id} value={b.id}>{ar && b.nameAr ? b.nameAr : b.nameEn}</option>)}</select></div>
              </div>
            </fieldset>
            <fieldset className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
              <legend className="px-1 text-sm font-semibold">{t('Fill these fields')}</legend>
              <div className="grid gap-3 sm:grid-cols-2">
                <div><label htmlFor={`${uid}-dp`} className="block text-xs font-medium">{t('Department')}</label>
                  <select id={`${uid}-dp`} className={input} value={form.departmentId} onChange={e => change({ departmentId: e.target.value })}><option value="">{t('Not chosen')}</option>{lookups.departments.map(d => <option key={d.id} value={d.id}>{ar && d.nameAr ? d.nameAr : d.nameEn}</option>)}</select></div>
                <div><label htmlFor={`${uid}-mr`} className="block text-xs font-medium">{t('Line manager')}</label>
                  <select id={`${uid}-mr`} className={input} value={form.managerRule} onChange={e => change({ managerRule: e.target.value as Form['managerRule'] })}><option value="none">{t('Not chosen')}</option><option value="department-head">{t('The head of the department')}</option></select></div>
                <div><label htmlFor={`${uid}-et`} className="block text-xs font-medium">{t('Employment type')}</label><input id={`${uid}-et`} className={input} value={form.employmentType} onChange={e => change({ employmentType: e.target.value })} /></div>
                <div><label htmlFor={`${uid}-ct`} className="block text-xs font-medium">{t('Contract type')}</label><input id={`${uid}-ct`} className={input} value={form.contractType} onChange={e => change({ contractType: e.target.value })} /></div>
                <div><label htmlFor={`${uid}-pd`} className="block text-xs font-medium">{t('Probation (days)')}</label>
                  <input id={`${uid}-pd`} type="number" inputMode="numeric" className={input} dir="ltr" value={form.probationDays} aria-invalid={tried && probationBad ? true : undefined} aria-describedby={tried && probationBad ? `${uid}-pd-err` : undefined} onChange={e => change({ probationDays: e.target.value })} />
                  {tried && probationBad && <p id={`${uid}-pd-err`} className="mt-1 text-xs font-medium text-rose-700">{t('Enter a number of days from 0 to 180.')}</p>}</div>
                <div><label htmlFor={`${uid}-wl`} className="block text-xs font-medium">{t('Work location')}</label><input id={`${uid}-wl`} className={input} value={form.workLocation} onChange={e => change({ workLocation: e.target.value })} /></div>
              </div>
            </fieldset>
            <div><label htmlFor={`${uid}-cl`} className="block text-sm font-medium">{t('Onboarding checklist')}</label>
              <select id={`${uid}-cl`} className={input} value={form.checklistId} onChange={e => change({ checklistId: e.target.value })}><option value="">{t('The company default')}</option>{lookups.checklists.map(c => <option key={c.id} value={c.id}>{ar && c.nameAr ? c.nameAr : c.name}</option>)}</select></div>
            <fieldset><legend className="text-sm font-medium">{t('Ask the new hire to complete these sections in Self-Service')}</legend>
              <div className="mt-1 grid gap-1 sm:grid-cols-2">
                {LINK_SECTIONS.map(s => (
                  <label key={s} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.sections.includes(s)} onChange={e => change({ sections: e.target.checked ? [...form.sections, s] : form.sections.filter(x => x !== s) })} />{t(`onboarding.section.${s}`)}</label>
                ))}
              </div></fieldset>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.isActive} onChange={e => change({ isActive: e.target.checked })} />{t('Available in the Add Employee wizard')}</label>
            <div className="flex justify-end gap-2">
              <button type="button" className="btn-secondary" onClick={() => setForm(null)}>{t('Cancel')}</button>
              {/* !bg-blue-700: .btn-primary is 4.49:1 outside .tenant-app-shell, where this portalled drawer sits (see FU-1 in 05-DECISIONS.md). */}
              <button type="button" className="btn-primary !bg-blue-700" disabled={saving} onClick={() => { void save(); }}>{t('Save hire template')}</button>
            </div>
          </div>
        )}
      </SettingsDrawer>
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}
