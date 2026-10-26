'use client';

import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown, Info, Lock } from 'lucide-react';
import { employeesApi, type EmployeeDetail } from '../../api/employees';
import { costCentersApi, departmentsApi, positionsApi, type CostCenterDto, type DepartmentDto, type PositionDto } from '../../api/organization';
import { financeGlApi } from '../../api/financeGl';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { describeApiError } from '../../lib/apiError';
import {
  costCentreErrorKind,
  isOverride,
  overridePatch,
  removeOverridePatch,
  resolveCostCentre,
  type Resolution,
  type ResolveInput,
  type SourceLevel,
} from '../../lib/employeeCostCentre';
import { pickServerMessage } from '../../lib/glCostAccounting';
import { Modal } from '../Modal';
import { FieldError, LoadError } from '../gl/glUi';

/**
 * Screen 6: the employee's cost centre on Employment. Shows the value with where it comes from as text, a "Why" disclosure
 * with the server's order, and Override / Remove override dialogs that require a reason (AM-30). Without
 * organization.costcenter.manage the field is read-only with a visible sentence.
 */

const STEPS: Array<{ level: SourceLevel; label: string }> = [
  { level: 'employee', label: 'Employee override' },
  { level: 'position', label: 'Position' },
  { level: 'department', label: 'Department' },
  { level: 'parentDepartment', label: 'Parent department' },
  { level: 'company', label: 'Company default' },
];

interface Loaded {
  costCentres: CostCenterDto[];
  departments: DepartmentDto[];
  positions: PositionDto[];
  /** undefined = the reader cannot see finance settings. */
  companyDefaultId: string | null | undefined;
  costingOn: boolean | undefined;
}

