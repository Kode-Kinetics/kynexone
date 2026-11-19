'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Modal } from '../Modal';
import { Field, Input, Select, Textarea } from '../ui/Field';
import { useLocale } from '../../contexts/LocaleContext';
import type { UsageRequestInput, UtilisationRow } from '../../api/benefitUsage';
import {
  INFO_KEYS, calendarDate, emptyUsageForm, fill, measureText, newUsageId, refusalKey, usageKind, usageProblems,
  type UsageField, type UsageForm, type UsageProblems,
} from '../../lib/benefitUsage';

/**
 * Request a use of a fixed contract benefit, the way a loan is requested: HR for an employee, or the employee for themselves. Finance
 * approves it next. The benefits offered are the employee's rows that can take a request now; education asks for the child, the
 * academic year and the school; a benefit that covers family asks who it is for. Evidence is required. One request id per opening
 * of the dialog, so a double submit is the same request. Nothing is paid from here.
 */
export function RequestUsageDialog({ open, rows, today, onClose, onSubmit, forSelf = false }: {
  open: boolean;
  /** One employee's utilisation rows. */
  rows: UtilisationRow[];
  today: string;
  onClose: () => void;
  onSubmit: (input: UsageRequestInput) => Promise<void>;
  /** The employee asking for themselves (My benefits). */
  forSelf?: boolean;
}) {
  const { t, locale } = useLocale();
  const choices = useMemo(() => rows.filter((r) => r.canRequest), [rows]);
  const [form, setForm] = useState<UsageForm>(() => emptyUsageForm(today));
  const [problems, setProblems] = useState<UsageProblems>({});
  const [serverError, setServerError] = useState('');
  const [saving, setSaving] = useState(false);
  const requestId = useRef(newUsageId());

  // A fresh form and request id each time the dialog opens (keyed on the benefits offered, never on the array identity).
  const choiceKey = choices.map((c) => c.benefitCode).join('|');
  useEffect(() => {
    if (!open) return;
    requestId.current = newUsageId();
    const only = choiceKey.split('|').filter(Boolean);
    setForm({ ...emptyUsageForm(today), benefitCode: only.length === 1 ? only[0] : '' });
    setProblems({});
    setServerError('');
  }, [open, today, choiceKey]);

  const row = choices.find((r) => r.benefitCode === form.benefitCode);
  const kind = row ? usageKind(row.benefitCode) : 'other';
  const name = (r: UtilisationRow) => (locale === 'ar' ? r.labelAr : r.labelEn);
  const set = (patch: Partial<UsageForm>) => setForm((f) => ({ ...f, ...patch }));
  const err = (field: UsageField) => (problems[field] ? t(problems[field]!) : undefined);
  const people = row?.beneficiaries ?? [];
  const room = row?.maxDependants == null || people.filter((b) => b.dependantId && b.named).length < (row?.maxDependants ?? 0);

  const submit = async () => {
    const found = usageProblems(form, row);
    setProblems(found);
    setServerError('');
    if (Object.keys(found).length > 0 || !row) return;
    setSaving(true);
    try {
      const value = Number(form.quantity);
      await onSubmit({
        requestId: requestId.current, employeeId: row.employeeId, benefitCode: row.benefitCode, usageDate: form.usageDate,
        units: row.measure === 'Units' ? value : null, amount: row.measure === 'Amount' ? value : null,
        currency: row.measure === 'Amount' ? row.currency : null, evidenceReference: form.evidenceReference.trim(),
        dependantId: form.dependantId || null, academicYear: kind === 'education' ? form.academicYear.trim() : null,
        providerName: form.providerName.trim() || null, note: form.note.trim() || null,
      });
    } catch (e) {
      setServerError(t(refusalKey(e)));
    } finally {
      setSaving(false);
    }
  };

  const footer = (
    <div className="flex flex-wrap justify-end gap-2">
      <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
      <button type="button" className="btn-primary" onClick={() => void submit()} disabled={saving || choices.length === 0} data-testid="usage-request-submit">
        {saving ? t('Saving…') : t('Send request')}
      </button>
    </div>
  );

  return (
    <Modal isOpen={open} title={forSelf ? t('Request a benefit') : t('Request a benefit for an employee')} onClose={onClose} footer={footer} size="lg"
      closeLabel={t('Close')} closeDisabled={saving}>
      <div className="space-y-4" data-testid="usage-request-dialog">
        <p className="text-sm text-fg-muted">{t('Give the evidence for what was used. Finance approves the request next. Nothing is paid from here.')}</p>
        {choices.length === 0
          ? <p role="status" className="text-sm text-fg">{t('Nothing can be requested now: every benefit is used, not yet confirmed, or its period has ended.')}</p>
          : <>
            <Field label={t('Benefit')} required noInfo="the list is the employee's own fixed package" error={err('benefitCode')}>
              <Select value={form.benefitCode} onChange={(e) => set({ benefitCode: e.target.value, dependantId: '', quantity: '' })}>
                <option value="">{t('Choose a benefit')}</option>
                {choices.map((r) => <option key={r.benefitCode} value={r.benefitCode}>{name(r)}</option>)}
              </Select>
            </Field>
            {row && (
              <p className="rounded-lg bg-surface-sunken px-3 py-2 text-sm text-fg" data-testid="usage-request-left">
                {fill(t('Left this period ({from} to {to}): {left} of {total}'), {
                  from: calendarDate(row.periodStart, locale), to: calendarDate(row.periodEnd, locale),
                  left: measureText(row.remaining, row.measure, row.currency, locale), total: measureText(row.entitlement, row.measure, row.currency, locale),
                })}
              </p>
            )}
            {row && people.length > 0 && (
              <Field label={kind === 'education' ? t('Child') : t('Who it is for')} required={kind === 'education'} error={err('dependantId')}
                info={kind === 'education' ? INFO_KEYS.child : INFO_KEYS.traveller}>
                <Select value={form.dependantId} onChange={(e) => set({ dependantId: e.target.value })}>
                  {kind === 'education' && <option value="">{t('Choose a child')}</option>}
                  {people.map((b) => (
                    <option key={b.dependantId ?? 'self'} value={b.dependantId ?? ''} disabled={b.remaining <= 0 || (b.dependantId !== null && !b.named && !room)}>
                      {fill(t('{name} — {left} left'), { name: b.dependantId ? b.name : fill(t('{name} (employee)'), { name: b.name }), left: measureText(b.remaining, row.measure, row.currency, locale) })}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={kind === 'ticket' ? t('Travel date') : kind === 'education' ? t('Claim date') : t('Date')} required error={err('usageDate')}
                noInfo="a plain date inside the benefit period">
                <Input type="date" value={form.usageDate} min={row?.periodStart} max={row?.periodEnd} onChange={(e) => set({ usageDate: e.target.value })} />
              </Field>
              <Field label={row?.measure === 'Amount' ? fill(t('Amount ({currency})'), { currency: row.currency ?? '' }) : t('Number of tickets or items')} required
                error={err('quantity')} noInfo="the limit is shown above the field">
                <Input type="number" inputMode="decimal" min="0" step={row?.measure === 'Amount' ? '0.01' : '1'} value={form.quantity} dir="ltr"
                  onChange={(e) => set({ quantity: e.target.value })} />
              </Field>
            </div>
            {kind === 'education' && (
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t('Academic year')} required error={err('academicYear')} hint={t('For example 2026/2027')} noInfo="a label the school uses">
                  <Input value={form.academicYear} maxLength={20} dir="ltr" onChange={(e) => set({ academicYear: e.target.value })} />
                </Field>
                <Field label={t('School')} noInfo="the name on the invoice">
                  <Input value={form.providerName} maxLength={200} onChange={(e) => set({ providerName: e.target.value })} />
                </Field>
              </div>
            )}
            <Field label={t('Evidence reference')} required error={err('evidenceReference')} info={INFO_KEYS.evidence} hint={t('Invoice, receipt or booking number')}>
              <Input value={form.evidenceReference} maxLength={200} dir="ltr" onChange={(e) => set({ evidenceReference: e.target.value })} />
            </Field>
            <Field label={t('Note (optional)')} noInfo="free text for Finance">
              <Textarea value={form.note} maxLength={1000} rows={2} onChange={(e) => set({ note: e.target.value })} />
            </Field>
          </>}
        {serverError && <p role="alert" className="text-sm text-danger-fg" data-testid="usage-request-error">{serverError}</p>}
      </div>
    </Modal>
  );
}
