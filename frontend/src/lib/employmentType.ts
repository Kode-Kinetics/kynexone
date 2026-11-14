import { enumLabel } from '../i18n/enumLabel';
import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/** An employment type as stored ("full_time", "Full-Time") in the viewer's language; an unknown one reads as plain words. */
export function employmentTypeLabel(t: T, value: string | null | undefined): string {
  return enumLabel(t, 'EmploymentType', value);
}
