'use client';

import { useState } from 'react';
import { Modal } from '../Modal';
import { Field, Input, Select, Textarea } from '../ui/Field';
import { EmployeeSearchSelect } from '../EmployeeSearchSelect';
import type { EmployeeSelection } from '../EmployeeSearchSelect';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { employeesApi } from '../../api/employees';
import { probationApi } from '../../api/performance';
import type { ProbationReview } from '../../api/performance';
import { appraisalRefusal } from '../../lib/appraisalsRefusal';
import { PROBATION_RECOMMENDATIONS } from '../../lib/appraisalsUi';

const day = (value?: string | null) => (value ?? '').slice(0, 10);

/**
 * HR opens a probation review for someone nearing the end of probation. The employee's own probation dates are filled in
 * from their record, so HR confirms them rather than typing them. The manager then writes the review.
 */
export function NewProbationModal({ onClose, onSaved }: { onClose: () => void; onSaved: (review: ProbationReview) => void }) {
  const t = useT();
  const { locale } = useLocale();
  const [employee, setEmployee] = useState<EmployeeSelection | null>(null);
  const [form, setForm] = useState({ start: '', end: '', due: '' });
  const [prefilled, setPrefilled] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const pick = (selection: EmployeeSelection | null) => {
    setEmployee(selection); setPrefilled(false); setError('');
    if (!selection) return;
    // Best effort: a failed read just leaves the dates for HR to type.
    employeesApi.get(selection.intId).then(e => {
      const start = day(e.probationStartDate) || day(e.joiningDate);
      const end = day(e.probationEndDate);
      setForm(f => ({ ...f, start: start || f.start, end: end || f.end }));
      setPrefilled(!!(start || end));
    }).catch(() => undefined);
  };

  const save = async () => {
    if (!employee) { setError(t('apr.probation.pickEmployee')); return; }
    if (!form.start || !form.end) { setError(t('apr.probation.datesRequired')); return; }
    if (form.end < form.start) { setError(t('apr.error.invalid_date_range')); return; }
    setSaving(true); setError('');
    try {
      const review = await probationApi.create({
        employeeId: employee.intId, employeeName: employee.fullName, departmentName: employee.department, designationTitle: '',
        probationStartDate: form.start, probationEndDate: form.end, reviewDueDate: form.due || undefined,
      });
      onSaved(review);
    } catch (e) { setError(appraisalRefusal(e, t, t('apr.probation.createFailed'), locale)); setSaving(false); }
  };

  return (
    <Modal isOpen title={t('apr.probation.newTitle')} onClose={onClose} closeLabel={t('Close')} size="md"
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" disabled={saving} aria-busy={saving || undefined} onClick={save}>{saving ? t('Creating…') : t('apr.probation.create')}</button>
        </div>
      )}>
      <div className="space-y-4" data-testid="new-probation">
        <p className="text-body text-fg-muted" data-purpose>{t('apr.probation.newPurpose')}</p>
        <Field label={t('Employee *')} required noInfo="a person picker, the name says what it is">
          <EmployeeSearchSelect value={employee} onChange={pick} required />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('apr.probation.start')} required noInfo="a plain date" hint={prefilled ? t('apr.probation.fromRecord') : undefined}>
            <Input type="date" value={form.start} onChange={e => set('start', e.target.value)} />
          </Field>
          <Field label={t('apr.probation.end')} required info={t('apr.probation.endInfo')}>
            <Input type="date" value={form.end} onChange={e => set('end', e.target.value)} />
          </Field>
        </div>
        <Field label={t('apr.probation.due')} noInfo="a plain date, optional" hint={t('apr.probation.dueHint')}>
          <Input type="date" value={form.due} onChange={e => set('due', e.target.value)} />
        </Field>
        {error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
      </div>
    </Modal>
  );
}

/** The line manager's probation review: how the person did, and what the manager recommends. HR makes the decision. */
export function ProbationManagerReviewModal({ review, onClose, onSaved }: { review: ProbationReview; onClose: () => void; onSaved: (review: ProbationReview) => void }) {
  const t = useT();
  const { locale } = useLocale();
  const [form, setForm] = useState({ summary: '', rating: '', recommendation: '', notes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.summary.trim()) { setError(t('apr.probation.summaryRequired')); return; }
    if (!form.rating) { setError(t('apr.probation.ratingRequired')); return; }
    if (!form.recommendation) { setError(t('apr.probation.recommendationRequired')); return; }
    setSaving(true); setError('');
    try {
      const saved = await probationApi.managerReview(review.id, {
        performanceSummary: form.summary.trim(), overallRating: Number(form.rating), recommendation: form.recommendation, notes: form.notes.trim() || undefined,
      });
      onSaved(saved);
    } catch (e) { setError(appraisalRefusal(e, t, t('apr.probation.reviewFailed'), locale)); setSaving(false); }
  };

  return (
    <Modal isOpen title={t('apr.probation.reviewTitle', { name: review.employeeName })} onClose={onClose} closeLabel={t('Close')} size="md"
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" disabled={saving} aria-busy={saving || undefined} onClick={save}>{saving ? t('Submitting…') : t('apr.probation.submitReview')}</button>
        </div>
      )}>
      <div className="space-y-4" data-testid="probation-review-form">
        <p className="text-body text-fg-muted" data-purpose>{t('apr.probation.reviewPurpose')}</p>
        <p className="text-meta text-fg-muted">{t('apr.probation.period', { start: day(review.probationStartDate), end: day(review.probationEndDate) })}</p>
        <Field label={t('apr.probation.summary')} required noInfo="free text">
          <Textarea rows={4} value={form.summary} onChange={e => set('summary', e.target.value)} placeholder={t('apr.probation.summaryPlaceholder')} />
        </Field>
        <Field label={t('apr.probation.rating')} required info={t('apr.probation.ratingInfo')}>
          <Select value={form.rating} onChange={e => set('rating', e.target.value)}>
            <option value="">{t('apr.probation.ratingChoose')}</option>
            {[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{t(`apr.probation.rating.${n}`)}</option>)}
          </Select>
        </Field>
        <fieldset className="space-y-2">
          <legend className="text-body font-medium text-fg">{t('apr.probation.recommendation')} <span aria-hidden>*</span></legend>
          <p className="text-meta text-fg-muted">{t('apr.probation.recommendationHint')}</p>
          <div className="grid gap-2 sm:grid-cols-3">
            {PROBATION_RECOMMENDATIONS.map(r => (
              <label key={r} className={`flex min-h-control-md cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-body ${form.recommendation === r ? 'border-primary bg-primary-soft text-fg' : 'border-line text-fg-muted'}`}>
                <input type="radio" name="probation-recommendation" value={r} checked={form.recommendation === r} onChange={() => set('recommendation', r)} />
                {t(`apr.probation.rec.${r}`)}
              </label>
            ))}
          </div>
        </fieldset>
        <Field label={t('Notes')} noInfo="free text, optional">
          <Textarea rows={3} value={form.notes} onChange={e => set('notes', e.target.value)} />
        </Field>
        {error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
      </div>
    </Modal>
  );
}
