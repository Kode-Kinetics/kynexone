'use client';

import { useCallback, useEffect, useState } from 'react';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { learningApi, type CourseSummary, type LearningPath, type PathInput } from '../../api/learning';
import { departmentsApi, type DepartmentDto } from '../../api/organization';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { learningError, pathStatusLabel, validCode } from '../../lib/learning';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, LoadingRows } from './LearningParts';

const tone = { Draft: 'amber', Active: 'emerald', Archived: 'slate' } as const;

/**
 * Learning paths (LRN-02): ordered courses, required or optional, each optionally requiring the course before it
 * (enforced by the server on launch and attempt), eligibility by department, effective dates and the renewal window.
 * A path's courses are fixed once it is active, because people are assigned to exactly those courses.
 */
export function PathsPanel({ canManage }: { canManage: boolean }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [paths, setPaths] = useState<LearningPath[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<LearningPath | 'new' | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setPaths(await learningApi.paths()); }
    catch (e) { setError(learningError(e, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  const act = async (key: string, work: () => Promise<unknown>, success: string) => {
    setBusy(key);
    try { await work(); toast.success(success); await load(); }
    catch (e) { toast.error(learningError(e, t)); }
    finally { setBusy(null); }
  };

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!paths) return <LoadingRows label={t('Loading learning paths')} />;

  return (
    <div className="space-y-3" data-testid="paths-panel">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('A path is what you assign: its courses in order, and who it is for.')}</p>
        {canManage && (
          <button type="button" className="btn-primary inline-flex items-center gap-1.5 text-sm" onClick={() => setEditing('new')}>
            <Plus className="h-4 w-4" aria-hidden="true" />{t('New learning path')}
          </button>
        )}
      </div>
      {paths.length === 0 ? (
        <EmptyBlock title={t('No learning paths yet')} body={t('Publish a course, then put it in a path to assign it.')} />
      ) : (
        <div className="grid gap-3 lg:grid-cols-2">
          {paths.map((p) => (
            <article key={p.id} className="wg-card space-y-2 rounded-2xl p-4" data-testid={`path-${p.code}`}>
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                  <h3 className="text-sm font-bold text-slate-800 dark:text-slate-100">{locale === 'ar' && p.nameAr ? p.nameAr : p.name}</h3>
                  <p className="text-xs text-slate-500" dir="ltr">{p.code}</p>
                </div>
                <StatusChip label={t(pathStatusLabel[p.status])} tone={tone[p.status]} dot />
              </div>
              <ol className="space-y-1 text-xs">
                {p.items.map((item) => (
                  <li key={item.id} className="flex flex-wrap gap-x-2">
                    <span className="font-semibold">{t('{n}. {course}', { n: item.sequence, course: locale === 'ar' && item.courseTitleAr ? item.courseTitleAr : item.courseTitle })}</span>
                    <span className="text-slate-500">{item.isRequired ? t('Required') : t('Optional')}</span>
                    {item.prerequisiteSequence && <span className="text-slate-500">{t('After course {n}', { n: item.prerequisiteSequence })}</span>}
                    {!item.hasPublishedVersion && <span className="font-semibold text-amber-700">{t('No published version')}</span>}
                  </li>
                ))}
              </ol>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {t('{live} in progress · {done} completed', { live: p.liveAssignments, done: p.completedAssignments })}
                {p.effectiveFrom || p.effectiveTo ? ` · ${t('Effective {from} to {to}', { from: p.effectiveFrom ? fx.date(p.effectiveFrom) : '—', to: p.effectiveTo ? fx.date(p.effectiveTo) : '—' })}` : ''}
                {(p.eligibility.departmentIds?.length ?? 0) > 0 ? ` · ${t('{count, plural, one {# department} other {# departments}} eligible', { count: p.eligibility.departmentIds!.length })}` : ` · ${t('Everyone is eligible')}`}
              </p>
              {canManage && p.status !== 'Archived' && (
                <div className="flex flex-wrap gap-1.5">
                  <button type="button" className="btn-secondary text-xs" onClick={() => setEditing(p)} disabled={busy !== null}>{t('Edit')}</button>
                  {p.status === 'Draft' && (
                    <button type="button" className="btn-primary text-xs" disabled={busy !== null}
                      onClick={() => void act(`activate-${p.id}`, () => learningApi.activatePath(p.id), t('Path activated. It can now be assigned.'))}>
                      {busy === `activate-${p.id}` ? t('Activating…') : t('Activate')}
                    </button>
                  )}
                  <button type="button" className="btn-secondary text-xs" disabled={busy !== null}
                    onClick={() => void act(`archive-${p.id}`, () => learningApi.archivePath(p.id), t('Path archived. Its standing rules were switched off; people already assigned carry on.'))}>
                    {t('Archive')}
                  </button>
                </div>
              )}
            </article>
          ))}
        </div>
      )}
      {editing && <PathEditor path={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={async () => { setEditing(null); await load(); }} />}
    </div>
  );
}

interface ItemDraft { courseId: string; isRequired: boolean; afterPrevious: boolean }

function PathEditor({ path, onClose, onSaved }: { path: LearningPath | null; onClose: () => void; onSaved: () => Promise<void> }) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [courses, setCourses] = useState<CourseSummary[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [code, setCode] = useState(path?.code ?? '');
  const [name, setName] = useState(path?.name ?? '');
  const [nameAr, setNameAr] = useState(path?.nameAr ?? '');
  const [description, setDescription] = useState(path?.description ?? '');
  const [from, setFrom] = useState(path?.effectiveFrom ?? '');
  const [to, setTo] = useState(path?.effectiveTo ?? '');
  const [renewal, setRenewal] = useState(String(path?.renewalWindowDays ?? 30));
  const [departmentIds, setDepartmentIds] = useState<string[]>(path?.eligibility.departmentIds ?? []);
  const [items, setItems] = useState<ItemDraft[]>(() => (path?.items ?? []).map((i) => ({ courseId: i.courseId, isRequired: i.isRequired, afterPrevious: i.prerequisiteSequence === i.sequence - 1 })));
  const [adding, setAdding] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);
  const locked = !!path && path.status !== 'Draft';

  useEffect(() => {
    void learningApi.courses().then((list) => setCourses(list.filter((c) => c.status !== 'Retired'))).catch(() => setCourses([]));
    void departmentsApi.listAll().then(setDepartments).catch(() => setDepartments([]));
  }, []);

  const label = (id: string) => {
    const c = courses.find((x) => x.id === id) ?? path?.items.find((x) => x.courseId === id);
    if (!c) return id;
    return 'title' in c ? (locale === 'ar' && c.titleAr ? c.titleAr : c.title) : (locale === 'ar' && c.courseTitleAr ? c.courseTitleAr : c.courseTitle);
  };

  const move = (index: number, by: number) => setItems((list) => {
    const next = [...list];
    const [moved] = next.splice(index, 1);
    next.splice(index + by, 0, moved);
    return next.map((x, i) => (i === 0 ? { ...x, afterPrevious: false } : x));
  });

  const save = async () => {
    const e: Record<string, string> = {};
    if (!path && !validCode(code)) e.code = t('Use 2–40 letters, digits or dashes, e.g. FIRE-SAFETY.');
    if (!name.trim()) e.name = t('Give the path a name.');
    if (!locked && items.length === 0) e.items = t('Add at least one course.');
    if (!locked && items.length > 0 && !items.some((i) => i.isRequired)) e.items = t('At least one course must be required.');
    if (!locked && items.some((i, idx) => i.afterPrevious && (idx === 0 || !items[idx - 1].isRequired))) e.items = t('A course can only wait for a required course before it.');
    if (from && to && to < from) e.dates = t('The path cannot end before it starts.');
    const days = Number(renewal);
    if (!Number.isInteger(days) || days < 0 || days > 365) e.renewal = t('Between 0 and 365 days.');
    setErrors(e);
    if (Object.keys(e).length) return;
    const body: PathInput = {
      code: code.trim().toUpperCase(), name: name.trim(), nameAr: nameAr.trim() || null, description: description.trim() || null,
      eligibility: { departmentIds }, effectiveFrom: from || null, effectiveTo: to || null, renewalWindowDays: days,
      items: locked ? undefined : items.map((i, idx) => ({ courseId: i.courseId, isRequired: i.isRequired, prerequisiteSequence: i.afterPrevious ? idx : null })),
      version: path?.version,
    };
    setSaving(true);
    try {
      if (path) await learningApi.updatePath(path.id, body); else await learningApi.createPath(body);
      toast.success(t('Learning path saved.'));
      await onSaved();
    } catch (err) {
      toast.error(learningError(err, t));
    } finally {
      setSaving(false);
    }
  };

  const available = courses.filter((c) => !items.some((i) => i.courseId === c.id) && !items.some((i) => courses.find((x) => x.id === i.courseId)?.code === c.code));

  return (
    <Modal isOpen size="lg" onClose={onClose} title={path ? t('Edit {name}', { name: path.name }) : t('New learning path')}
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving} data-testid="path-save">{saving ? t('Saving...') : t('Save path')}</button>
        </div>
      )}>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); void save(); }} noValidate data-testid="path-editor">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field id="lp-code" label={t('Code')} error={errors.code}>
            <input id="lp-code" className="input w-full" dir="ltr" value={code} disabled={!!path} onChange={(e) => setCode(e.target.value)} maxLength={40} />
          </Field>
          <Field id="lp-name" label={t('Name')} error={errors.name}>
            <input id="lp-name" className="input w-full" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} />
          </Field>
          <Field id="lp-name-ar" label={t('Name in Arabic')} optional>
            <input id="lp-name-ar" className="input w-full" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} />
          </Field>
          <Field id="lp-renewal" label={t('Open renewal this many days before a certificate expires')} error={errors.renewal}>
            <input id="lp-renewal" type="number" min={0} max={365} className="input w-full" value={renewal} onChange={(e) => setRenewal(e.target.value)} />
          </Field>
          <div className="sm:col-span-2">
            <Field id="lp-description" label={t('Description')} optional>
              <textarea id="lp-description" className="input w-full" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
            </Field>
          </div>
          <Field id="lp-from" label={t('Effective from')} optional error={errors.dates}>
            <input id="lp-from" type="date" className="input w-full" value={from} onChange={(e) => setFrom(e.target.value)} />
          </Field>
          <Field id="lp-to" label={t('Effective to')} optional>
            <input id="lp-to" type="date" className="input w-full" value={to} onChange={(e) => setTo(e.target.value)} />
          </Field>
        </div>

        <fieldset className="space-y-2">
          <legend className="text-sm font-bold">{t('Courses, in order')}</legend>
          {locked && <p className="text-xs text-amber-700 dark:text-amber-300">{t('The courses of an active path are fixed. Create a new path to change them.')}</p>}
          {errors.items && <p role="alert" className="text-xs text-rose-600">{errors.items}</p>}
          <ol className="space-y-2">
            {items.map((item, idx) => (
              <li key={item.courseId} className="flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 p-2 text-sm dark:border-white/10" data-testid="path-item">
                <span className="min-w-0 flex-1 font-semibold">{t('{n}. {course}', { n: idx + 1, course: label(item.courseId) })}</span>
                <label className="flex items-center gap-1 text-xs"><input type="checkbox" disabled={locked} checked={item.isRequired}
                  onChange={(e) => setItems((l) => l.map((x, i) => (i === idx ? { ...x, isRequired: e.target.checked } : x)))} />{t('Required')}</label>
                {idx > 0 && (
                  <label className="flex items-center gap-1 text-xs"><input type="checkbox" disabled={locked} checked={item.afterPrevious}
                    onChange={(e) => setItems((l) => l.map((x, i) => (i === idx ? { ...x, afterPrevious: e.target.checked } : x)))} />{t('Only after the course above')}</label>
                )}
                {!locked && (
                  <span className="flex gap-1">
                    <button type="button" className="btn-secondary px-2 py-1" disabled={idx === 0} onClick={() => move(idx, -1)} aria-label={t('Move {course} up', { course: label(item.courseId) })}><ArrowUp className="h-3.5 w-3.5" aria-hidden="true" /></button>
                    <button type="button" className="btn-secondary px-2 py-1" disabled={idx === items.length - 1} onClick={() => move(idx, 1)} aria-label={t('Move {course} down', { course: label(item.courseId) })}><ArrowDown className="h-3.5 w-3.5" aria-hidden="true" /></button>
                    <button type="button" className="btn-secondary px-2 py-1" onClick={() => setItems((l) => l.filter((_, i) => i !== idx).map((x, i) => (i === 0 ? { ...x, afterPrevious: false } : x)))} aria-label={t('Remove {course}', { course: label(item.courseId) })}><Trash2 className="h-3.5 w-3.5" aria-hidden="true" /></button>
                  </span>
                )}
              </li>
            ))}
          </ol>
          {!locked && (
            <div className="flex flex-wrap items-end gap-2">
              <Field id="lp-add" label={t('Add a course')}>
                <select id="lp-add" className="input w-64" value={adding} onChange={(e) => setAdding(e.target.value)}>
                  <option value="">{t('Choose a course')}</option>
                  {available.map((c) => <option key={c.id} value={c.id}>{t('{course} (version {n})', { course: locale === 'ar' && c.titleAr ? c.titleAr : c.title, n: c.versionNumber })}</option>)}
                </select>
              </Field>
              <button type="button" className="btn-secondary inline-flex items-center gap-1 text-xs" disabled={!adding}
                onClick={() => { setItems((l) => [...l, { courseId: adding, isRequired: true, afterPrevious: false }]); setAdding(''); }}>
                <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('Add')}
              </button>
            </div>
          )}
        </fieldset>

        <fieldset className="space-y-2">
          <legend className="text-sm font-bold">{t('Who it is for')}</legend>
          <p className="text-xs text-slate-500">{t('Leave every department unticked to make the path open to everyone.')}</p>
          <div className="grid max-h-40 gap-1 overflow-y-auto sm:grid-cols-2">
            {departments.map((d) => (
              <label key={d.id} className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={departmentIds.includes(d.id)}
                  onChange={(e) => setDepartmentIds((ids) => (e.target.checked ? [...ids, d.id] : ids.filter((x) => x !== d.id)))} />
                {locale === 'ar' && d.nameAr ? d.nameAr : d.nameEn}
              </label>
            ))}
          </div>
        </fieldset>
      </form>
    </Modal>
  );
}
