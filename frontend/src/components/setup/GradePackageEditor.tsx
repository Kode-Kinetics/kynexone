'use client';

import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, Plus, Trash2 } from 'lucide-react';
import {
  gradesApi,
  type GradePackageComponentCreate, type GradePackageComponentOption, type GradePayScaleComponentRequest,
} from '../../api/organization';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { useLocale } from '../../contexts/LocaleContext';
import { isPercent, packageTotals, rangeStatus } from '../../lib/gradePackage';
import { msg } from '../../i18n/translations';

type ComponentType = GradePackageComponentOption['componentType'];
const TYPE_GROUPS: { type: ComponentType; label: string }[] = [
  { type: 'Earning', label: msg('Earnings') },
  { type: 'Benefit', label: msg('Benefits') },
  { type: 'Deduction', label: msg('Deductions') },
];

const blankLine = (sortOrder: number): GradePayScaleComponentRequest => ({
  componentCode: '', componentName: '', componentType: 'Earning', calculationType: 'Fixed',
  amount: 0, percentage: 0, isTaxable: false, frequency: 'Monthly', sortOrder, isActive: true,
});

/**
 * A grade's pay package (Setup → Grades): each line picks a component from the grade company's pay component catalogue —
 * code, name, type and taxable come from there — and sets Fixed or % of basic and the amount. "+ New component" adds a
 * component to the catalogue (this company) and puts it on the line. The gross monthly cash is shown under the lines, with
 * a warning when it falls outside the grade's Min–Max. Saving is the caller's; blank rows are stripped there.
 */
