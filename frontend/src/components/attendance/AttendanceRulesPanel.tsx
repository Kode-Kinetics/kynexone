'use client';

import { pickable } from '../../lib/pickable';
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useState, type ReactNode } from 'react';
import { Info, Pencil, Plus, Power, AlarmClockCheck } from 'lucide-react';
import { attendanceApi, type AttendancePolicy } from '../../api/attendance';
import { apiErrorReason } from '../../api/client';
import { branchesApi, departmentsApi, gradesApi, type BranchDto, type DepartmentDto, type GradeDto } from '../../api/organization';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import {
  NEW_RULE_FORM, isCompanyWide, ruleFormFromPolicy, ruleFormToRequest, validateRuleForm,
  type AttendanceRuleErrors, type AttendanceRuleForm,
} from '../../lib/attendanceRules';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { StatusChip } from '../StatusChip';
import { SettingsDrawer } from '../companySetup/rules/SettingsDrawer';

type Lookups = { branches: BranchDto[]; departments: DepartmentDto[]; grades: GradeDto[]; failed: boolean };

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none disabled:bg-slate-50 disabled:text-slate-500 dark:border-white/10 dark:bg-white/5 dark:text-white dark:disabled:bg-white/[0.03]';
const invalid = 'border-rose-400 focus:border-rose-500';

/**
 * The attendance rules: what counts as late, an early exit or a half day, and the length of the working day. Attendance
 * processing has always read these (AttendanceService.ResolveAttendancePolicy); this is where they are edited.
 * Admin and HR Manager only (AttendancePoliciesController). The editor opens beside the list and reports unsaved edits
 * to Setup's leave guard. Days already processed are not changed by an edit; the screen says so.
 */
