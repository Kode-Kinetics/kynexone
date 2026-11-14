/**
 * Approval queue text that the API sends as raw codes or English fragments.
 *
 * WHY. The queue printed the entity class name ("EmployeeChangeRequest"), the routing queue
 * ("Role:HR Manager"), the role hint and the changed-field list ("bank name, IBAN") exactly as the
 * API stores them, in both languages. These helpers turn them into readable, translated text and
 * fall back to the original wording for a value they do not know, so nothing disappears.
 */
import { enumLabel } from '../i18n/enumLabel';
import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

/** "EmployeeChangeRequest" → "Employee change request" (translated when the entity is known). */
export function approvalEntityLabel(t: Translator, entityName: string | null | undefined): string {
  return enumLabel(t, 'ApprovalEntity', entityName);
}

/** The role name as the tenant's roles are spelled, translated when the dictionary knows it. */
function roleName(t: Translator, role: string): string {
  return t(role);
}

/** "Role:HR Manager" → "HR Manager"; "Manager:Sara Ali" → "Sara Ali"; anything else unchanged. */
export function approvalQueueLabel(t: Translator, queue: string | null | undefined): string {
  const q = (queue ?? '').trim();
  const at = q.indexOf(':');
  if (at <= 0) return q;
  const kind = q.slice(0, at).trim();
  const rest = q.slice(at + 1).trim();
  if (!rest) return q;
  return kind.toLowerCase() === 'role' ? roleName(t, rest) : rest;
}

/** "Role · HR Manager" in the viewer's language. */
export function approvalOwnerMeta(t: Translator, approverType: string | null | undefined, approverRole: string | null | undefined): string {
  const parts = [approverType?.trim() ? t(approverType.trim()) : '', approverRole?.trim() ? roleName(t, approverRole.trim()) : ''].filter(Boolean);
  return parts.join(' · ') || t('No route metadata');
}

/** "bank name, IBAN" → each field translated; an unknown field keeps its own wording. */
export function approvalChangeSummary(t: Translator, summary: string | null | undefined): string {
  if (!summary) return '';
  return summary.split(',').map(part => {
    const token = part.trim();
    if (!token) return '';
    const key = `approval.field.${token.toLowerCase()}`;
    const label = t(key);
    return label === key ? token : label;
  }).filter(Boolean).join(t('approval.list_separator'));
}

const TITLE_PREFIXES: Array<[string, string]> = [
  ['Employee change approval - ', 'Employee change approval - {subject}'],
];

/** A server-built approval title: the known sentence is translated, the employee or record name is kept. */
export function approvalTitleLabel(t: Translator, title: string | null | undefined): string {
  const text = (title ?? '').trim();
  for (const [prefix, key] of TITLE_PREFIXES) {
    if (text.startsWith(prefix)) return t(key, { subject: text.slice(prefix.length) });
  }
  return text;
}
