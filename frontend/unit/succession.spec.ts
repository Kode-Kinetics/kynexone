import { expect, test } from '@playwright/test';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import {
  METRIC_GROUPS, SUCCESSION_WORKSPACE_PERMISSIONS, actionStatusLabel, actionTypeLabel, assessmentStatusLabel, bandLabel, calibrationReasonProblem,
  completionMethodLabel, currentPeriod, expectedDrillRows, gapExplanation, gapLabel, historyActionLabel, impactLabel, maskedStageLabel,
  metricDefinition, metricLabel, nineBoxCellLabel, nineBoxRows, nominationStatusLabel, onTrackReason, planStatusLabel, potentialLabel,
  progressParts, readinessLabel, riskLabel, slateDraftProblem, slateStatusLabel, sortGapsFirst, successionError, validPeriod, visualBandOrder,
} from '../src/lib/succession';
import { navigationItems } from '../src/routes/navigation';
import { essPages } from '../src/routes/essSections';

/** The succession screens' pure rules: words in both languages, the 9-box and its mirroring, refusals as sentences. */
test.describe('succession helpers', () => {
  const t = (key: string) => key;

  test('every value, gap, metric, cell and refusal has English and Arabic words', () => {
    const maps = [readinessLabel, riskLabel, impactLabel, nominationStatusLabel, maskedStageLabel, assessmentStatusLabel, slateStatusLabel,
      planStatusLabel, actionStatusLabel, actionTypeLabel, completionMethodLabel, bandLabel, potentialLabel, gapLabel, gapExplanation,
      metricLabel, metricDefinition, nineBoxCellLabel, historyActionLabel];
    for (const map of maps) {
      for (const key of Object.values(map)) {
        expect(LOCALE_DICTS.en[key], key).toBeTruthy();
        expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
        expect(LOCALE_DICTS.ar[key], key).not.toBe(LOCALE_DICTS.en[key]);
      }
    }
  });

  test('readiness, potential and the 9-box cells use the agreed words (SUC_DESIGN §2)', () => {
    expect(translate('en', readinessLabel.ReadyNow)).toBe('Ready now');
    expect(translate('ar', readinessLabel.ReadyNow)).toBe('جاهز الآن');
    expect(translate('en', readinessLabel.OneToTwoYears)).toBe('Ready in 1–2 years');
    expect(translate('ar', readinessLabel.OneToTwoYears)).toBe('جاهز خلال 1–2 سنة');
    expect(translate('ar', readinessLabel.ThreePlusYears)).toBe('جاهز خلال 3 سنوات أو أكثر');
    expect([1, 2, 3].map((p) => translate('ar', potentialLabel[p]))).toEqual(['محدود', 'متوسط', 'عالٍ']);
    expect([1, 2, 3].map((p) => translate('en', potentialLabel[p]))).toEqual(['Limited', 'Growth', 'High']);
    expect(translate('en', nineBoxCellLabel['P3-High'])).toBe('Future leader');
    expect(translate('ar', nineBoxCellLabel['P3-High'])).toBe('قائد المستقبل');
    expect(translate('en', nineBoxCellLabel['P1-Low'])).toBe('Below expectations');
    expect(translate('ar', nineBoxCellLabel['P2-Moderate'])).toBe('مساهم أساسي');
  });

  test('the 9-box is High potential on top, performance Low → High in reading order, and mirrors in Arabic', () => {
    const rows = nineBoxRows();
    expect(rows.map((r) => r.map((c) => c.code))).toEqual([
      ['P3-Low', 'P3-Moderate', 'P3-High'],
      ['P2-Low', 'P2-Moderate', 'P2-High'],
      ['P1-Low', 'P1-Moderate', 'P1-High'],
    ]);
    expect(visualBandOrder('ltr')).toEqual(['Low', 'Moderate', 'High']);
    expect(visualBandOrder('rtl')).toEqual(['High', 'Moderate', 'Low']);
  });

  test('gaps come first, the worst gaps before the mild ones', () => {
    const rows = sortGapsFirst([
      { positionTitle: 'A covered', gaps: [] },
      { positionTitle: 'B stale', gaps: ['StaleSlate'] },
      { positionTitle: 'C vacant', gaps: ['VacantNoReadyNow', 'NoReadyNow'] },
      { positionTitle: 'D no slate', gaps: ['NoApprovedSlate', 'NoReadyNow'] },
    ] as Array<{ positionTitle: string; gaps: Array<'StaleSlate' | 'VacantNoReadyNow' | 'NoReadyNow' | 'NoApprovedSlate'> }>);
    expect(rows.map((r) => r.positionTitle)).toEqual(['C vacant', 'D no slate', 'B stale', 'A covered']);
  });

  test('a figure and its drill rows reconcile: a rate drills to its numerator, a count to its value', () => {
    expect(expectedDrillRows({ key: 'coverage', value: 50, numerator: 2, denominator: 4 })).toBe(2);
    expect(expectedDrillRows({ key: 'gapNoReadyNow', value: 3, numerator: null, denominator: null })).toBe(3);
    expect(expectedDrillRows({ key: 'plansOnTrack', value: 1, numerator: 1, denominator: 3 })).toBe(1);
    // Overdue actions are a sum of actions; their drill lists the plans that hold them, so no row-count promise.
    expect(expectedDrillRows({ key: 'actionsOverdue', value: 5, numerator: null, denominator: null })).toBeNull();
    // Gaps lead the tiles too.
    expect(METRIC_GROUPS[0].id).toBe('gaps');
  });

  test('calibration needs a 20-character reason only when it moves the potential', () => {
    expect(calibrationReasonProblem(2, 2, '')).toBeNull();
    expect(calibrationReasonProblem(2, 3, 'too short')).not.toBeNull();
    expect(calibrationReasonProblem(2, 3, 'Led the Riyadh cut-over end to end')).toBeNull();
  });

  test('slate drafts are ordered 1..n by hand, each place with a reason', () => {
    expect(slateDraftProblem([])).toBe('suc.check.slateEmpty');
    expect(slateDraftProblem([{ order: 1, rationale: 'Ready to lead' }, { order: 3, rationale: 'Needs one more year' }])).toBe('suc.check.slateOrder');
    expect(slateDraftProblem([{ order: 1, rationale: 'Ready to lead' }, { order: 1, rationale: 'Needs one more year' }])).toBe('suc.check.slateOrder');
    expect(slateDraftProblem([{ order: 2, rationale: 'Ready to lead' }, { order: 1, rationale: 'short' }])).toBe('suc.check.slateRationale');
    expect(slateDraftProblem([{ order: 2, rationale: 'Ready to lead now' }, { order: 1, rationale: 'Ran the region' }])).toBeNull();
  });

  test('review periods follow the server pattern', () => {
    expect(validPeriod('2026-H2')).toBe(true);
    expect(validPeriod('2026-q3')).toBe(true);
    expect(validPeriod('2026-FY')).toBe(true);
    expect(validPeriod('2026-H3')).toBe(false);
    expect(validPeriod('H2-2026')).toBe(false);
    expect(currentPeriod(new Date(Date.UTC(2026, 9, 11)))).toBe('2026-H2');
    expect(currentPeriod(new Date(Date.UTC(2026, 2, 1)))).toBe('2026-H1');
  });

  test('progress counts completed over non-cancelled actions, and on-track always says why', () => {
    const base = { total: 4, completed: 1, cancelled: 1, overdue: 0, percentComplete: 33.3, onTrack: true, reviewOverdue: false };
    const p = progressParts(base);
    expect(translate('en', p.key, p.params)).toBe('1 of 3 actions completed (cancelled actions not counted)');
    expect(progressParts({ ...base, total: 1, cancelled: 1 }).key).toBe('suc.progress.none');
    expect(onTrackReason(base)).toBe('suc.track.ok');
    expect(onTrackReason({ ...base, overdue: 2, onTrack: false })).toBe('suc.track.overdueActions');
    expect(onTrackReason({ ...base, reviewOverdue: true, onTrack: false })).toBe('suc.track.reviewLate');
    expect(onTrackReason({ ...base, overdue: 1, reviewOverdue: true, onTrack: false })).toBe('suc.track.bothLate');
  });

  test('error codes become sentences; a reviewer conflict keeps the server\'s specific rule', () => {
    const err = (error: string, message = 'server words', status = 409) => ({ isAxiosError: true, response: { status, data: { error, message } } });
    expect(successionError(err('slate_open'), t)).toBe('suc.error.slate_open');
    expect(successionError(err('reviewer_conflict', 'The candidate reports to you.'), t)).toBe('suc.error.reviewer_conflict The candidate reports to you.');
    expect(successionError(err('feature_not_enabled', 'x', 403), t)).toBe('suc.error.feature_not_enabled');
    expect(successionError(err('invalid_request', 'Give the reason this position is critical of at least 10 characters.', 400), t))
      .toBe('Give the reason this position is critical of at least 10 characters.');
  });

  test('the nav entry opens for exactly the workspace keys, and the ESS page needs the succession flag and succession.self', () => {
    const nav = navigationItems.find((i) => i.path === '/performance/succession');
    expect(nav?.requiredPermissions).toEqual(SUCCESSION_WORKSPACE_PERMISSIONS);
    expect(nav?.requiredFeatureKey).toBe('succession');
    expect(nav?.requiredPermissions).not.toContain('succession.self');
    const ess = essPages.find((p) => p.path === '/ess/development-plan');
    expect(ess?.requiredFeatureKey).toBe('succession');
    expect(ess?.requiredPermission).toBe('succession.self');
    for (const key of [nav!.label, ess!.label, ess!.tab, ess!.hint]) {
      expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
    }
  });
});
