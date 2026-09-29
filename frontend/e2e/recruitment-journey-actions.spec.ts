import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  HIRE_THROUGH_OFFER_HINT,
  assessmentScoreLabel,
  assessmentScoreMax,
  canRecordAssessmentResult,
  canSendOffer,
  nextPipelineStage,
  parseAssessmentScore,
} from '../src/lib/recruitmentJourney';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');
const recruitmentPage = () => read('src/views/RecruitmentPage.tsx');

/**
 * F08 — the hire journey as the Recruitment screen presents it. Each defect below was a dead end a
 * user could reach on main:
 *   - "Move to Hired" hired nobody: no employee record, and the offer could no longer be accepted.
 *   - An assessment's result could never be recorded: the input only rendered for a Completed
 *     assessment with no score, and recording the score is what completes it.
 *   - The score box said 0–100 even where the API scores raw marks out of a question bank.
 *   - An offer that had passed approval (Approved) had no Send button anywhere.
 *   - A refused send from the Offers tab failed silently.
 */
test.describe('Recruitment — hire journey actions', () => {
  test('the pipeline never offers a stage move into Hired', () => {
    expect(nextPipelineStage('Applied')).toBe('Screening');
    expect(nextPipelineStage('Interview')).toBe('Offer');
    expect(nextPipelineStage('Offer')).toBeNull();
    expect(nextPipelineStage('Hired')).toBeNull();
    expect(nextPipelineStage('Unknown')).toBeNull();

    const source = recruitmentPage();
    expect(source).toContain('const nextStage = nextPipelineStage(app.stage);');
    expect(source).toContain('HIRE_THROUGH_OFFER_HINT');
    expect(HIRE_THROUGH_OFFER_HINT).toMatch(/accept the offer/i);
  });

  test('approved and unapproved-draft offers can be sent, from both places that send', () => {
    expect(canSendOffer('Draft')).toBe(true);
    expect(canSendOffer('Approved')).toBe(true);
    for (const status of ['PendingApproval', 'Sent', 'Accepted', 'Declined', 'Expired']) {
      expect(canSendOffer(status)).toBe(false);
    }

    const source = recruitmentPage();
    expect(source).toContain("{canSendOffer(offer.status) && (");
    expect(source).toContain('{canSendOffer(o.status) && (');
    // The Offers tab surfaces a refusal (e.g. "an approver rejected this offer") instead of swallowing it.
    expect(source).toMatch(/try \{ await offersApi\.send\(id\); load\(\); \} catch \(e\) \{ notifyApiError\(e\); \}/);
  });

  test('an assessment result can be recorded once sent, and only while it has no score', () => {
    expect(canRecordAssessmentResult({ status: 'Sent', scorePercentage: null })).toBe(true);
    expect(canRecordAssessmentResult({ status: 'InProgress', scorePercentage: null })).toBe(true);
    expect(canRecordAssessmentResult({ status: 'Completed', scorePercentage: null })).toBe(true);
    expect(canRecordAssessmentResult({ status: 'Completed', scorePercentage: 80 })).toBe(false);
    expect(canRecordAssessmentResult({ status: 'Pending', scorePercentage: null })).toBe(false);
    expect(canRecordAssessmentResult({ status: 'Expired', scorePercentage: null })).toBe(false);

    expect(recruitmentPage()).toContain('{canRecordAssessmentResult(a) && (');
  });

  test('the score is entered on the assessment’s own scale', () => {
    expect(assessmentScoreMax(0)).toBe(100);
    expect(assessmentScoreMax(null)).toBe(100);
    expect(assessmentScoreMax(20)).toBe(20);
    expect(assessmentScoreLabel(0)).toBe('Score % (0–100)');
    expect(assessmentScoreLabel(20)).toBe('Marks (0–20)');

    expect(parseAssessmentScore('85', 0)).toBe(85);
    expect(parseAssessmentScore('100', null)).toBe(100);
    expect(parseAssessmentScore('101', 0)).toBeNull();
    expect(parseAssessmentScore('15', 20)).toBe(15);
    expect(parseAssessmentScore('21', 20)).toBeNull();
    expect(parseAssessmentScore('-1', 20)).toBeNull();
    expect(parseAssessmentScore('7.5', 20)).toBeNull();
    expect(parseAssessmentScore('', 20)).toBeNull();
    expect(parseAssessmentScore(undefined, 20)).toBeNull();

    const source = recruitmentPage();
    expect(source).toContain('parseAssessmentScore(scoreInput[a.id], a.totalMarks)');
    expect(source).toContain('max={assessmentScoreMax(a.totalMarks)}');
    expect(source).toContain('placeholder={assessmentScoreLabel(a.totalMarks)}');
  });
});
