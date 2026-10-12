import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  assignmentChip, assignmentStatusLabel, assignmentTone, completionSourceLabel, credentialStatusLabel, dueIn, enrollmentStatusLabel, enrollmentTone, learningError,
  moveOnNeedsPassMark, nextActionLabel, publishProblemLabel, questionProblem, refusalReasonLabel, validCode, validHttpUrl,
} from '../src/lib/learning';

/** The learning screens' pure rules: every server value has words in both languages, refusals read as sentences. */
test.describe('learning helpers', () => {
  const t = (key: string) => key;

  test('every status, source, next action and refusal has English and Arabic words', () => {
    const maps = [assignmentStatusLabel, enrollmentStatusLabel, credentialStatusLabel, completionSourceLabel, nextActionLabel, refusalReasonLabel,
      publishProblemLabel];
    for (const map of maps) {
      for (const key of Object.values(map)) {
        expect(LOCALE_DICTS.en[key], key).toBeTruthy();
        expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
        expect(LOCALE_DICTS.ar[key], key).not.toBe(LOCALE_DICTS.en[key]);
      }
    }
  });

  test('an acknowledgement is labelled as such on the transcript', () => {
    expect(completionSourceLabel.Acknowledgement).toBe('Acknowledged');
    expect(completionSourceLabel.Assessment).not.toBe(completionSourceLabel.Acknowledgement);
  });

  test('a cmi5 completion is labelled as reported by the course content, and waits visibly for HR', () => {
    expect(completionSourceLabel.Cmi5).toBe('Completed — reported by course content');
    expect(enrollmentStatusLabel.PendingVerification).toBe('Waiting for HR to verify');
    expect(nextActionLabel.pending_verification).toBe('Waiting for HR to verify');
    expect(enrollmentTone('PendingVerification')).toBe('amber');
  });

  test('a refused publish names every problem; self-decisions and score corrections read as sentences', () => {
    const err = { isAxiosError: true, response: { status: 422, data: { error: 'course_incomplete', details: ['move_on_not_applicable_with_certificate', 'mastery_score_missing'] } } };
    const text = learningError(err, t);
    expect(text).toContain('cannot issue a certificate or expire');
    expect(text).toContain('Set the pass mark the unit must report.');
    const refusal = (error: string) => ({ isAxiosError: true, response: { status: 403, data: { error } } });
    expect(learningError(refusal('self_decision_forbidden'), t)).toContain('your own learning record');
    expect(learningError(refusal('score_below_pass_mark'), t)).toContain('below the pass mark');
    expect(moveOnNeedsPassMark('CompletedOrPassed')).toBe(true);
    expect(moveOnNeedsPassMark('Completed')).toBe(false);
  });

  test('waiting for HR verification wins over overdue; overdue over the plain status', () => {
    expect(assignmentChip('InProgress', true, true)).toEqual({ label: 'Waiting for HR to verify', tone: 'amber' });
    expect(assignmentChip('InProgress', true, false)).toEqual({ label: 'Overdue', tone: 'rose' });
    expect(assignmentChip('Completed', true, true).label).toBe('Completed');
  });

  test('overdue wins over the status colour, completed is green', () => {
    expect(assignmentTone('Assigned', true)).toBe('rose');
    expect(assignmentTone('InProgress', false)).toBe('blue');
    expect(assignmentTone('Completed', true)).toBe('emerald');
  });

  test('error codes become actionable sentences; unknown codes fall back', () => {
    const err = (error: string, status = 409) => ({ isAxiosError: true, response: { status, data: { error, message: 'server words' } } });
    expect(learningError(err('duplicate_assignment'), t)).toBe('This employee already has this path and has not finished it.');
    expect(learningError(err('completion_rule_mismatch'), t)).toContain('not by acknowledging it');
    expect(learningError(err('feature_not_enabled', 403), t)).toBe('Learning is not switched on for your organisation yet.');
    expect(learningError(err('something_new', 400), t)).toBe('server words');
  });

  test('form checks mirror the server', () => {
    expect(validCode('FIRE-SAFETY')).toBe(true);
    expect(validCode('a')).toBe(false);
    expect(validCode('has space')).toBe(false);
    expect(validHttpUrl('https://au.example.com/index.html')).toBe(true);
    expect(validHttpUrl('javascript:alert(1)')).toBe(false);
    expect(questionProblem({ kind: 'Single', prompt: 'Q', options: ['a', 'b'], correct: [0, 1], trueFalse: null })).toContain('exactly one');
    expect(questionProblem({ kind: 'TrueFalse', prompt: 'Q', options: [], correct: [], trueFalse: null })).toContain('true or false');
    expect(questionProblem({ kind: 'Multi', prompt: 'Q', options: ['a', 'b', ''], correct: [0, 1], trueFalse: null })).toBeNull();
  });

  test('due-in counts whole days in UTC', () => {
    expect(dueIn('2026-10-11', '2026-10-01')).toBe(10);
    expect(dueIn('2026-09-30', '2026-10-01')).toBe(-1);
    expect(dueIn(null, '2026-10-01')).toBeNull();
  });
});
