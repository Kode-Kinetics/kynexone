'use client';

import { useLocale } from '@/src/contexts/LocaleContext';
import { hijriLine } from '@/src/lib/hire/dates';
import { FieldShell, inputClass } from './FieldShell';
import { fieldId } from './FieldShell';

/** A native date input (calendar and keyboard come with it) with its Umm al-Qura date beside it. */
export function DateField({ field, label, value, onChange, onBlur, required, error, hint, min, max, disabled }: {
  field: string; label: string; value: string; onChange: (v: string) => void; onBlur?: () => void;
  required?: boolean; error?: string; hint?: string; min?: string; max?: string; disabled?: boolean;
}) {
  const { locale, t } = useLocale();
  const hijri = hijriLine(value, locale);
  return (
    <FieldShell field={field} label={label} required={required} error={error} hint={hint}
      extra={{ id: `${fieldId(field)}-hijri`, node: hijri ? <span data-testid={`hijri-${field}`} dir="auto">{t('Hijri: {date}', { date: hijri })}</span> : null }}>
      {p => <input {...p} type="date" dir="ltr" value={value} min={min} max={max} disabled={disabled} onChange={e => onChange(e.target.value)} onBlur={onBlur} className={`${inputClass} text-start`} />}
    </FieldShell>
  );
}
