import { msg } from '../i18n/translations';

/**
 * The Qiwa data check (server: QiwaIntegrationService.MissingQiwaFields) reports a missing field as a snake_case code. The
 * screen names it in words, through the translator, and says plainly why a work location ID is asked for.
 */
const LABELS: Record<string, string> = {
  saudi_or_non_saudi: msg('Saudi / non-Saudi'),
  id_type: msg('ID type'),
  id_number: msg('ID number'),
  nationality: msg('Nationality'),
  occupation_code: msg('Occupation code'),
  establishment_id: msg('Establishment ID'),
  work_location_id: msg('Work location ID'),
  contract_reference: msg('Contract reference'),
};

export function qiwaFieldLabel(code: string, t: (key: string) => string): string {
  const key = LABELS[code];
  return key ? t(key) : code;
}

export function qiwaMissingText(codes: readonly string[], t: (key: string) => string): string {
  return codes.map(c => qiwaFieldLabel(c, t)).join(t(', '));
}

export const qiwaNeedsWorkLocationNote = (blocked: readonly { missingFields: readonly string[] }[]): boolean =>
  blocked.some(e => e.missingFields.includes('work_location_id'));

/**
 * Why the check asks for it. Qiwa identifies each work location of an establishment with its own number; the product's
 * branches carry a labour-office code, not that number, so it cannot be derived from the branch. It is entered per
 * employee, in the Qiwa section of the employee's record.
 */
export const QIWA_WORK_LOCATION_NOTE = msg(
  'Work location ID is the number Qiwa gives each work location of your establishment. Branches in KynexOne do not hold it yet, so it is not filled from the employee’s branch: enter it on each employee’s record, in the Qiwa section.',
);
