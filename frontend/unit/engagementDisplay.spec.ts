import { expect, test } from '@playwright/test';
import {
  fromAnswers, metricCell, missingRequired, pick, progressPct, suppressedText, suppressionReasonText, toAnswers,
} from '../src/lib/engagementDisplay';
import type { TemplateContent } from '../src/api/engagement';
import { translate } from '../src/i18n/translations';

const en = (key: string, params?: Record<string, string | number>) => translate('en', key, params);
const ar = (key: string, params?: Record<string, string | number>) => translate('ar', key, params);

test.describe('engagement suppression rendering', () => {
  test('a hidden cell renders the suppression sentence with k, never a number', () => {
    const cell = metricCell(en, 5, false, 62.4, (n) => `${n}%`);
    expect(cell).toEqual({ kind: 'suppressed', text: 'Not enough responses to protect confidentiality (minimum 5).' });
  });

  test('the visible flag wins even if a value arrived with a hidden cell', () => {
    const cell = metricCell(en, 7, false, 0, (n) => `${n}`);
    expect(cell.kind).toBe('suppressed');
    expect(cell.text).not.toContain('0');
    expect(cell.text).toContain('7');
  });

  test('a visible cell is formatted; a visible empty cell says so instead of 0', () => {
    expect(metricCell(en, 5, true, 77.2, (n) => `${n}%`)).toEqual({ kind: 'value', text: '77.2%' });
    expect(metricCell(en, 5, true, null, (n) => `${n}`)).toEqual({ kind: 'value', text: 'No answers yet' });
  });

  test('the sentence and every reason exist in Arabic and keep k', () => {
    const text = suppressedText(ar, 5);
    expect(text).not.toBe('Not enough responses to protect confidentiality (minimum 5).');
    expect(text).toContain('5');
    for (const reason of ['suppressed', null]) {
      const english = suppressionReasonText(en, reason, 5);
      const arabic = suppressionReasonText(ar, reason, 5);
      expect(arabic).not.toBe(english);
      if (english.includes('5')) expect(arabic).toContain('5');
    }
  });
});

test.describe('respondent answers', () => {
  const content: TemplateContent = {
    drivers: [],
    questions: [
      { key: 'q1', type: 'likert5', textEn: 'A', textAr: 'أ', required: true },
      { key: 'q2', type: 'enps', textEn: 'B', textAr: 'ب', required: true },
      { key: 'q3', type: 'multi', textEn: 'C', textAr: 'ج', required: false, options: [{ key: 'x', labelEn: 'X', labelAr: 'س' }, { key: 'y', labelEn: 'Y', labelAr: 'ص' }] },
      { key: 'q4', type: 'text', textEn: 'D', textAr: 'د', required: false },
    ],
  };

  test('missing required questions and progress', () => {
    expect(missingRequired(content, {}).map((q) => q.key)).toEqual(['q1', 'q2']);
    expect(missingRequired(content, { q1: { value: 4 }, q2: { value: 0 } })).toEqual([]);
    expect(progressPct(content, { q1: { value: 4 }, q2: { value: 0 } })).toBe(50);
  });

  test('a zero on eNPS is an answer, blank text is not', () => {
    const answers = toAnswers({ q2: { value: 0 }, q4: { text: '   ' }, q3: { choices: [] } });
    expect(answers).toEqual([{ questionKey: 'q2', value: 0, choices: null, text: null }]);
  });

  test('a draft round-trips', () => {
    const state = { q1: { value: 5 }, q3: { choices: ['x', 'y'] }, q4: { text: 'More training' } };
    expect(fromAnswers(toAnswers(state))).toEqual({
      q1: { value: 5, choices: undefined, text: undefined },
      q3: { value: undefined, choices: ['x', 'y'], text: undefined },
      q4: { value: undefined, choices: undefined, text: 'More training' },
    });
  });

  test('bilingual pick falls back instead of going blank', () => {
    expect(pick('ar', 'Hello', 'مرحبا')).toBe('مرحبا');
    expect(pick('ar', 'Hello', '')).toBe('Hello');
    expect(pick('en', '', 'مرحبا')).toBe('مرحبا');
  });
});
