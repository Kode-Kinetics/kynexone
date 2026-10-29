'use client';

import { pickable } from '../../lib/pickable';
import { useCallback, useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { learningApi, type BulkResult, type LearningPath, type Rule, type TargetKind } from '../../api/learning';
import { departmentsApi, designationsApi, gradesApi, companiesApi } from '../../api/organization';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { learningError, refusalReasonLabel, targetKindLabel, todayIso } from '../../lib/learning';
import { EmployeePicker, type SelectedEmployee } from '../EmployeePicker';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, LoadingRows } from './LearningParts';

interface Option { id: string; label: string }

/**
 * Assign (LRN-03): a path to chosen employees or to everyone in a department, designation or grade, due on a date
 * or N days after assignment. The result says, per person, who was assigned, who already had it, and who was refused
 * and why. Standing rules (learning.manage) also catch future hires and transfers on the hourly sweep.
 */
export function AssignPanel({ canAssign, canManageRules }: { canAssign: boolean; canManageRules: boolean }) {
  const { t, locale } = useLocale();
  const [paths, setPaths] = useState<LearningPath[] | null>(null);
  const [targets, setTargets] = useState<Record<Exclude<TargetKind, 'Employee'>, Option[]>>({ Company: [], Department: [], Designation: [], Grade: [] });
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const list = await learningApi.paths();
      setPaths(list.filter((p) => p.status === 'Active'));
      const [companies, departments, designations, grades] = await Promise.all([
        companiesApi.listAll().catch(() => []), departmentsApi.listAll().catch(() => []), designationsApi.listAll().catch(() => []), gradesApi.listAll().catch(() => []),
      ]);
      setTargets({
        Company: companies.map((c) => ({ id: c.id, label: locale === 'ar' && c.legalNameAr ? c.legalNameAr : c.legalNameEn })),
        Department: pickable(departments).map((d) => ({ id: d.id, label: locale === 'ar' && d.nameAr ? d.nameAr : d.nameEn })),
        Designation: pickable(designations).map((d) => ({ id: d.id, label: locale === 'ar' && d.titleAr ? d.titleAr : d.titleEn })),
        Grade: pickable(grades).map((g) => ({ id: g.id, label: g.name })),
      });
    } catch (e) {
      setError(learningError(e, t));
    }
  }, [t, locale]);
  useEffect(() => { void load(); }, [load]);

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!paths) return <LoadingRows label={t('Loading learning paths')} />;
  if (paths.length === 0) return <EmptyBlock title={t('No active learning paths')} body={t('Activate a learning path before assigning it.')} />;

  return (
    <div className="space-y-5">
      {canAssign && <AssignForm paths={paths} targets={targets} />}
      {canManageRules && <RulesSection paths={paths} targets={targets} />}
    </div>
  );
}