export function EmployeeCostCentreField({ employee, onChanged }: { employee: EmployeeDetail; onChanged?: () => void }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('organization.costcenter.manage');
  const canReadFinance = hasPermission('finance.gl.read');
  const labelId = useId();
  const whyId = useId();
  const overrideButton = useRef<HTMLButtonElement>(null);

  const [data, setData] = useState<Loaded | null>(null);
  const [loadError, setLoadError] = useState('');
  const [whyOpen, setWhyOpen] = useState(false);
  const [dialog, setDialog] = useState<'override' | 'remove' | null>(null);
  const [pick, setPick] = useState('');
  const [reason, setReason] = useState('');
  const [fieldError, setFieldError] = useState('');
  const [formError, setFormError] = useState('');
  const [saving, setSaving] = useState(false);
  const [lockedMessage, setLockedMessage] = useState('');
  const [status, setStatus] = useState('');

  const companyId = employee.companyId;
  const load = useCallback(async () => {
    setLoadError('');
    const [cc, deps, positions, readiness] = await Promise.allSettled([
      costCentersApi.listAll(companyId),
      departmentsApi.listAll(),
      positionsApi.list(companyId),
      canReadFinance && companyId ? financeGlApi.costAccountingReadiness(companyId) : Promise.reject(new Error('no-finance')),
    ]);
    if (cc.status === 'rejected') return setLoadError(describeApiError(cc.reason, t));
    if (deps.status === 'rejected') return setLoadError(describeApiError(deps.reason, t));
    setData({
      costCentres: cc.value,
      departments: deps.value,
      positions: positions.status === 'fulfilled' && Array.isArray(positions.value) ? positions.value : [],
      // Without finance.gl.read (or when the call fails) the company default and the switch are unknown, not "none"/"off".
      companyDefaultId: readiness.status === 'fulfilled' && readiness.value?.settings ? readiness.value.settings.defaultCostCenterId ?? null : undefined,
      costingOn: readiness.status === 'fulfilled' && readiness.value?.settings ? readiness.value.settings.enabled : undefined,
    });
  }, [canReadFinance, companyId, t]);
  useEffect(() => { void load(); }, [load]);

  const positionId = employee.positionId ?? null;
  const input: ResolveInput | null = useMemo(() => data && ({
    companyId,
    employee: { costCenterId: employee.costCenterId ?? null, departmentId: employee.departmentId ?? null, positionId, costCenterIsOverride: employee.costCenterIsOverride ?? null },
    departments: data.departments.map((d) => ({ id: d.id, nameEn: locale === 'ar' && d.nameAr ? d.nameAr : d.nameEn, parentDepartmentId: d.parentDepartmentId ?? null, costCenterId: d.costCenterId ?? null })),
    positions: data.positions.map((p) => ({ id: p.id, title: p.title ?? p.code ?? '', costCenterId: p.costCenterId ?? null })),
    costCentres: data.costCentres,
    companyDefaultId: data.companyDefaultId,
  }), [companyId, data, employee.costCenterId, employee.costCenterIsOverride, employee.departmentId, locale, positionId]);

  if (loadError) return <div data-testid="cc-emp-field"><LoadError message={loadError} onRetry={() => void load()} /></div>;
  if (!data || !input) {
    return <div data-testid="cc-emp-field" role="status" aria-busy="true"><span className="sr-only">{t('Loading…')}</span><div className="h-11 animate-pulse rounded-lg bg-slate-100 dark:bg-white/10" aria-hidden /></div>;
  }

  const ccById = (id: string | null) => data.costCentres.find((c) => c.id === id) ?? null;
  const ccName = (c: CostCenterDto) => (locale === 'ar' && c.nameAr ? c.nameAr : c.name);
  const resolution: Resolution = resolveCostCentre(input);
  const overridden = isOverride(input);
  const inherited: Resolution = overridden ? resolveCostCentre(input, { ignoreOverride: true }) : resolution;

  const returnsTo = (r: Resolution, code: string): string => {
    switch (r.kind) {
      case 'department': return t('Removing it returns to: {code}, from department {name}.', { code, name: r.sourceName ?? '' });
      case 'parentDepartment': return t('Removing it returns to: {code}, from parent department {name}.', { code, name: r.sourceName ?? '' });
      case 'position': return t('Removing it returns to: {code}, from position {name}.', { code, name: r.sourceName ?? '' });
      default: return t('Removing it returns to: {code}, from the company default.', { code });
    }
  };
  const replaces = (r: Resolution, code: string): string => {
    switch (r.kind) {
      case 'department': return t("Replaces the department's cost centre: {code}. Applies from the next Lock; locked runs do not change.", { code });
      case 'parentDepartment': return t("Replaces the parent department's cost centre: {code}. Applies from the next Lock; locked runs do not change.", { code });
      case 'position': return t("Replaces the position's cost centre: {code}. Applies from the next Lock; locked runs do not change.", { code });
      default: return t('Replaces the company default cost centre: {code}. Applies from the next Lock; locked runs do not change.', { code });
    }
  };
  const invalidSentence = (level: SourceLevel, code: string): string => {
    switch (level) {
      case 'employee': return t('The employee cost centre {code} is inactive, deleted or in another company.', { code });
      case 'position': return t('The position cost centre {code} is inactive, deleted or in another company.', { code });
      case 'department': return t('The department cost centre {code} is inactive, deleted or in another company.', { code });
      case 'parentDepartment': return t('The parent department cost centre {code} is inactive, deleted or in another company.', { code });
      default: return t('The company default cost centre {code} is inactive, deleted or in another company.', { code });
    }
  };
  const chipText = (r: Resolution): string => {
    switch (r.kind) {
      case 'employee': return t('Set for this employee (override)');
      case 'department': return t('From department: {name}', { name: r.sourceName ?? '' });
      case 'parentDepartment': return t('From parent department: {name}', { name: r.sourceName ?? '' });
      case 'position': return t('From position: {name}', { name: r.sourceName ?? '' });
      case 'companyDefault': return t('Company default');
      case 'none': return t('No cost centre');
      default: return '';
    }
  };

  const current = ccById(resolution.costCentreId);
  const invalidCode = resolution.kind === 'invalid' ? resolution.code : null;
  const inheritedCc = ccById(inherited.costCentreId);
  const applyingLevel: SourceLevel | null = resolution.kind === 'invalid' ? resolution.level
    : resolution.kind === 'companyDefault' ? 'company'
    : resolution.kind === 'none' || resolution.kind === 'unknown' ? null : resolution.kind;

  const optionList = data.costCentres.filter((c) => c.isActive && c.companyId === companyId);
  const locked = !!lockedMessage;

  const closeDialog = () => {
    if (saving) return;
    setDialog(null);
    setFieldError('');
    setFormError('');
    // Remove override unmounts its own button, so focus goes where the next action is.
    requestAnimationFrame(() => overrideButton.current?.focus());
  };
  const openDialog = (kind: 'override' | 'remove') => {
    setPick('');
    setReason('');
    setFieldError('');
    setFormError('');
    setStatus('');
    setDialog(kind);
  };

  const submit = async () => {
    setFieldError('');
    setFormError('');
    if (dialog === 'override' && !pick) return setFieldError(t('Choose a cost centre.'));
    if (!reason.trim()) return setFieldError(t('Enter a reason for this change.'));
    const body = dialog === 'override' ? overridePatch(ccById(pick)?.code ?? '', reason) : removeOverridePatch(reason);
    setSaving(true);
    try {
      await employeesApi.update(employee.id, new Date().toISOString().slice(0, 10), body.changes, { costCenterReason: body.costCenterReason });
      setStatus(dialog === 'override' ? t('Override saved.') : t('Override removed.'));
      setDialog(null);
      onChanged?.();
      requestAnimationFrame(() => overrideButton.current?.focus());
    } catch (err) {
      const kind = costCentreErrorKind(err);
      const message = pickServerMessage((err as { response?: { data?: unknown } })?.response?.data, locale) ?? describeApiError(err, t);
      if (kind === 'locked') setLockedMessage(message);
      setFormError(message);
    } finally {
      setSaving(false);
    }
  };

  const valueNode = current
    ? <><bdi dir="ltr">{current.code}</bdi> — <bdi dir="auto">{ccName(current)}</bdi></>
    : invalidCode ? <bdi dir="ltr">{invalidCode}</bdi>
    : resolution.kind === 'unknown' && employee.costCenter ? <bdi dir="ltr">{employee.costCenter}</bdi>
    : <span aria-hidden="true">—</span>;

  const chip = chipText(resolution);

  return (
    <div role="group" aria-labelledby={labelId} data-testid="cc-emp-field" className="space-y-2">
      <p id={labelId} className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Cost centre')}</p>
      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2.5 dark:border-white/10 dark:bg-white/[0.03]">
        <span data-testid="cc-emp-value" className="font-medium text-slate-900 dark:text-white">{valueNode}</span>
        {resolution.kind === 'invalid' ? (
          <span data-testid="cc-emp-invalid-chip" className="inline-flex items-start gap-1 rounded-full bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
            <span>{invalidSentence(resolution.level, resolution.code)}</span>
          </span>
        ) : chip ? (
          <span data-testid="cc-emp-source" className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-semibold ${resolution.kind === 'none' ? 'bg-amber-50 text-amber-800 dark:bg-amber-500/10 dark:text-amber-300' : resolution.kind === 'employee' ? 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300' : 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200'}`}>
            {resolution.kind === 'none' && <AlertTriangle className="h-3.5 w-3.5" aria-hidden />}
            {chip}
          </span>
        ) : null}
      </div>

      {data.costingOn === false && (
        <p role="note" data-testid="cc-emp-costing-off-note" className="flex items-start gap-1.5 text-xs text-slate-600 dark:text-slate-300">
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t('Cost accounting is off, so this does not affect payroll yet.')}
        </p>
      )}

      {canManage ? (
        <div className="flex flex-wrap items-center gap-2">
          <button ref={overrideButton} type="button" data-testid="cc-emp-override-open" disabled={locked} onClick={() => openDialog('override')} className="btn-secondary min-h-8 px-3 text-sm">
            {overridden ? t('Change…') : t('Override…')}
          </button>
          {overridden && (
            <button type="button" data-testid="cc-emp-override-remove" disabled={locked} onClick={() => openDialog('remove')} className="btn-secondary min-h-8 px-3 text-sm">
              {t('Remove override')}
            </button>
          )}
          <button type="button" data-testid="cc-emp-why" aria-expanded={whyOpen} aria-controls={whyId} onClick={() => setWhyOpen((o) => !o)} className="ms-auto inline-flex min-h-8 items-center gap-1 rounded-md px-2 text-sm font-semibold text-sapphire hover:underline focus-visible:ring-2 focus-visible:ring-sapphire">
            {t('Why this cost centre?')}<ChevronDown className={`h-4 w-4 transition-transform ${whyOpen ? 'rotate-180' : ''}`} aria-hidden />
          </button>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <p role="note" data-testid="cc-emp-readonly-note" className="flex items-start gap-1.5 text-xs text-slate-600 dark:text-slate-300">
            <Lock className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t('Only HR managers and admins can change this.')}
          </p>
          <button type="button" data-testid="cc-emp-why" aria-expanded={whyOpen} aria-controls={whyId} onClick={() => setWhyOpen((o) => !o)} className="ms-auto inline-flex min-h-8 items-center gap-1 rounded-md px-2 text-sm font-semibold text-sapphire hover:underline focus-visible:ring-2 focus-visible:ring-sapphire">
            {t('Why this cost centre?')}<ChevronDown className={`h-4 w-4 transition-transform ${whyOpen ? 'rotate-180' : ''}`} aria-hidden />
          </button>
        </div>
      )}

      {locked && (
        <p role="alert" data-testid="cc-emp-locked-note" className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">{lockedMessage}</p>
      )}
      {status && <p role="status" className="text-xs font-semibold text-emerald-700 dark:text-emerald-300">{status}</p>}

      <div id={whyId} hidden={!whyOpen} data-testid="cc-emp-why-order" className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/5 dark:text-slate-200">
        <p>{t('Order: employee override, position, department, parent department, company default.')}</p>
        <ol className="mt-1.5 list-decimal space-y-0.5 ps-5">
          {STEPS.map((step) => (
            <li key={step.level} className={step.level === applyingLevel ? 'font-semibold' : undefined}>
              {t(step.label)}{step.level === applyingLevel && <> <span>{t('(applies)')}</span></>}
            </li>
          ))}
        </ol>
      </div>

      <Modal
        isOpen={dialog === 'override'}
        onClose={closeDialog}
        title={overridden ? t('Change cost centre override') : t('Override cost centre')}
        footer={<>
          <button type="button" onClick={closeDialog} disabled={saving} className="btn-secondary">{t('Cancel')}</button>
          <button type="button" data-testid="cc-emp-override-save" onClick={() => void submit()} disabled={saving} className="btn-primary !bg-blue-700 hover:!bg-blue-800">{saving ? t('Saving…') : t('Save')}</button>
        </>}
      >
        <div className="space-y-3">
          <p className="text-sm text-slate-600 dark:text-slate-300">
            {overridden || inherited.kind === 'none' || inherited.kind === 'unknown' || inherited.kind === 'invalid'
              ? t('Applies from the next Lock; locked runs do not change.')
              : replaces(inherited, inheritedCc?.code ?? '')}
          </p>
          <div>
            <label htmlFor={`${labelId}-select`} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Cost centre')} <span className="text-rose-500" aria-hidden>*</span></label>
            <select id={`${labelId}-select`} data-testid="cc-emp-override-select" value={pick} onChange={(e) => setPick(e.target.value)} className="select w-full" dir="auto">
              <option value="">{t('Select')}</option>
              {optionList.map((c) => <option key={c.id} value={c.id}>{c.code} — {ccName(c)}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor={`${labelId}-reason`} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason')} <span className="text-rose-500" aria-hidden>*</span></label>
            <input id={`${labelId}-reason`} data-testid="cc-emp-override-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} className="input w-full" />
          </div>
          <FieldError error={fieldError || formError} testId="cc-emp-dialog-error" />
        </div>
      </Modal>

      <Modal
        isOpen={dialog === 'remove'}
        onClose={closeDialog}
        title={t('Remove the override?')}
        footer={<>
          <button type="button" onClick={closeDialog} disabled={saving} className="btn-secondary">{t('Cancel')}</button>
          <button type="button" data-testid="cc-emp-remove-confirm" onClick={() => void submit()} disabled={saving} className="btn-primary !bg-blue-700 hover:!bg-blue-800">{saving ? t('Saving…') : t('Remove override')}</button>
        </>}
      >
        <div className="space-y-3">
          <p className="text-sm text-slate-600 dark:text-slate-300">
            {inheritedCc
              ? returnsTo(inherited, inheritedCc.code)
              : t('Removing it leaves this employee with no cost centre.')}
          </p>
          <div>
            <label htmlFor={`${labelId}-remove-reason`} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason')} <span className="text-rose-500" aria-hidden>*</span></label>
            <input id={`${labelId}-remove-reason`} data-testid="cc-emp-remove-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} className="input w-full" />
          </div>
          <FieldError error={fieldError || formError} testId="cc-emp-dialog-error" />
        </div>
      </Modal>
    </div>
  );
}
