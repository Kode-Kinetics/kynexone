import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * The name of a pay line as the payroll run stored it, in the viewer's language. The run persists English names, some with
 * the working in brackets ("Overtime (5.00 h × (168.75 + 125.00 × 0.90)/h)", "Loss of Pay (1.00 d × 133.33/d)"). The name
 * is looked up in the dictionary (an unknown one prints as stored); the working stays Latin and is wrapped in a
 * left-to-right isolate, so in an Arabic line its brackets and numbers do not reorder. The payslip does the same.
 */
export function payItemLabel(t: T, label: string): string {
  const trimmed = label.trim();
  const whole = t(trimmed);
  if (whole !== trimmed) return whole;
  const bracket = trimmed.indexOf(' (');
  if (bracket > 0) {
    const head = trimmed.slice(0, bracket);
    const name = t(head);
    if (name !== head) return `${name} ⁦${trimmed.slice(bracket + 1)}⁩`;
  }
  return label;
}
