'use client';

import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useState } from 'react';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { companiesApi, type CompanyDto } from '@/src/api/organization';
import {
  NATIONALITY_CONDITIONS, OWNER_ROLES, LINK_SECTIONS, onboardingTemplatesApi,
  type ChecklistSummary, type ChecklistTask, type ChecklistTemplate, type NationalityCondition, type OwnerRole,
} from '@/src/api/onboardingTemplates';
import { useLocale } from '@/src/contexts/LocaleContext';
import { blankTask, moveTask, previewTasks, validateTasks, PREVIEW_CLASSES, type PreviewClass } from '@/src/lib/onboardingChecklist';
import { onboardingProblemKey } from '@/src/lib/onboardingProblem';
import { ConfirmDialog, type ConfirmState } from '@/src/components/ui/ConfirmDialog';
import { LoadFailedNotice } from '@/src/components/ui/LoadFailedRow';
import { StatusChip } from '@/src/components/StatusChip';
import { SettingsDrawer } from '@/src/components/companySetup/rules/SettingsDrawer';

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';

interface Draft { id?: string; name: string; nameAr: string; isDefault: boolean; tasks: ChecklistTask[] }

/** Setup > Onboarding checklist templates: per company, with owners and due dates relative to the joining date. */
export function OnboardingChecklistTemplatesPanel({ companyId: scopeCompany, companies: given, onDirtyChange }: {
  companyId?: string | null; companies?: CompanyDto[]; onDirtyChange?: (dirty: boolean) => void;
}) {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const [companies, setCompanies] = useState<CompanyDto[]>(given ?? []);
  const [companyId, setCompanyId] = useState(scopeCompany ?? '');
  const [rows, setRows] = useState<ChecklistSummary[]>([]);
  const [showRemoved, setShowRemoved] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [editing, setEditing] = useState<Draft | null>(null);
  const [dirty, setDirty] = useState(false);
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [copyTarget, setCopyTarget] = useState<Record<string, string>>({});
  const [starterBusy, setStarterBusy] = useState(false);

  useLayoutEffect(() => { onDirtyChange?.(editing !== null && dirty); return () => onDirtyChange?.(false); }, [editing, dirty, onDirtyChange]);
  useEffect(() => {
    if (given && given.length > 0) { setCompanies(given); return; }
    let live = true;
    companiesApi.listAll().then(list => { if (live) setCompanies(list); }).catch(() => undefined);
    return () => { live = false; };
  }, [given]);
  const active = useMemo(() => companies.filter(c => c.isActive), [companies]);
  useEffect(() => { if (!companyId && active.length > 0) setCompanyId(scopeCompany && active.some(c => c.id === scopeCompany) ? scopeCompany : active[0].id); }, [active, companyId, scopeCompany]);
  const companyName = useCallback((c: CompanyDto) => (ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn), [ar]);

  const load = useCallback(async () => {
    if (!companyId) { setLoading(false); return; }
    setLoading(true);
    try { setRows(await onboardingTemplatesApi.checklists.list(companyId, showRemoved)); setLoadError(null); }
    catch (e) { setRows([]); setLoadError(e); }
    finally { setLoading(false); }
  }, [companyId, showRemoved]);
  useEffect(() => { void load(); }, [load]);

  const fail = (e: unknown) => setNotice({ kind: 'error', text: t(onboardingProblemKey(e)) });
  const open = async (id?: string) => {
    setNotice(null);
    if (!id) { setEditing({ name: '', nameAr: '', isDefault: false, tasks: [blankTask()] }); setDirty(false); return; }
    try {
      const doc: ChecklistTemplate = await onboardingTemplatesApi.checklists.get(id);
      setEditing({ id, name: doc.name, nameAr: doc.nameAr ?? '', isDefault: doc.isDefault, tasks: doc.tasks }); setDirty(false);
    } catch (e) { fail(e); }
  };
  const addStarter = async () => {
    setStarterBusy(true); setNotice(null);
    try { const doc = await onboardingTemplatesApi.checklists.copyStarter('ksa', companyId); setNotice({ kind: 'ok', text: t('Added the Saudi starter checklist with {count, plural, one {# task} other {# tasks}}. Edit it to fit your company.', { count: Number(doc.tasks.length) }) }); await load(); }
    catch (e) { fail(e); } finally { setStarterBusy(false); }
  };
  const copy = async (row: ChecklistSummary) => {
    const target = copyTarget[row.id];
    if (!target) return;
    try { await onboardingTemplatesApi.checklists.copyTo(row.id, target); setNotice({ kind: 'ok', text: t('Copied to the other company. The two lists are independent from now on.') }); }
    catch (e) { fail(e); }
  };
  const remove = (row: ChecklistSummary) => setConfirm({
    title: t('Remove this checklist?'),
    message: t('Hires who already have its tasks keep them. If any hire used it, it is turned off instead of deleted, and you can restore it.'),
    confirmLabel: t('Remove'), closeOnConfirm: false,
    onConfirm: async () => {
      try {
        const r = await onboardingTemplatesApi.checklists.remove(row.id);
        setNotice({ kind: 'ok', text: r.outcome === 'deleted' ? t('Removed.') : t('Turned off, not deleted: {count, plural, one {# item already uses it} other {# items already use it}}. You can restore it.', { count: Number(r.used) }) });
        setConfirm(null); await load();
      } catch (e) { fail(e); setConfirm(null); }
    },
  });
  const restore = async (row: ChecklistSummary) => {
    try { await onboardingTemplatesApi.checklists.restore(row.id); setNotice({ kind: 'ok', text: t('Restored.') }); await load(); } catch (e) { fail(e); }
  };

  return (
    <div className="space-y-4" data-testid="onboarding-checklist-templates">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <p className="max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t('A checklist lists the tasks a new hire needs, who does each one, and when it is due relative to the joining date. Tasks never stop an activation unless you choose that for a task.')}
        </p>
        <div className="flex flex-wrap gap-2">
          <button type="button" className="btn-secondary" disabled={!companyId || starterBusy} onClick={() => { void addStarter(); }}>{t('Add the Saudi starter checklist')}</button>
          <button type="button" className="btn-primary" disabled={!companyId} onClick={() => { void open(); }}><Plus className="h-4 w-4" aria-hidden="true" />{t('New checklist')}</button>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-4">
        <div>
          <label htmlFor="ocl-company" className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Company')}</label>
          <select id="ocl-company" className={`${input} mt-1 min-w-56`} value={companyId} onChange={e => setCompanyId(e.target.value)}>
            {active.map(c => <option key={c.id} value={c.id}>{companyName(c)}</option>)}
          </select>
        </div>
        <label className="mt-5 inline-flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
          <input type="checkbox" checked={showRemoved} onChange={e => setShowRemoved(e.target.checked)} />{t('Show removed checklists')}
        </label>
      </div>

      {notice && (
        <div role={notice.kind === 'error' ? 'alert' : 'status'} className={`rounded-lg border px-4 py-3 text-sm ${notice.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>{notice.text}</div>
      )}

      {loading ? <p role="status" className="text-sm text-slate-500">{t('Loading…')}</p>
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
        : rows.length === 0 ? <p className="surface p-6 text-sm text-slate-600 dark:text-slate-300">{t('No checklists for this company yet. Start from the Saudi starter checklist or create your own.')}</p>
        : (
          <ul className="grid gap-4 md:grid-cols-2" data-testid="checklist-list">
            {rows.map(row => {
              const off = row.isDeleted || !row.isActive;
              return (
                <li key={row.id} className={`surface p-4 ${off ? 'opacity-70' : ''}`} data-testid={`checklist-${row.name}`}>
                  <div className="flex flex-wrap items-center gap-2">
                    <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{ar && row.nameAr ? row.nameAr : row.name}</h3>
                    {row.isDefault && <StatusChip label={t('Default for new hires')} tone="blue" />}
                    {off && <StatusChip label={row.isDeleted ? t('Removed') : t('Turned off')} tone="slate" />}
                  </div>
                  <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('{count, plural, one {# task} other {# tasks}}', { count: Number(row.taskCount) })}</p>
                  <div className="mt-3 flex flex-wrap items-end gap-2">
                    {!off && <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { void open(row.id); }}>{t('Edit')}</button>}
                    {off ? <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { void restore(row); }}>{t('Restore')}</button>
                      : <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => remove(row)}>{t('Remove')}</button>}
                    {!off && active.length > 1 && (
                      <span className="flex items-end gap-1">
                        <span>
                          <label htmlFor={`copy-${row.id}`} className="block text-xs text-slate-500">{t('Copy to')}</label>
                          <select id={`copy-${row.id}`} className={`${input} h-8 py-0 text-xs`} value={copyTarget[row.id] ?? ''} onChange={e => setCopyTarget(c => ({ ...c, [row.id]: e.target.value }))}>
                            <option value="">{t('Choose a company')}</option>
                            {active.filter(c => c.id !== companyId).map(c => <option key={c.id} value={c.id}>{companyName(c)}</option>)}
                          </select>
                        </span>
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!copyTarget[row.id]} onClick={() => { void copy(row); }}>{t('Copy')}</button>
                      </span>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}

      <ChecklistEditor draft={editing} companyId={companyId} onChange={d => { setEditing(d); setDirty(true); }} onClose={() => setEditing(null)}
        onSaved={async () => { setEditing(null); setDirty(false); setNotice({ kind: 'ok', text: t('Saved. Tasks already raised for hires are not changed.') }); await load(); }} onError={fail} />
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}

function ChecklistEditor({ draft, companyId, onChange, onClose, onSaved, onError }: {
  draft: Draft | null; companyId: string; onChange: (d: Draft) => void; onClose: () => void; onSaved: () => Promise<void>; onError: (e: unknown) => void;
}) {
  const { t } = useLocale();
  const uid = useId();
  const [saving, setSaving] = useState(false);
  const [tried, setTried] = useState(false);
  const [cls, setCls] = useState<PreviewClass>('saudi');
  const [start, setStart] = useState('');
  const [announce, setAnnounce] = useState('');
  useEffect(() => { setTried(false); setAnnounce(''); }, [draft?.id]);
  if (!draft) return <SettingsDrawer open={false} title="" onClose={onClose}>{null}</SettingsDrawer>;

  const issues = validateTasks(draft.tasks);
  const nameBad = !draft.name.trim();
  const setTask = (i: number, patch: Partial<ChecklistTask>) => onChange({ ...draft, tasks: draft.tasks.map((x, j) => (j === i ? { ...x, ...patch } : x)) });
  const move = (i: number, d: -1 | 1) => {
    onChange({ ...draft, tasks: moveTask(draft.tasks, i, d) });
    setAnnounce(d < 0 ? t('Moved task {n} up.', { n: String(i + 1) }) : t('Moved task {n} down.', { n: String(i + 1) }));
  };
  const save = async () => {
    setTried(true);
    if (nameBad || issues.length > 0) return;
    setSaving(true);
    try {
      const body = { name: draft.name.trim(), nameAr: draft.nameAr.trim() || null, companyId, isDefault: draft.isDefault, tasks: draft.tasks.map((x, order) => ({ ...x, order })) };
      if (draft.id) await onboardingTemplatesApi.checklists.update(draft.id, body); else await onboardingTemplatesApi.checklists.create(body);
      await onSaved();
    } catch (e) { onError(e); } finally { setSaving(false); }
  };
  const preview = previewTasks(draft.tasks.filter(x => x.title.trim()), cls, start);
  const issueFor = (i: number, f: string) => tried && issues.some(x => x.index === i && x.field === f);
  const conditionLabel: Record<NationalityCondition, string> = { any: t('Everyone'), saudi: t('Saudi nationals only'), gcc: t('GCC nationals only'), expat: t('Expatriates only') };
  const classLabel: Record<PreviewClass, string> = { saudi: t('Saudi hire'), gcc: t('GCC hire'), expat: t('Expatriate hire'), unknown: t('Nationality not entered') };

  return (
    <SettingsDrawer open title={draft.id ? t('Edit checklist') : t('New checklist')} onClose={onClose} wide testId="checklist-editor">
      <div className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <div>
            <label htmlFor={`${uid}-name`} className="block text-sm font-medium">{t('Name')} <span className="text-xs font-normal text-slate-500">{t('(required)')}</span></label>
            <input id={`${uid}-name`} className={input} dir="ltr" value={draft.name} aria-invalid={tried && nameBad ? true : undefined} aria-describedby={tried && nameBad ? `${uid}-name-err` : undefined} onChange={e => onChange({ ...draft, name: e.target.value })} />
            {tried && nameBad && <p id={`${uid}-name-err`} className="mt-1 text-xs font-medium text-rose-700">{t('onboarding.templates.enterName')}</p>}
          </div>
          <div>
            <label htmlFor={`${uid}-name-ar`} className="block text-sm font-medium">{t('Name in Arabic')}</label>
            <input id={`${uid}-name-ar`} className={input} dir="rtl" value={draft.nameAr} onChange={e => onChange({ ...draft, nameAr: e.target.value })} />
          </div>
        </div>
        <label className="inline-flex items-center gap-2 text-sm"><input type="checkbox" checked={draft.isDefault} onChange={e => onChange({ ...draft, isDefault: e.target.checked })} />{t('Use for new hires of this company when no hire template names a checklist')}</label>

        <p aria-live="polite" role="status" className="sr-only">{announce}</p>
        <ol className="space-y-3" data-testid="task-rows">
          {draft.tasks.map((task, i) => (
            <li key={task.id ?? `new-${i}`} className="rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid={`task-row-${i}`}>
              <fieldset>
                <legend className="px-1 text-sm font-semibold">{t('Task {n}', { n: String(i + 1) })}</legend>
                <div className="grid gap-3 sm:grid-cols-2">
                  <div>
                    <label htmlFor={`${uid}-t${i}`} className="block text-xs font-medium">{t('Task')}</label>
                    <input id={`${uid}-t${i}`} className={input} dir="ltr" value={task.title} aria-invalid={issueFor(i, 'title') || issueFor(i, 'duplicate') ? true : undefined}
                      aria-describedby={issueFor(i, 'title') || issueFor(i, 'duplicate') ? `${uid}-t${i}-err` : undefined} onChange={e => setTask(i, { title: e.target.value })} />
                    {(issueFor(i, 'title') || issueFor(i, 'duplicate')) && <p id={`${uid}-t${i}-err`} className="mt-1 text-xs font-medium text-rose-700">{issueFor(i, 'duplicate') ? t('Each task needs its own title.') : t('Enter a task title of 200 characters or fewer.')}</p>}
                  </div>
                  <div>
                    <label htmlFor={`${uid}-ta${i}`} className="block text-xs font-medium">{t('Task in Arabic')}</label>
                    <input id={`${uid}-ta${i}`} className={input} dir="rtl" value={task.titleAr ?? ''} onChange={e => setTask(i, { titleAr: e.target.value })} />
                  </div>
                  <div>
                    <label htmlFor={`${uid}-o${i}`} className="block text-xs font-medium">{t('Who does it')}</label>
                    <select id={`${uid}-o${i}`} className={input} value={task.ownerRole} onChange={e => setTask(i, { ownerRole: e.target.value as OwnerRole })}>
                      {OWNER_ROLES.map(r => <option key={r} value={r}>{t(`onboarding.owner.${r}`)}</option>)}
                    </select>
                  </div>
                  <div>
                    <label htmlFor={`${uid}-d${i}`} className="block text-xs font-medium">{t('Due (days from the joining date)')}</label>
                    <input id={`${uid}-d${i}`} type="number" inputMode="numeric" className={input} dir="ltr" value={task.dueOffsetDays} aria-invalid={issueFor(i, 'dueOffsetDays') ? true : undefined}
                      aria-describedby={`${uid}-d${i}-hint`} onChange={e => setTask(i, { dueOffsetDays: e.target.value === '' ? 0 : Number(e.target.value) })} />
                    <p id={`${uid}-d${i}-hint`} className="mt-1 text-xs text-slate-500">{t('Use a negative number for before the joining date, such as -7.')}</p>
                  </div>
                  <div>
                    <label htmlFor={`${uid}-c${i}`} className="block text-xs font-medium">{t('Applies to')}</label>
                    <select id={`${uid}-c${i}`} className={input} value={task.condition.nationalityClass} onChange={e => setTask(i, { condition: { nationalityClass: e.target.value as NationalityCondition } })}>
                      {NATIONALITY_CONDITIONS.map(c => <option key={c} value={c}>{conditionLabel[c]}</option>)}
                    </select>
                  </div>
                  <div>
                    <label htmlFor={`${uid}-l${i}`} className="block text-xs font-medium">{t('Links to a profile section')}</label>
                    <select id={`${uid}-l${i}`} className={input} value={task.linkSection ?? ''} onChange={e => setTask(i, { linkSection: e.target.value })}>
                      <option value="">{t('None')}</option>
                      {LINK_SECTIONS.map(s => <option key={s} value={s}>{t(`onboarding.section.${s}`)}</option>)}
                    </select>
                  </div>
                </div>
                <div className="mt-3 space-y-1">
                  <label className="flex items-start gap-2 text-sm"><input type="checkbox" className="mt-1" checked={task.blocksActivation} onChange={e => setTask(i, { blocksActivation: e.target.checked })} /><span>{t('Blocks activation')}<span className="block text-xs text-slate-500">{t('While this task is open, the hire cannot be activated. Leave it off unless the work must be finished first.')}</span></span></label>
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={task.evidenceRequired} onChange={e => setTask(i, { evidenceRequired: e.target.checked })} />{t('Needs an uploaded document before it can be completed')}</label>
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={task.isStatutory} onChange={e => setTask(i, { isStatutory: e.target.checked })} />{t('Required by law or regulation')}</label>
                </div>
                <div className="mt-3 flex flex-wrap gap-2">
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={i === 0} onClick={() => move(i, -1)} aria-label={t('Move task {n} up', { n: String(i + 1) })}><ArrowUp className="h-3.5 w-3.5" aria-hidden="true" />{t('Move up')}</button>
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={i === draft.tasks.length - 1} onClick={() => move(i, 1)} aria-label={t('Move task {n} down', { n: String(i + 1) })}><ArrowDown className="h-3.5 w-3.5" aria-hidden="true" />{t('Move down')}</button>
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => onChange({ ...draft, tasks: draft.tasks.filter((_, j) => j !== i) })} aria-label={t('Remove task {n}', { n: String(i + 1) })}><Trash2 className="h-3.5 w-3.5" aria-hidden="true" />{t('Remove task')}</button>
                </div>
              </fieldset>
            </li>
          ))}
        </ol>
        <button type="button" className="btn-secondary" onClick={() => onChange({ ...draft, tasks: [...draft.tasks, blankTask()] })}><Plus className="h-4 w-4" aria-hidden="true" />{t('Add a task')}</button>

        <section aria-label={t('Preview')} className="rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="checklist-preview">
          <h3 className="text-sm font-semibold">{t('Preview')}</h3>
          <div className="mt-2 flex flex-wrap gap-3">
            <div><label htmlFor={`${uid}-pc`} className="block text-xs font-medium">{t('For a')}</label>
              <select id={`${uid}-pc`} className={input} value={cls} onChange={e => setCls(e.target.value as PreviewClass)}>{PREVIEW_CLASSES.map(c => <option key={c} value={c}>{classLabel[c]}</option>)}</select></div>
            <div><label htmlFor={`${uid}-ps`} className="block text-xs font-medium">{t('Joining date')}</label>
              <input id={`${uid}-ps`} type="date" className={input} dir="ltr" value={start} onChange={e => setStart(e.target.value)} /></div>
          </div>
          <p role="status" aria-live="polite" className="mt-2 text-xs text-slate-500">{t('{count, plural, one {# task for this hire.} other {# tasks for this hire.}}', { count: Number(preview.length) })}</p>
          <ul className="mt-1 space-y-1 text-sm">
            {preview.map((p, i) => <li key={i}>{p.title} <span className="text-xs text-slate-500">({t(`onboarding.owner.${p.ownerRole}`)}, {p.dueDate ?? t('Due when the start date is set')})</span>{p.blocksActivation && <span className="ms-1 text-xs font-medium text-amber-800">{t('Blocks activation')}</span>}</li>)}
          </ul>
        </section>

        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          {/* !bg-blue-700: .btn-primary is 4.49:1 outside .tenant-app-shell, where this portalled drawer sits (see FU-1 in 05-DECISIONS.md). */}
              <button type="button" className="btn-primary !bg-blue-700" disabled={saving} onClick={() => { void save(); }}>{t('Save checklist')}</button>
        </div>
      </div>
    </SettingsDrawer>
  );
}
