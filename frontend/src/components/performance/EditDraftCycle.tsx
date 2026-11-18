'use client';

import { useEffect, useState } from 'react';
import { Field } from '../ui/Field';
import { Modal } from '../Modal';
import { useLocale } from '../../contexts/LocaleContext';
import { apiErrorReason } from '../../api/client';
import { cyclesApi, templatesApi } from '../../api/performance';
import type { PerformanceCycle, ScorecardTemplate } from '../../api/performance';
import { companiesApi, departmentsApi } from '../../api/organization';
import type { CompanyDto, DepartmentDto } from '../../api/organization';

/**
 * Edit a Draft cycle (PUT /api/performance/cycles/{id}; the server refuses any other status): its name and period, the
 * scorecard template every review is scored on (Launch refuses without one), and WHO it appraises: the company
 * (required where the organisation has several companies) and optionally one department of it. The deadlines and
 * stage switches are sent back unchanged.
 */
const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const ghost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const primary = 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50';

export function EditDraftCycle({ cycle, onSaved }: { cycle: PerformanceCycle; onSaved: () => void }) {
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className={ghost} onClick={() => setOpen(true)}>{t('Edit')}</button>
      {open && <EditDraftCycleModal cycle={cycle} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); onSaved(); }} />}
    </>
  );
}

function EditDraftCycleModal({ cycle, onClose, onSaved }: { cycle: PerformanceCycle; onClose: () => void; onSaved: () => void }) {
  const { t, locale } = useLocale();
  const [form, setForm] = useState({
    name: cycle.name, reviewPeriodStart: cycle.reviewPeriodStart?.slice(0, 10) ?? '', reviewPeriodEnd: cycle.reviewPeriodEnd?.slice(0, 10) ?? '',
    templateId: cycle.defaultScorecardTemplateId ?? '', companyId: cycle.companyId ?? '', departmentId: cycle.departmentId ?? '',
  });
  const [templates, setTemplates] = useState<ScorecardTemplate[]>([]);
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v, ...(k === 'companyId' ? { departmentId: '' } : {}) }));

  useEffect(() => {
    templatesApi.list().then(all => setTemplates(all.filter(x => x.isActive))).catch(() => setTemplates([]));
    companiesApi.listAll().then(setCompanies).catch(() => setCompanies([]));
    departmentsApi.listAll().then(setDepartments).catch(() => setDepartments([]));
  }, []);

  const companyDepartments = departments.filter(d => d.isActive && (!d.companyId || d.companyId === form.companyId));
  const companyName = (c: CompanyDto) => (locale === 'ar' && c.legalNameAr) ? c.legalNameAr : (c.tradeName || c.legalNameEn);
  const departmentName = (d: DepartmentDto) => (locale === 'ar' && d.nameAr) ? d.nameAr : d.nameEn;

  const save = async () => {
    if (!form.name.trim() || !form.reviewPeriodStart || !form.reviewPeriodEnd) { setError(t('Name and review period dates are required.')); return; }
    setSaving(true); setError('');
    try {
      await cyclesApi.update(cycle.id, {
        name: form.name.trim(), cycleType: cycle.cycleType, reviewPeriodStart: form.reviewPeriodStart, reviewPeriodEnd: form.reviewPeriodEnd,
        enableCalibration: cycle.enableCalibration, enable360Feedback: cycle.enable360Feedback, enableSelfAssessment: cycle.enableSelfAssessment,
        enableForcedDistribution: cycle.enableForcedDistribution, selfAssessmentDeadline: cycle.selfAssessmentDeadline ?? undefined,
        managerReviewDeadline: cycle.managerReviewDeadline ?? undefined, calibrationDeadline: cycle.calibrationDeadline ?? undefined,
        defaultScorecardTemplateId: form.templateId || undefined, notes: cycle.notes || undefined,
        companyId: form.companyId || undefined, departmentId: form.departmentId || undefined,
      });
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('The cycle could not be saved.'))); setSaving(false); }
  };

  return (
    <Modal isOpen title={t('Edit draft cycle — {name}', { name: cycle.name })} onClose={onClose} closeLabel={t('Close')} size="md">
      <div className="space-y-4">
        <Field label={t('Cycle Name *')} noInfo="the cycle's display name">
          <input className={inp} value={form.name} onChange={e => set('name', e.target.value)} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Period Start *')} noInfo="a plain date"><input type="date" className={inp} value={form.reviewPeriodStart} onChange={e => set('reviewPeriodStart', e.target.value)} /></Field>
          <Field label={t('Period End *')} noInfo="a plain date"><input type="date" className={inp} value={form.reviewPeriodEnd} onChange={e => set('reviewPeriodEnd', e.target.value)} /></Field>
        </div>
        <Field label={t('Scorecard template for this cycle')} info={t('Every review in the cycle is scored on this template. A cycle cannot be launched without one.')}>
          <select className={inp} value={form.templateId} onChange={e => set('templateId', e.target.value)}>
            <option value="">{t('Choose a template')}</option>
            {templates.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}
          </select>
        </Field>
        <Field label={t('Company appraised')} info={t('The cycle enrols the active employees of this company who joined by the end of the review period. Where your organisation has several companies, a cycle cannot be launched without one.')}>
          <select className={inp} value={form.companyId} onChange={e => set('companyId', e.target.value)}>
            <option value="">{t('Choose a company')}</option>
            {companies.map(c => <option key={c.id} value={c.id}>{companyName(c)}</option>)}
          </select>
        </Field>
        <Field label={t('Department (optional)')} info={t('Leave empty to appraise the whole company, or choose one department of it.')}>
          <select className={inp} value={form.departmentId} disabled={!form.companyId} onChange={e => set('departmentId', e.target.value)}>
            <option value="">{t('Whole company')}</option>
            {companyDepartments.map(d => <option key={d.id} value={d.id}>{departmentName(d)}</option>)}
          </select>
        </Field>
        {error && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={primary} onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Save')}</button>
        </div>
      </div>
    </Modal>
  );
}