function AssignForm({ paths, targets }: { paths: LearningPath[]; targets: Record<Exclude<TargetKind, 'Employee'>, Option[]> }) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const [pathId, setPathId] = useState(paths[0]?.id ?? '');
  const [kind, setKind] = useState<TargetKind>('Employee');
  const [people, setPeople] = useState<SelectedEmployee[]>([]);
  const [targetId, setTargetId] = useState('');
  const [dueMode, setDueMode] = useState<'date' | 'days'>('date');
  const [dueDate, setDueDate] = useState('');
  const [dueDays, setDueDays] = useState('30');
  const [mandatory, setMandatory] = useState(true);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<BulkResult | null>(null);

  const submit = async () => {
    const e: Record<string, string> = {};
    if (!pathId) e.path = t('Choose a learning path.');
    if (kind === 'Employee' && people.length === 0) e.target = t('Add at least one employee.');
    if (kind !== 'Employee' && !targetId) e.target = t('Choose who to assign it to.');
    if (dueMode === 'date' && dueDate && dueDate < todayIso()) e.due = t('The due date cannot be in the past.');
    if (dueMode === 'days' && (!/^\d+$/.test(dueDays) || Number(dueDays) > 3650)) e.due = t('Enter a number of days between 0 and 3650.');
    setErrors(e);
    if (Object.keys(e).length) return;
    setBusy(true);
    setResult(null);
    try {
      const res = await learningApi.assign({
        pathId, targetKind: kind, employeeIds: kind === 'Employee' ? people.map((p) => p.id) : undefined, targetId: kind === 'Employee' ? undefined : targetId,
        dueDate: dueMode === 'date' ? (dueDate || null) : null, dueInDays: dueMode === 'days' ? Number(dueDays) : null, isMandatory: mandatory,
      });
      setResult(res);
      toast.success(t('{count, plural, one {# person assigned} other {# people assigned}}.', { count: res.created }));
      setPeople([]);
    } catch (err) {
      toast.error(learningError(err, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="wg-card space-y-4 rounded-2xl p-4" aria-labelledby="assign-title" data-testid="assign-form">
      <h2 id="assign-title" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Assign a learning path')}</h2>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); void submit(); }} noValidate>
        <Field id="as-path" label={t('Learning path')} error={errors.path}>
          <select id="as-path" className="input w-full max-w-md" value={pathId} onChange={(e) => setPathId(e.target.value)}>
            {paths.map((p) => <option key={p.id} value={p.id}>{locale === 'ar' && p.nameAr ? p.nameAr : p.name}</option>)}
          </select>
        </Field>
        <fieldset className="space-y-2">
          <legend className="text-xs font-semibold">{t('Assign to')}</legend>
          <div className="flex flex-wrap gap-3" role="radiogroup" aria-label={t('Assign to')}>
            {(['Employee', 'Department', 'Designation', 'Grade'] as TargetKind[]).map((k) => (
              <label key={k} className="flex items-center gap-1.5 text-sm">
                <input type="radio" name="as-kind" checked={kind === k} onChange={() => { setKind(k); setTargetId(''); }} />{t(targetKindLabel[k])}
              </label>
            ))}
          </div>
          {kind === 'Employee' ? (
            <div className="space-y-2">
              <EmployeePicker value={null} label={t('Add an employee')} placeholder={t('Search by name or ID…')}
                onChange={(emp) => { if (emp && !people.some((p) => p.id === emp.id)) setPeople((l) => [...l, emp]); }} />
              <ul className="flex flex-wrap gap-1.5" aria-label={t('Employees to assign')}>
                {people.map((p) => (
                  <li key={p.id} className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2.5 py-1 text-xs dark:bg-white/10">
                    {p.fullName}
                    <button type="button" onClick={() => setPeople((l) => l.filter((x) => x.id !== p.id))} aria-label={t('Remove {name}', { name: p.fullName })}>
                      <X className="h-3 w-3" aria-hidden="true" />
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ) : (
            <select id="as-target" aria-label={t(targetKindLabel[kind])} className="input w-full max-w-md" value={targetId} onChange={(e) => setTargetId(e.target.value)}>
              <option value="">{t('Choose…')}</option>
              {targets[kind as Exclude<TargetKind, 'Employee'>].map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
            </select>
          )}
          {errors.target && <p role="alert" className="text-xs text-rose-600">{errors.target}</p>}
        </fieldset>
        <fieldset className="space-y-2">
          <legend className="text-xs font-semibold">{t('Due')}</legend>
          <div className="flex flex-wrap items-center gap-3 text-sm">
            <label className="flex items-center gap-1.5"><input type="radio" name="as-due" checked={dueMode === 'date'} onChange={() => setDueMode('date')} />{t('On a date')}</label>
            <input type="date" className="input w-auto" aria-label={t('Due date')} value={dueDate} min={todayIso()} disabled={dueMode !== 'date'} onChange={(e) => setDueDate(e.target.value)} />
            <label className="flex items-center gap-1.5"><input type="radio" name="as-due" checked={dueMode === 'days'} onChange={() => setDueMode('days')} />{t('Days after assignment')}</label>
            <input type="number" min={0} max={3650} className="input w-24" aria-label={t('Days after assignment')} value={dueDays} disabled={dueMode !== 'days'} onChange={(e) => setDueDays(e.target.value)} />
          </div>
          {errors.due && <p role="alert" className="text-xs text-rose-600">{errors.due}</p>}
        </fieldset>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={mandatory} onChange={(e) => setMandatory(e.target.checked)} />{t('Mandatory training (reminders and overdue notices are sent)')}
        </label>
        <button type="submit" className="btn-primary" disabled={busy} data-testid="assign-submit">{busy ? t('Assigning…') : t('Assign')}</button>
      </form>
      {result && <BulkResultView result={result} />}
    </section>
  );
}

export function BulkResultView({ result }: { result: BulkResult }) {
  const { t } = useLocale();
  return (
    <div className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" role="status" data-testid="assign-result">
      <p className="text-sm font-semibold">
        {t('{created} assigned · {skipped} already had it · {refused} not assigned', { created: result.created, skipped: result.skipped, refused: result.refused })}
      </p>
      {result.results.filter((r) => r.outcome !== 'created').length > 0 && (
        <ul className="space-y-1 text-xs">
          {result.results.filter((r) => r.outcome !== 'created').map((r) => (
            <li key={r.employeeId}>
              <span className="font-semibold">{r.employeeName || t('Employee {id}', { id: r.employeeId })}</span>{' — '}
              {r.reasons.map((reason) => t(refusalReasonLabel[reason] ?? reason)).join('; ')}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function RulesSection({ paths, targets }: { paths: LearningPath[]; targets: Record<Exclude<TargetKind, 'Employee'>, Option[]> }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [rules, setRules] = useState<Rule[] | null>(null);
  const [pathId, setPathId] = useState(paths[0]?.id ?? '');
  const [kind, setKind] = useState<Exclude<TargetKind, 'Employee'>>('Department');
  const [targetId, setTargetId] = useState('');
  const [days, setDays] = useState('30');
  const [busy, setBusy] = useState(false);
  const [applied, setApplied] = useState<BulkResult | null>(null);

  const load = useCallback(async () => {
    try { setRules(await learningApi.rules()); } catch (e) { toast.error(learningError(e, t)); setRules([]); }
  }, [t, toast]);
  useEffect(() => { void load(); }, [load]);

  const create = async () => {
    if (!targetId) { toast.error(t('Choose who the rule applies to.')); return; }
    setBusy(true);
    try {
      const res = await learningApi.createRule({ pathId, targetKind: kind, targetId, dueInDays: Number(days) || 0, isMandatory: true });
      setApplied(res.applied);
      toast.success(t('Rule saved and applied to everyone it matches today.'));
      await load();
    } catch (e) {
      toast.error(learningError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="wg-card space-y-3 rounded-2xl p-4" aria-labelledby="rules-title" data-testid="rules-section">
      <h2 id="rules-title" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Standing rules')}</h2>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('A rule assigns a path to everyone in a group now, and to anyone who joins the group later (checked every hour).')}</p>
      <div className="flex flex-wrap items-end gap-2">
        <Field id="rl-path" label={t('Learning path')}>
          <select id="rl-path" className="input w-56" value={pathId} onChange={(e) => setPathId(e.target.value)}>
            {paths.map((p) => <option key={p.id} value={p.id}>{locale === 'ar' && p.nameAr ? p.nameAr : p.name}</option>)}
          </select>
        </Field>
        <Field id="rl-kind" label={t('Group')}>
          <select id="rl-kind" className="input w-40" value={kind} onChange={(e) => { setKind(e.target.value as Exclude<TargetKind, 'Employee'>); setTargetId(''); }}>
            {(['Company', 'Department', 'Designation', 'Grade'] as const).map((k) => <option key={k} value={k}>{t(targetKindLabel[k])}</option>)}
          </select>
        </Field>
        <Field id="rl-target" label={t(targetKindLabel[kind])}>
          <select id="rl-target" className="input w-56" value={targetId} onChange={(e) => setTargetId(e.target.value)}>
            <option value="">{t('Choose…')}</option>
            {targets[kind].map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
          </select>
        </Field>
        <Field id="rl-days" label={t('Due after (days)')}>
          <input id="rl-days" type="number" min={0} max={3650} className="input w-24" value={days} onChange={(e) => setDays(e.target.value)} />
        </Field>
        <button type="button" className="btn-primary" onClick={() => void create()} disabled={busy}>{busy ? t('Saving...') : t('Save rule')}</button>
      </div>
      {applied && <BulkResultView result={applied} />}
      {rules === null ? <LoadingRows label={t('Loading rules')} rows={1} /> : rules.length === 0 ? (
        <p className="text-xs text-slate-500">{t('No standing rules yet.')}</p>
      ) : (
        <ul className="divide-y divide-slate-100 text-sm dark:divide-white/5">
          {rules.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
              <span>
                <span className="font-semibold">{r.pathName}</span>{' — '}{t('{kind}: {target}', { kind: t(targetKindLabel[r.targetKind]), target: r.targetName })}
                <span className="block text-xs text-slate-500">
                  {t('Due {count, plural, one {# day} other {# days}} after assignment', { count: r.dueInDays })}
                  {r.lastAppliedAtUtc ? ` · ${t('Last applied {date}', { date: fx.dateTime(r.lastAppliedAtUtc) })}` : ''}
                </span>
              </span>
              <span className="flex items-center gap-2">
                <StatusChip label={r.isActive ? t('Active') : t('Switched off')} tone={r.isActive ? 'emerald' : 'slate'} />
                {r.isActive && (
                  <button type="button" className="btn-secondary text-xs"
                    onClick={async () => { try { await learningApi.deactivateRule(r.id); toast.success(t('Rule switched off.')); await load(); } catch (e) { toast.error(learningError(e, t)); } }}>
                    {t('Switch off')}
                  </button>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