export function AttendanceRulesPanel({ onDirtyChange }: { onDirtyChange?: (dirty: boolean) => void }) {
  const t = useT();
  const { locale } = useLocale();
  const [policies, setPolicies] = useState<AttendancePolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [lookups, setLookups] = useState<Lookups>({ branches: [], departments: [], grades: [], failed: false });
  const [editing, setEditing] = useState<AttendancePolicy | 'new' | null>(null);
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [dirty, setDirty] = useState(false);

  useLayoutEffect(() => { onDirtyChange?.(editing !== null && dirty); return () => onDirtyChange?.(false); }, [editing, dirty, onDirtyChange]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setPolicies(await attendanceApi.policies.list());
      setLoadError(null);
    } catch (error) {
      setPolicies([]);
      setLoadError(error);
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  // The scope pickers need the company's structure. If it cannot be read, the policies still list; only scoping is off.
  useEffect(() => {
    let live = true;
    void Promise.allSettled([branchesApi.listAll(), departmentsApi.listAll(), gradesApi.listAll()]).then(([b, d, g]) => {
      if (!live) return;
      setLookups({
        branches: b.status === 'fulfilled' ? b.value : [], departments: d.status === 'fulfilled' ? d.value : [], grades: g.status === 'fulfilled' ? g.value : [],
        failed: [b, d, g].some(result => result.status === 'rejected'),
      });
    });
    return () => { live = false; };
  }, []);

  const arabic = locale === 'ar';
  const names = useMemo(() => ({
    branch: new Map(lookups.branches.map(x => [x.id, (arabic && x.nameAr) || x.nameEn])),
    department: new Map(lookups.departments.map(x => [x.id, (arabic && x.nameAr) || x.nameEn])),
    grade: new Map(lookups.grades.map(x => [x.id, x.name])),
  }), [lookups, arabic]);

  const scopeText = (policy: AttendancePolicy) => {
    if (isCompanyWide(policy)) return t('Everyone');
    return [
      policy.branchId && `${t('Branch')}: ${names.branch.get(policy.branchId) ?? '…'}`,
      policy.departmentId && `${t('Department')}: ${names.department.get(policy.departmentId) ?? '…'}`,
      policy.gradeId && `${t('Grade')}: ${names.grade.get(policy.gradeId) ?? '…'}`,
    ].filter(Boolean).join(' · ');
  };

  const setActive = (policy: AttendancePolicy, active: boolean) => {
    const run = async () => {
      setNotice(null);
      try {
        await (active ? attendanceApi.policies.reactivate(policy.id) : attendanceApi.policies.deactivate(policy.id));
        setNotice({ kind: 'ok', text: active ? t('{name} is on again.', { name: policy.name }) : t('{name} is off. Its employees follow the other rules.', { name: policy.name }) });
        setConfirm(null);
        await load();
      } catch (error) {
        setConfirm(null);
        setNotice({ kind: 'error', text: apiErrorReason(error, t('The change could not be saved. Try again.')) });
      }
    };
    if (active) void run();
    else setConfirm({
      title: t('Turn off these rules?'),
      message: t('Employees covered by {name} will follow the next most specific policy from now on. Days already processed do not change.', { name: policy.name }),
      confirmLabel: t('Turn off'), onConfirm: run, closeOnConfirm: false,
    });
  };

  return (
    <div className="space-y-4" data-testid="attendance-rules">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t('These rules decide who counts as late, who left early, what makes a half day, and how long a working day is. Each employee follows the most specific policy that covers them, or the one assigned on their record.')}
        </p>
        <button type="button" className="btn-primary shrink-0" onClick={() => { setNotice(null); setEditing('new'); }} disabled={loadError != null}>
          <Plus className="h-4 w-4" aria-hidden="true" />{t('New rules')}
        </button>
      </div>

      {notice && (
        <div role={notice.kind === 'error' ? 'alert' : 'status'}
          className={`rounded-lg border px-4 py-3 text-sm ${notice.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
          {notice.text}
        </div>
      )}

      {loading ? <p role="status" className="text-sm text-slate-400">{t('Loading…')}</p>
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
        : (
          <div className="grid gap-4 md:grid-cols-2" data-testid="attendance-rules-list">
            {policies.map(policy => (
              <article key={policy.id} className={`surface p-4 ${policy.isActive ? '' : 'opacity-70'}`} data-testid={`attendance-rule-${policy.code}`}>
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <AlarmClockCheck className="mb-2 h-5 w-5 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
                    <h3 className="font-semibold text-slate-900 dark:text-white">{policy.name}</h3>
                    <p className="mt-0.5 text-xs text-slate-500">{policy.code}</p>
                  </div>
                  <StatusChip label={policy.isActive ? t('Active') : t('Off')} tone={policy.isActive ? 'emerald' : 'slate'} dot />
                </div>
                <p className="mt-2 text-xs text-slate-600 dark:text-slate-300"><span className="font-medium">{t('Applies to')}:</span> {scopeText(policy)}</p>
                <dl className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs text-slate-500">
                  <Value label={t('Grace before late')} value={t('{n} min', { n: policy.graceMinutes })} />
                  <Value label={t('Early leaving allowed')} value={t('{n} min', { n: policy.earlyExitThresholdMinutes })} />
                  <Value label={t('Half day below')} value={t('{n} min', { n: policy.halfDayThresholdMinutes })} />
                  <Value label={t('Working day')} value={t('{n} min', { n: policy.standardWorkMinutes })} />
                  <Value label={t('Unpaid break')} value={t('{n} min', { n: policy.breakMinutes })} />
                </dl>
                <div className="mt-4 flex flex-wrap gap-2">
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { setNotice(null); setEditing(policy); }}>
                    <Pencil className="h-3.5 w-3.5" aria-hidden="true" />{t('Edit')}
                  </button>
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setActive(policy, !policy.isActive)}>
                    <Power className="h-3.5 w-3.5" aria-hidden="true" />{policy.isActive ? t('Turn off') : t('Turn on')}
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}

      <SettingsDrawer open={editing !== null} title={editing === 'new' ? t('New attendance rules') : t('Edit attendance rules')}
        description={t('Changes apply to attendance processed from now on.')} onClose={() => setEditing(null)} testId="attendance-rules-editor">
        {editing && (
          <RuleEditor key={editing === 'new' ? 'new' : editing.id} policy={editing === 'new' ? null : editing} lookups={lookups}
            onDirty={setDirty} onCancel={() => setEditing(null)}
            onSaved={async saved => { setEditing(null); setNotice({ kind: 'ok', text: t('{name} saved.', { name: saved.name }) }); await load(); }} />
        )}
      </SettingsDrawer>
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}

function Value({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0"><dt className="truncate">{label}</dt><dd className="font-medium text-slate-800 dark:text-slate-200">{value}</dd></div>;
}

function RuleEditor({ policy, lookups, onDirty, onCancel, onSaved }: {
  policy: AttendancePolicy | null;
  lookups: Lookups;
  onDirty: (dirty: boolean) => void;
  onCancel: () => void;
  onSaved: (saved: AttendancePolicy) => void | Promise<void>;
}) {
  const t = useT();
  const initial = useMemo(() => (policy ? ruleFormFromPolicy(policy) : NEW_RULE_FORM), [policy]);
  const [form, setForm] = useState<AttendanceRuleForm>(initial);
  const [errors, setErrors] = useState<AttendanceRuleErrors>({});
  const [saving, setSaving] = useState(false);
  const [serverError, setServerError] = useState('');
  const idBase = useId();

  useEffect(() => { onDirty(JSON.stringify(form) !== JSON.stringify(initial)); return () => onDirty(false); }, [form, initial, onDirty]);

  const set = (key: keyof AttendanceRuleForm, value: string) => {
    setForm(current => ({ ...current, [key]: value }));
    setErrors(current => ({ ...current, [key]: undefined }));
  };

  const save = async () => {
    const found = validateRuleForm(form);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    setSaving(true);
    setServerError('');
    try {
      const request = ruleFormToRequest(form);
      const saved = policy ? await attendanceApi.policies.update(policy.id, request) : await attendanceApi.policies.create(request);
      await onSaved(saved);
    } catch (error) {
      setServerError(apiErrorReason(error, t('The rules could not be saved. Try again.')));
      setSaving(false);
    }
  };

  const field = (key: keyof AttendanceRuleForm, label: string, help: string, opts: { numeric?: boolean; disabled?: boolean; maxLength?: number } = {}) => {
    const id = `${idBase}-${key}`;
    return (
      <div>
        <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-700 dark:text-slate-300">{label}</label>
        <input id={id} className={`${input} ${errors[key] ? invalid : ''}`} value={form[key]} disabled={opts.disabled} maxLength={opts.maxLength}
          inputMode={opts.numeric ? 'numeric' : undefined} onChange={event => set(key, event.target.value)}
          aria-invalid={errors[key] ? true : undefined} aria-describedby={`${id}-help`} />
        <p id={`${id}-help`} className={`mt-1 text-xs leading-5 ${errors[key] ? 'text-rose-600 dark:text-rose-400' : 'text-slate-500 dark:text-slate-400'}`}>{errors[key] ? t(errors[key]!) : help}</p>
      </div>
    );
  };

  const scope = (key: 'branchId' | 'departmentId' | 'gradeId', label: string, all: string, options: { id: string; name: string }[]) => {
    const id = `${idBase}-${key}`;
    return (
      <div>
        <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-700 dark:text-slate-300">{label}</label>
        <select id={id} className={input} value={form[key]} onChange={event => set(key, event.target.value)} disabled={lookups.failed && options.length === 0 && !form[key]}>
          <option value="">{all}</option>
          {options.map(option => <option key={option.id} value={option.id}>{option.name}</option>)}
        </select>
      </div>
    );
  };

  return (
    <form className="space-y-5" noValidate onSubmit={event => { event.preventDefault(); void save(); }}>
      <div className="grid gap-4 sm:grid-cols-2">
        {field('name', t('Name'), t('Shown in this list, for example “Head office hours”.'), { maxLength: 120 })}
        {field('code', t('Code'), policy ? t('The code cannot be changed: employees can be assigned to these rules by code.') : t('Short and unique, for example HQ-DAY. Letters, digits, hyphens and underscores.'), { disabled: policy !== null, maxLength: 40 })}
      </div>

      <fieldset className="space-y-3 rounded-xl border border-slate-200 p-4 dark:border-white/10">
        <legend className="px-1 text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Who it applies to')}</legend>
        <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">
          {t('Leave all three on “All” to cover everyone. Choose a branch, department or grade to cover only them. The most specific policy wins, and an employee assigned a policy on their record always follows that one.')}
        </p>
        {lookups.failed && <p role="status" className="text-xs text-amber-700 dark:text-amber-300">{t('Some branches, departments or grades could not be loaded.')}</p>}
        <div className="grid gap-4 sm:grid-cols-3">
          {scope('branchId', t('Branch'), t('All branches'), pickable(lookups.branches, form.branchId).map(x => ({ id: x.id, name: x.nameEn })))}
          {scope('departmentId', t('Department'), t('All departments'), pickable(lookups.departments, form.departmentId).map(x => ({ id: x.id, name: x.nameEn })))}
          {scope('gradeId', t('Grade'), t('All grades'), pickable(lookups.grades, form.gradeId).map(x => ({ id: x.id, name: x.name })))}
        </div>
      </fieldset>

      <div className="grid gap-4 sm:grid-cols-2">
        {field('graceMinutes', t('Grace period before late (minutes)'), t('Someone arriving within this time of shift start is not counted as late.'), { numeric: true })}
        {field('earlyExitThresholdMinutes', t('Early leaving allowed (minutes)'), t('Leaving this long before shift end is not counted as an early exit.'), { numeric: true })}
        {field('standardWorkMinutes', t('Working day (minutes)'), t('The length of a full day, for example 480 for 8 hours. Also used for leave days and Ramadan hours.'), { numeric: true })}
        {field('breakMinutes', t('Unpaid break (minutes)'), t('Taken off the time between first in and last out.'), { numeric: true })}
        {field('halfDayThresholdMinutes', t('Half day below (minutes)'), t('A day worked for less than this is marked as a half day.'), { numeric: true })}
      </div>

      <Callout>
        {t('Saving does not change days already processed. The new rules apply to days processed from now on and to days you recompute. Before a payroll run, unpaid days that now differ are listed under “Recompute these days”; paid and payroll-locked days never change.')}
      </Callout>

      {serverError && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300">{serverError}</p>}

      <div className="flex justify-end gap-2 border-t border-slate-200/80 pt-4 dark:border-white/10">
        <button type="button" className="btn-secondary" onClick={onCancel} disabled={saving}>{t('Cancel')}</button>
        <button type="submit" className="btn-primary" disabled={saving}>{saving ? t('Saving…') : policy ? t('Save changes') : t('Create rules')}</button>
      </div>
    </form>
  );
}

function Callout({ children }: { children: ReactNode }) {
  return (
    <p className="flex items-start gap-2 rounded-lg border border-slate-200 bg-white/70 px-3 py-2.5 text-xs leading-5 text-slate-600 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
      <Info className="mt-0.5 h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden="true" /><span>{children}</span>
    </p>
  );
}