export function GradePackageEditor({ companyId, components, onChange, minSalary, maxSalary, currency, onCatalogue }: {
  companyId?: string | null;
  components: GradePayScaleComponentRequest[];
  onChange: (c: GradePayScaleComponentRequest[]) => void;
  minSalary?: number;
  maxSalary?: number;
  currency?: string;
  /** The loaded catalogue, so the caller can check the lines before saving. */
  onCatalogue?: (options: GradePackageComponentOption[]) => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const { locale } = useLocale();
  const [catalogue, setCatalogue] = useState<GradePackageComponentOption[]>([]);
  const [loadFailed, setLoadFailed] = useState(false);
  // The "+ New component" form: open for a line (its index) or for a new line (-1).
  const [creatingFor, setCreatingFor] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoadFailed(false);
    gradesApi.packageComponents(companyId)
      .then((rows) => { if (!cancelled) { setCatalogue(rows); onCatalogue?.(rows); } })
      .catch(() => { if (!cancelled) setLoadFailed(true); });
    return () => { cancelled = true; };
    // onCatalogue is a setter from the caller; reloading on its identity would loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [companyId]);

  const byCode = useMemo(() => new Map(catalogue.map(c => [c.code.toUpperCase(), c])), [catalogue]);
  const label = (c: GradePackageComponentOption) => (locale === 'ar' ? c.nameAr || c.nameEn : c.nameEn);
  const update = (i: number, patch: Partial<GradePayScaleComponentRequest>) =>
    onChange(components.map((c, idx) => (idx === i ? { ...c, ...patch } : c)));
  const choose = (i: number, option: GradePackageComponentOption | undefined) => update(i, option
    ? { componentCode: option.code, componentName: option.nameEn, componentType: option.componentType, isTaxable: option.isTaxable,
        ...(option.code === 'BASIC' ? { calculationType: 'Fixed', percentage: 0 } : {}) }
    : { componentCode: '', componentName: '' });
  const remove = (i: number) => onChange(components.filter((_, idx) => idx !== i));
  const add = () => onChange([...components, blankLine(components.length + 1)]);
  const used = new Set(components.map(c => (c.componentCode ?? '').toUpperCase()).filter(Boolean));

  const created = (option: GradePackageComponentOption) => {
    const next = [...catalogue, option];
    setCatalogue(next);
    onCatalogue?.(next);
    const target = creatingFor ?? -1;
    setCreatingFor(null);
    const line: Partial<GradePayScaleComponentRequest> = {
      componentCode: option.code, componentName: option.nameEn, componentType: option.componentType, isTaxable: option.isTaxable,
    };
    if (target >= 0 && target < components.length) update(target, line);
    else onChange([...components, { ...blankLine(components.length + 1), ...line }]);
  };

  const totals = packageTotals(components);
  const status = rangeStatus(totals.gross, minSalary, maxSalary);
  const money = (n: number) => fmt.money(n, currency || null);
  const hasNonMonthly = components.some(c => c.componentCode && (c.componentType === 'Benefit' || c.frequency === 'Annual'));

  return (
    <div className="mt-3" data-testid="grade-package-editor">
      <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
        <span className="text-xs font-medium text-slate-600 dark:text-slate-300">{t('Pay package')}</span>
        <div className="flex gap-1.5">
          <button type="button" onClick={() => setCreatingFor(-1)} className="btn-secondary h-6 px-2 text-xs"><Plus className="h-3 w-3" aria-hidden="true" /> {t('New component')}</button>
          <button type="button" onClick={add} className="btn-secondary h-6 px-2 text-xs"><Plus className="h-3 w-3" aria-hidden="true" /> {t('Add line')}</button>
        </div>
      </div>
      <p className="mb-2 text-xs text-slate-500 dark:text-slate-400">{t("Pick each line from this company's pay components, then set a fixed amount or a percentage of basic.")}</p>
      {loadFailed && <p role="alert" className="mb-2 text-xs text-rose-700 dark:text-rose-300">{t('Pay components could not be loaded. Close the grade and open it again.')}</p>}

      {components.length === 0 ? (
        <p className="py-2 text-xs text-slate-500 dark:text-slate-400">{t('No lines yet. Add Basic first, then the allowances this grade gets.')}</p>
      ) : (
        <div className="space-y-1.5">
          {components.map((c, i) => {
            const code = (c.componentCode ?? '').toUpperCase();
            const option = code ? byCode.get(code) : undefined;
            const unknown = !!code && catalogue.length > 0 && !option;
            return (
              <div key={i} data-testid="grade-package-line">
                <div className="grid grid-cols-12 items-center gap-1.5">
                  <select aria-label={t('Pay component')} title={t('Pay component')} value={code}
                    onChange={(e) => choose(i, byCode.get(e.target.value))} className="select col-span-5 h-8 text-xs">
                    <option value="">{t('Choose a component…')}</option>
                    {unknown && <option value={code} disabled>{t('{code}: not in the list', { code })}</option>}
                    {TYPE_GROUPS.map(group => {
                      const rows = catalogue.filter(o => o.componentType === group.type);
                      return rows.length === 0 ? null : (
                        <optgroup key={group.type} label={t(group.label)}>
                          {rows.map(o => (
                            <option key={o.code} value={o.code.toUpperCase()} disabled={used.has(o.code.toUpperCase()) && o.code.toUpperCase() !== code}>{label(o)}</option>
                          ))}
                        </optgroup>
                      );
                    })}
                  </select>
                  <select aria-label={t('Calculation type')} title={t('Calculation type')} value={isPercent(c.calculationType) ? 'PercentOfBasic' : 'Fixed'}
                    onChange={(e) => update(i, { calculationType: e.target.value })} className="select col-span-3 h-8 text-xs">
                    <option value="Fixed">{t('Fixed')}</option>
                    <option value="PercentOfBasic" disabled={code === 'BASIC'}>{t('% of Basic')}</option>
                  </select>
                  {isPercent(c.calculationType)
                    ? <input aria-label={t('Percentage of basic')} type="number" min={0} max={100} step="0.01" value={c.percentage} onChange={(e) => update(i, { percentage: Number(e.target.value) })} className="input col-span-3 h-8 text-xs" placeholder="%" />
                    : <input aria-label={t('Amount')} type="number" min={0} step="0.01" value={c.amount} onChange={(e) => update(i, { amount: Number(e.target.value) })} className="input col-span-3 h-8 text-xs" placeholder={t('Amount')} />}
                  <button type="button" onClick={() => remove(i)} aria-label={t('Remove component')} className="col-span-1 grid h-8 place-items-center rounded-md border border-slate-200 text-slate-500 hover:border-rose-300 hover:text-rose-600 dark:border-white/10"><Trash2 className="h-3.5 w-3.5" aria-hidden="true" /></button>
                </div>
                {option && (option.isTaxable || c.frequency === 'Annual' || option.componentType !== 'Earning') && (
                  <p className="mt-0.5 flex gap-2 text-[11px] text-slate-500 dark:text-slate-400">
                    {option.componentType === 'Benefit' && <span>{t('Benefit')}</span>}
                    {option.componentType === 'Deduction' && <span>{t('Deduction')}</span>}
                    {option.isTaxable && <span>{t('Taxable')}</span>}
                    {c.frequency === 'Annual' && <span>{t('Annual')}</span>}
                  </p>
                )}
                {unknown && (
                  <p role="alert" className="mt-0.5 text-[11px] text-amber-800 dark:text-amber-200">
                    {t("{code} is not one of this company's pay components. Choose a component, or add it as a new one.", { code })}{' '}
                    <button type="button" onClick={() => setCreatingFor(i)} className="font-medium text-sapphire underline">{t('Add as new component')}</button>
                  </p>
                )}
                {creatingFor === i && (
                  <NewComponentForm companyId={companyId} initialName={c.componentName || code} initialType={(c.componentType as ComponentType) || 'Earning'}
                    onCancel={() => setCreatingFor(null)} onCreated={created} />
                )}
              </div>
            );
          })}
        </div>
      )}
      {creatingFor === -1 && <NewComponentForm companyId={companyId} onCancel={() => setCreatingFor(null)} onCreated={created} />}

      <div className="mt-3 rounded-md bg-slate-50 px-3 py-2 text-xs dark:bg-white/5" data-testid="grade-package-total">
        <p className="font-semibold text-slate-800 dark:text-slate-100">{t('Gross monthly cash: {amount}', { amount: money(totals.gross) })}</p>
        {totals.deductions > 0 && <p className="text-slate-600 dark:text-slate-300">{t('Monthly deductions: {amount}', { amount: money(totals.deductions) })}</p>}
        {hasNonMonthly && <p className="text-slate-500 dark:text-slate-400">{t('Benefits and annual lines are not counted in the monthly total.')}</p>}
        {status && (
          <p role="status" className="mt-1 flex items-center gap-1 text-amber-800 dark:text-amber-200" data-testid="grade-package-range-warning">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            {status === 'below'
              ? t("Below this grade's minimum of {amount}.", { amount: money(minSalary ?? 0) })
              : t("Above this grade's maximum of {amount}.", { amount: money(maxSalary ?? 0) })}
          </p>
        )}
        <p className="mt-1 text-slate-500 dark:text-slate-400">{t("Payroll uses this package for the grade's salary structure. It is updated when you save.")}</p>
      </div>
    </div>
  );
}

/** "+ New component": adds a grade-package component to the company's catalogue and hands it back. */
function NewComponentForm({ companyId, initialName = '', initialType = 'Earning', onCancel, onCreated }: {
  companyId?: string | null;
  initialName?: string;
  initialType?: ComponentType;
  onCancel: () => void;
  onCreated: (option: GradePackageComponentOption) => void;
}) {
  const t = useT();
  const [form, setForm] = useState<GradePackageComponentCreate>({ nameEn: initialName, nameAr: '', componentType: initialType, isTaxable: false });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const submit = async () => {
    if (!form.nameEn.trim()) { setError(t("Enter the component's name in English.")); return; }
    setSaving(true); setError('');
    try {
      onCreated(await gradesApi.createPackageComponent(companyId, { ...form, nameEn: form.nameEn.trim(), nameAr: form.nameAr?.trim() || undefined }));
    } catch (err: unknown) {
      const data = (err as { response?: { data?: { message?: string; errors?: Record<string, string> } } })?.response?.data;
      setError(data?.message ?? Object.values(data?.errors ?? {})[0] ?? t('The component could not be added. Try again.'));
      setSaving(false);
    }
  };
  return (
    <div className="mt-2 rounded-md border border-sapphire/30 bg-sapphire/[0.03] p-2.5" data-testid="new-pay-component">
      <p className="mb-1.5 text-xs font-semibold text-slate-800 dark:text-slate-100">{t('New pay component')}</p>
      <div className="grid grid-cols-2 gap-1.5">
        <input aria-label={t('Name (English)')} placeholder={t('Name (English)')} value={form.nameEn} onChange={(e) => setForm(f => ({ ...f, nameEn: e.target.value }))} className="input h-8 text-xs" />
        <input aria-label={t('Name (Arabic, optional)')} placeholder={t('Name (Arabic, optional)')} dir="rtl" lang="ar" value={form.nameAr ?? ''} onChange={(e) => setForm(f => ({ ...f, nameAr: e.target.value }))} className="input h-8 text-xs" />
        <select aria-label={t('Type')} title={t('Type')} value={form.componentType} onChange={(e) => setForm(f => ({ ...f, componentType: e.target.value as ComponentType, isTaxable: e.target.value === 'Earning' && f.isTaxable }))} className="select h-8 text-xs">
          <option value="Earning">{t('Earning')}</option>
          <option value="Benefit">{t('Benefit')}</option>
          <option value="Deduction">{t('Deduction')}</option>
        </select>
        <label className="flex h-8 items-center gap-2 text-xs text-slate-700 dark:text-slate-300">
          <input type="checkbox" checked={form.isTaxable} disabled={form.componentType !== 'Earning'} onChange={(e) => setForm(f => ({ ...f, isTaxable: e.target.checked }))} className="h-4 w-4 accent-sapphire" />
          {t('Taxable income')}
        </label>
      </div>
      <p className="mt-1.5 text-[11px] text-slate-500 dark:text-slate-400">{t("It is added to this company's pay components, so other grades can use it too. Each grade sets its own amount.")}</p>
      {error && <p role="alert" className="mt-1 text-[11px] text-rose-700 dark:text-rose-300">{error}</p>}
      <div className="mt-2 flex justify-end gap-1.5">
        <button type="button" onClick={onCancel} className="btn-secondary h-7 px-2 text-xs">{t('Cancel')}</button>
        <button type="button" onClick={submit} disabled={saving} className="btn-primary h-7 px-2 text-xs disabled:opacity-60">{saving ? t('Adding…') : t('Add to the list')}</button>
      </div>
    </div>
  );
}
