'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { CostCentreOption } from '../../lib/employeeCostCentre';

/**
 * A company's cost centres as `CODE — name` (from costCentreOptions). The follow option (value '') reads "Follow department
 * (CODE — name)"; without one, `placeholder` adds an empty "Select". The caller owns the <label htmlFor={id}>. `extra` keeps a
 * stored value that is no longer offered (an inactive override) visible instead of silently showing another option.
 */
export function CostCentreSelect({ id, value, onChange, options, placeholder, extra, followPending, testId, disabled, describedBy, invalid, className }: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  options: CostCentreOption[];
  placeholder?: string;
  extra?: { value: string; label: string } | null;
  /** What following gives is not known yet (the server is being asked): the follow option says only "Follow department". */
  followPending?: boolean;
  testId?: string;
  disabled?: boolean;
  describedBy?: string;
  invalid?: boolean;
  className?: string;
}) {
  const { t } = useLocale();
  const followLabel = (o: CostCentreOption) => (followPending ? t('Follow department') : o.label ? t('Follow department · {code}', { code: o.label }) : t('Follow department · no cost centre'));
  const showExtra = !!extra && extra.value !== '' && !options.some((o) => o.value === extra.value);
  return (
    <select
      id={id}
      data-testid={testId}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      disabled={disabled}
      aria-describedby={describedBy}
      aria-invalid={invalid || undefined}
      className={className ?? 'select w-full'}
      dir="auto"
    >
      {placeholder !== undefined && !options.some((o) => o.inherit) && <option value="">{placeholder}</option>}
      {options.map((o) => <option key={o.inherit ? '__follow' : o.value} value={o.value}>{o.inherit ? followLabel(o) : o.label}</option>)}
      {showExtra && extra && <option value={extra.value}>{extra.label}</option>}
    </select>
  );
}
