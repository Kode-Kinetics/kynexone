import { msg } from '../i18n/translations';
import type { AnswerInput, Question, TemplateContent } from '../api/engagement';

type Translator = (key: string, params?: Record<string, string | number>) => string;

/**
 * Engagement survey display rules, pure so they are unit-tested (unit/engagementDisplay.spec.ts).
 *
 * The server decides what is suppressed (CohortSuppression); the client's only job is to never show a number for a
 * hidden cell and to say why in plain words. A hidden cell renders the suppression sentence, not "0", "—" or "N/A".
 */

export const SUPPRESSED_SENTENCE = msg('Not enough responses to protect confidentiality (minimum {k}).');

export function suppressedText(t: Translator, k: number): string {
  return t(SUPPRESSED_SENTENCE, { k });
}

/** Why a cell is hidden, in the words a manager can act on. */
export function suppressionReasonText(t: Translator, reason: string | null | undefined, k: number): string {
  // The API gives one reason for every hidden value ('suppressed'): telling "too few people" apart from "a
  // neighbouring group is small" would itself say something about the counts.
  if (reason === 'suppressed') {
    return t('Hidden so that no group of fewer than {k} people can be worked out from this or any other result.', { k });
  }
  return suppressedText(t, k);
}

export type CellView = { kind: 'value'; text: string } | { kind: 'suppressed'; text: string };

/**
 * One metric cell. A hidden cell is ALWAYS the suppression sentence, even if a value somehow arrived with it — the
 * visible flag wins, so a server bug cannot leak a number through the UI.
 */
export function metricCell(
  t: Translator, k: number, visible: boolean, value: number | null | undefined, format: (n: number) => string,
): CellView {
  if (!visible) return { kind: 'suppressed', text: suppressedText(t, k) };
  if (value === null || value === undefined || Number.isNaN(value)) return { kind: 'value', text: t('No answers yet') };
  return { kind: 'value', text: format(value) };
}

/** The language-appropriate copy of a bilingual pair, falling back to the other language rather than blank. */
export function pick(locale: string, en: string | null | undefined, ar: string | null | undefined): string {
  const primary = locale === 'ar' ? ar : en;
  const fallback = locale === 'ar' ? en : ar;
  return (primary && primary.trim()) || (fallback ?? '') || '';
}

export const LIKERT_LABELS = [
  msg('Strongly disagree'), msg('Disagree'), msg('Neutral'), msg('Agree'), msg('Strongly agree'),
] as const;

export const MODE_LABELS = { Identified: msg('Identified'), Confidential: msg('Confidential'), Anonymous: msg('Anonymous') } as const;

export const SURVEY_STATUS_LABELS = {
  Draft: msg('Draft'), Scheduled: msg('Scheduled'), Open: msg('Open'), Closed: msg('Closed'), Cancelled: msg('Cancelled'),
} as const;

export const ACTION_STATUS_LABELS = {
  Open: msg('Open'), InProgress: msg('In progress'), Done: msg('Done'), Cancelled: msg('Cancelled'),
} as const;

export const MY_SURVEY_STATUS_LABELS = {
  NotStarted: msg('Not started'), Draft: msg('Draft saved'), Submitted: msg('Submitted'), Closed: msg('Closed'),
} as const;

export const DIMENSION_LABELS = {
  company: msg('Company'), department: msg('Department'), designation: msg('Designation'), grade: msg('Grade band'), tenure: msg('Tenure'),
} as const;

export const TENURE_LABELS: Record<string, string> = {
  lt1y: msg('Under 1 year'), '1to3y': msg('1–3 years'), '3to5y': msg('3–5 years'), '5yplus': msg('5 years or more'),
};

/** The respondent's in-progress answers, keyed by question. */
export type AnswerState = Record<string, { value?: number; choices?: string[]; text?: string }>;

export function toAnswers(state: AnswerState): AnswerInput[] {
  return Object.entries(state)
    .map(([questionKey, a]) => ({
      questionKey,
      value: a.value ?? null,
      choices: a.choices && a.choices.length > 0 ? a.choices : null,
      text: a.text && a.text.trim() ? a.text : null,
    }))
    .filter((a) => a.value !== null || a.choices !== null || a.text !== null);
}

export function fromAnswers(answers: AnswerInput[]): AnswerState {
  const state: AnswerState = {};
  for (const a of answers) {
    state[a.questionKey] = {
      value: a.value ?? undefined,
      choices: a.choices ?? undefined,
      text: a.text ?? undefined,
    };
  }
  return state;
}

export function isAnswered(q: Question, state: AnswerState): boolean {
  const a = state[q.key];
  if (!a) return false;
  if (q.type === 'likert5' || q.type === 'enps') return typeof a.value === 'number';
  if (q.type === 'single' || q.type === 'multi') return (a.choices?.length ?? 0) > 0;
  return !!a.text && a.text.trim().length > 0;
}

/** Required questions still unanswered, in order — the submit button says how many. */
export function missingRequired(content: TemplateContent, state: AnswerState): Question[] {
  return content.questions.filter((q) => q.required && !isAnswered(q, state));
}

export function progressPct(content: TemplateContent, state: AnswerState): number {
  if (content.questions.length === 0) return 0;
  return Math.round((100 * content.questions.filter((q) => isAnswered(q, state)).length) / content.questions.length);
}
