import { expect, test } from '@playwright/test';
import {
  adverseCloseNeedsCheckIn, canAcknowledge, canAppeal, defaultTemplateId, employeeMaySeeResult,
  reviewProgress, selectableTemplates, PROBATION_RECOMMENDATIONS,
} from '../src/lib/appraisalsUi';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { appraisalsUi } from '../src/i18n/appraisalsUi';
import { REFUSAL_CODES, appraisalRefusal, refusalCode } from '../src/lib/appraisalsRefusal';

const tpl = (over: Record<string, unknown>) => ({ id: 'x', name: 'T', isActive: true, isDefault: false, ...over }) as never;

test.describe('appraisal screens: what the employee may see and do', () => {
  test('the result (score, rating, breakdown) is shown only once HR has published it', () => {
    for (const status of ['Published', 'Acknowledged', 'Appealed', 'Closed']) expect(employeeMaySeeResult(status), status).toBe(true);
    for (const status of ['Pending', 'SelfAssessmentDue', 'SelfAssessmentSubmitted', 'ManagerReview', 'ManagerReviewComplete', 'Calibration', 'FinalApproval'])
      expect(employeeMaySeeResult(status), status).toBe(false);
  });

  test('acknowledge is offered while Published; an appeal while Published or Acknowledged, never twice', () => {
    expect(canAcknowledge('Published')).toBe(true);
    expect(canAcknowledge('Acknowledged')).toBe(false);
    expect(canAppeal('Published')).toBe(true);
    expect(canAppeal('Acknowledged')).toBe(true);
    expect(canAppeal('Appealed')).toBe(false);
    expect(canAppeal('FinalApproval')).toBe(false);
  });

  test('the progress strip names where the review is', () => {
    expect(reviewProgress('SelfAssessmentDue')).toBe(0);
    expect(reviewProgress('ManagerReview')).toBe(1);
    expect(reviewProgress('FinalApproval')).toBe(2);
    expect(reviewProgress('Published')).toBe(3);
    expect(reviewProgress('Acknowledged')).toBe(4);
    expect(reviewProgress('Appealed')).toBe(4);
  });
});

test.describe('appraisal screens: cycle scorecard, PIP close, probation', () => {
  test('only active templates can be picked; the default is chosen for the user, a sole template too', () => {
    const list = [tpl({ id: 'a', isActive: false, isDefault: true }), tpl({ id: 'b' }), tpl({ id: 'c', isDefault: true })];
    expect(selectableTemplates(list).map(t => t.id)).toEqual(['b', 'c']);
    expect(defaultTemplateId(list)).toBe('c');
    expect(defaultTemplateId([tpl({ id: 'only' })])).toBe('only');
    expect(defaultTemplateId([tpl({ id: 'p' }), tpl({ id: 'q' })])).toBe('');
    expect(defaultTemplateId([])).toBe('');
  });

  test('a PIP cannot be closed Failed or with a termination recommendation before a check-in exists', () => {
    expect(adverseCloseNeedsCheckIn('Failed', 0)).toBe(true);
    expect(adverseCloseNeedsCheckIn('TerminationRecommended', 0)).toBe(true);
    expect(adverseCloseNeedsCheckIn('TerminationRecommended', 2)).toBe(false);
    expect(adverseCloseNeedsCheckIn('Improved', 0)).toBe(false);
    expect(adverseCloseNeedsCheckIn('Extended', 0)).toBe(false);
  });

  test('the probation recommendations are the three the API accepts', () => {
    expect([...PROBATION_RECOMMENDATIONS]).toEqual(['Confirm', 'Extend', 'Terminate']);
  });
});

test.describe('appraisal screens: Arabic', () => {
  test('every new key has its own Arabic with the same placeholders, and the two header strings are translated', () => {
    const keys = Object.keys(appraisalsUi.en);
    expect(keys.length).toBeGreaterThan(40);
    for (const k of keys) {
      const ar = appraisalsUi.ar[k];
      expect(ar, k).toBeTruthy();
      expect(ar, k).toMatch(/[؀-ۿ]/);
      const ph = (s: string) => (s.match(/\{\w+\}/g) ?? []).sort().join();
      expect(ph(ar), k).toBe(ph(appraisalsUi.en[k]));
      expect(LOCALE_DICTS.ar[k], `${k} is registered`).toBe(ar);
    }
    expect(translate('ar', 'Performance & Appraisals')).toMatch(/[؀-ۿ]/);
    expect(translate('ar', 'Balanced scorecard appraisals, goal management, calibration, recommendations, PIPs, and analytics.')).toMatch(/[؀-ۿ]/);
  });
});

test.describe('appraisal screens: refusals in the viewer\'s language', () => {
  const refusal = (data: unknown, status = 409) => ({ isAxiosError: true, response: { status, data } });
  const en = (k: string) => translate('en', k);
  const ar = (k: string) => translate('ar', k);

  test('a known error code becomes the screen\'s own sentence, in English and in Arabic', () => {
    const err = refusal({ error: 'no_monitoring_evidence', message: 'This plan has no recorded check-ins' });
    expect(refusalCode(err)).toBe('no_monitoring_evidence');
    expect(appraisalRefusal(err, en, 'fallback')).toBe(appraisalsUi.en['apr.error.no_monitoring_evidence']);
    expect(appraisalRefusal(err, ar, 'fallback')).toBe(appraisalsUi.ar['apr.error.no_monitoring_evidence']);
  });

  test('an unknown code keeps the server\'s own sentence, and nothing at all falls back to the caller\'s', () => {
    expect(refusalCode(refusal({ error: 'something_new', message: 'x' }))).toBeNull();
    expect(appraisalRefusal(refusal({ error: 'something_new', message: 'Specific reason.' }, 400), en, 'fallback')).toBe('Specific reason.');
    expect(appraisalRefusal(refusal({}, 400), en, 'fallback')).toBe('fallback');
  });

  test('every code the screen handles has an English and an Arabic sentence', () => {
    for (const code of REFUSAL_CODES) {
      expect(appraisalsUi.en[`apr.error.${code}`], code).toBeTruthy();
      expect(appraisalsUi.ar[`apr.error.${code}`], code).toMatch(/[؀-ۿ]/);
    }
  });
});
