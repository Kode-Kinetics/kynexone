import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { ReconBridgeRow, ReconDto, SignoffDto } from '../src/api/payrollRecon';
import {
  MIN_EXPLANATION_LENGTH, activeSignoff, buildSignoffRequest, categoryOfKey, drillParams, employeeReasonCategories,
  explanationId, flaggedItems, itemKey, missingExplanations, normalizeCells, offCycleSummary, parseReconError,
  requiredExplanations, reviewFromSignoff, rowTies, signedAmount, signoffGate, tiesOk, valueTieIssues, volumeMovement,
  volumeTies, reconTrusted,
} from '../src/lib/payrollRecon';

const fixture = (name: string): ReconDto => JSON.parse(fs.readFileSync(path.join(__dirname, '../e2e/fixtures', name), 'utf8')) as ReconDto;
const FULL = () => fixture('recon-full.json');
const FLAGGED = () => fixture('recon-flagged.json');

const signoff = (over: Partial<SignoffDto> = {}): SignoffDto => ({
  id: 's1', status: 'Active', signedOffByName: 'Layla Al-Harbi', signedOffAtUtc: '2026-11-02T09:15:00Z', snapshotHash: 'sha256:x', ...over,
});

test.describe('the shared fixtures really tie (they are the contract)', () => {
  for (const name of ['recon-full.json', 'recon-flagged.json', 'recon-partial.json', 'recon-signed.json']) {
    test(`${name}: volume identity, every item row and the headline tie to the cent`, () => {
      const d = fixture(name);
      expect(volumeTies(d.volume)).toBe(true);
      expect(valueTieIssues(d.value)).toEqual([]);
      expect(reconTrusted(d)).toBe(true);
    });
  }

  test('recon-noprior.json has no opening and no movement, only a closing and its composition', () => {
    const d = fixture('recon-noprior.json');
    expect(d.mode).toBe('NoPrior');
    expect(d.priorRunId ?? null).toBeNull();
    expect(d.volume.opening).toBe(0);
    expect(d.volume.lines).toEqual([]);
    expect(valueTieIssues(d.value, false)).toEqual([]);
  });
});

test.describe('ties', () => {
  test('a server tie is only true when it says true', () => {
    expect(tiesOk(true)).toBe(true);
    expect(tiesOk({ ok: true })).toBe(true);
    expect(tiesOk(false)).toBe(false);
    expect(tiesOk({ ok: false })).toBe(false);
    expect(tiesOk(undefined)).toBe(false);
    expect(tiesOk(null)).toBe(false);
  });

  test('volume: opening + adds - exits must equal closing', () => {
    const d = FULL();
    expect(volumeMovement(d.volume)).toEqual({ adds: 4, exits: 2, net: 2 });
    expect(volumeTies({ ...d.volume, closing: d.volume.closing + 1 })).toBe(false);
    expect(volumeTies({ ...d.volume, opening: d.volume.opening - 1 })).toBe(false);
  });

  test('a row ties when prior + adds + exits + continuing = current, compared in cents (no float noise)', () => {
    const row: ReconBridgeRow = {
      prior: 0.1, adds: { newJoiner: 0.2, rehire: 0, lateJoiner: 0, reinstated: 0, transferIn: 0 },
      exits: { leaver: 0, transferOut: 0, notPaid: 0, unexplained: 0 },
      continuing: { package: 0, proration: 0, variable: 0, arrears: 0, attendance: 0, loans: 0, statutory: 0, other: 0 },
      current: 0.3, delta: 0.2, deltaPct: null,
    };
    expect(rowTies(row)).toBe(true); // 0.1 + 0.2 !== 0.3 in floating point
    expect(rowTies({ ...row, current: 0.31 })).toBe(false);
  });

  test('one broken item is named, by section and code, so the screen can say which', () => {
    const d = FULL();
    d.value.sections[0].items[1].current += 5;
    expect(valueTieIssues(d.value)).toContain('Earning:HOUSING');
    expect(reconTrusted(d)).toBe(false);
  });

  test('a headline that is not Gross - Deductions is reported', () => {
    const d = FULL();
    d.value.headline.net.current += 1;
    expect(valueTieIssues(d.value)).toContain('headline:net');
  });

  test('an integrity failure or a server tie of false makes the reconciliation untrusted', () => {
    const a = FULL(); a.integrity = { ok: false, issues: [{ code: 'lineRegister' }] };
    expect(reconTrusted(a)).toBe(false);
    const b = FULL(); b.value.ties = false;
    expect(reconTrusted(b)).toBe(false);
    const c = FULL(); c.volume.ties = false;
    expect(reconTrusted(c)).toBe(false);
  });
});

test.describe('sign formatting', () => {
  const f = (n: number) => `SAR ${n.toFixed(2)}`;
  test('positive gets a plus, negative a real minus sign, zero none', () => {
    expect(signedAmount(5, f)).toBe('+SAR 5.00');
    expect(signedAmount(-5, f)).toBe('−SAR 5.00');
    expect(signedAmount(0, f)).toBe('SAR 0.00');
  });
  test('negative zero and sub-cent noise read as zero, not as a change', () => {
    expect(signedAmount(-0, f)).toBe('SAR 0.00');
    expect(signedAmount(0.004, f)).toBe('SAR 0.00');
    expect(signedAmount(-0.004, f)).toBe('SAR 0.00');
  });
});

test.describe('flagged items and written reasons', () => {
  test('only flagged items need an explanation, and each key is unique across sections', () => {
    const items = flaggedItems(FULL().value);
    expect(items.map(i => i.key)).toEqual(['Earning:OVERTIME']);
    const many = flaggedItems(FLAGGED().value);
    expect(many.map(i => i.key)).toEqual(['Earning:OVERTIME', 'Earning:BONUS']);
    expect(itemKey('Deduction', 'GOSI')).not.toBe(itemKey('Employer', 'GOSI'));
  });

  test('NOT_PAID and UNEXPLAINED employees need a reason; other exits do not', () => {
    expect(employeeReasonCategories(FULL().volume)).toEqual([]);
    expect(employeeReasonCategories(FLAGGED().volume)).toEqual(['NOT_PAID']);
    const v = FLAGGED().volume;
    v.lines = v.lines.map(l => (l.category === 'UNEXPLAINED' ? { ...l, count: 1 } : l));
    expect(employeeReasonCategories(v)).toEqual(['NOT_PAID', 'UNEXPLAINED']);
  });

  test('required explanations = flagged items + the listed employees, by scope and key', () => {
    const req = requiredExplanations(FLAGGED().value, [101, 102]);
    expect(req).toEqual([
      { scope: 'Item', key: 'Earning:OVERTIME', label: 'Overtime' },
      { scope: 'Item', key: 'Earning:BONUS', label: 'Performance bonus' },
      { scope: 'Employee', key: '101', label: null },
      { scope: 'Employee', key: '102', label: null },
    ]);
  });

  test('a reason shorter than the server minimum (after trimming) counts as missing', () => {
    expect(MIN_EXPLANATION_LENGTH).toBe(10);
    const req = requiredExplanations(FLAGGED().value, []);
    const drafts = {
      [explanationId('Item', 'Earning:OVERTIME')]: '   short   ',
      [explanationId('Item', 'Earning:BONUS')]: 'Q3 performance bonus approved by the board',
    };
    expect(missingExplanations(req, drafts)).toEqual([{ scope: 'Item', key: 'Earning:OVERTIME' }]);
    expect(missingExplanations(req, {})).toHaveLength(2);
  });

  test('a ten-character reason passes, nine does not', () => {
    const req = [{ scope: 'Employee' as const, key: '7', label: null }];
    expect(missingExplanations(req, { [explanationId('Employee', '7')]: '123456789' })).toHaveLength(1);
    expect(missingExplanations(req, { [explanationId('Employee', '7')]: '1234567890' })).toHaveLength(0);
  });
});

test.describe('the sign-off request', () => {
  const input = () => {
    const recon = FLAGGED();
    const required = requiredExplanations(recon.value, [101]);
    return {
      recon, required,
      drafts: {
        [explanationId('Item', 'Earning:OVERTIME')]: '  Seasonal overtime for the Q4 stock count  ',
        [explanationId('Item', 'Earning:BONUS')]: 'Board-approved Q3 performance bonus',
        [explanationId('Employee', '101')]: 'On unpaid leave, paid next month',
        [explanationId('Item', 'Earning:STALE')]: 'An explanation for a row that is no longer flagged',
      },
      acknowledgedFlaggedEmployeeCount: 3,
      notes: '',
      priorRunId: null as string | null,
    };
  };

  test('binds the version and hash the signer saw, trims reasons and drops anything not required', () => {
    const req = buildSignoffRequest(input());
    expect(req.expectedProcessVersion).toBe(3);
    expect(req.expectedSnapshotHash).toBe('sha256:7c1f0d8e5b2a49e3a6d4f1b0c9e8a7d6');
    expect(req.acknowledgedFlaggedEmployeeCount).toBe(3);
    expect(req.explanations).toEqual([
      { scope: 'Item', key: 'Earning:OVERTIME', text: 'Seasonal overtime for the Q4 stock count' },
      { scope: 'Item', key: 'Earning:BONUS', text: 'Board-approved Q3 performance bonus' },
      { scope: 'Employee', key: '101', text: 'On unpaid leave, paid next month' },
    ]);
  });

  test('notes and the prior-run override are sent only when present', () => {
    const a = buildSignoffRequest(input());
    expect('notes' in a).toBe(false);
    expect('priorRunId' in a).toBe(false);
    const b = buildSignoffRequest({ ...input(), notes: '  checked with finance ', priorRunId: 'abc' });
    expect(b.notes).toBe('checked with finance');
    expect(b.priorRunId).toBe('abc');
  });

  test('the reconciliation is bound to the run as it was read: a missing stamp is refused, never invented', () => {
    const i = input();
    const recon = { ...i.recon } as Partial<ReconDto>;
    delete recon.processVersion;
    expect(() => buildSignoffRequest({ ...i, recon: recon as ReconDto })).toThrow(/stamp/i);
  });
});

test.describe('who may sign, and why not', () => {
  const ok = () => ({
    recon: FULL(), runStatus: 'Processed', mayApprove: true,
    required: requiredExplanations(FULL().value, []),
    drafts: { [explanationId('Item', 'Earning:OVERTIME')]: 'Seasonal overtime for the Q4 stock count' },
    flaggedEmployeeCount: 3 as number | null, flaggedAffirmed: true,
  });

  test('everything in place: the form can be submitted', () => {
    expect(signoffGate(ok())).toEqual({ canSubmit: true, blocker: null });
  });

  test('blockers come in a fixed order, the first one is the reason shown', () => {
    expect(signoffGate({ ...ok(), mayApprove: false }).blocker).toBe('no_permission');
    expect(signoffGate({ ...ok(), runStatus: 'Approved' }).blocker).toBe('run_status');
    expect(signoffGate({ ...ok(), runStatus: 'Draft' }).blocker).toBe('run_status');
    const partial = ok(); partial.recon.scope = { level: 'Department', partial: true, canSignOff: false, reasons: ['x'] };
    expect(signoffGate(partial).blocker).toBe('scope');
    const na = ok(); na.recon.mode = 'NotApplicable';
    expect(signoffGate(na).blocker).toBe('not_applicable');
    const bad = ok(); bad.recon.integrity = { ok: false, issues: [] };
    expect(signoffGate(bad).blocker).toBe('integrity');
    const noStamp = ok(); delete (noStamp.recon as Partial<ReconDto>).snapshotHash;
    expect(signoffGate(noStamp).blocker).toBe('no_stamp');
    expect(signoffGate({ ...ok(), drafts: {} }).blocker).toBe('explanations');
    expect(signoffGate({ ...ok(), flaggedAffirmed: false }).blocker).toBe('acknowledge');
  });

  test('the flagged-employee count could not be read: signing is withheld, not waved through', () => {
    expect(signoffGate({ ...ok(), flaggedEmployeeCount: null }).blocker).toBe('flagged_unknown');
  });

  test('no flagged employees means there is nothing to acknowledge', () => {
    expect(signoffGate({ ...ok(), flaggedEmployeeCount: 0, flaggedAffirmed: false })).toEqual({ canSubmit: true, blocker: null });
  });

  test('an Active sign-off already stands', () => {
    const s = ok(); s.recon.signoff = signoff();
    expect(signoffGate(s).blocker).toBe('already_signed');
  });

  test('a NoPrior reconciliation still needs a sign-off but has no explanations to give', () => {
    const np = fixture('recon-noprior.json');
    const g = signoffGate({ ...ok(), recon: np, required: requiredExplanations(np.value, []), drafts: {}, flaggedEmployeeCount: 0 });
    expect(g).toEqual({ canSubmit: true, blocker: null });
  });
});

test.describe('review is done only on an Active server sign-off', () => {
  test('Active counts; Superseded, absent and null do not', () => {
    expect(activeSignoff(signoff())).not.toBeNull();
    expect(activeSignoff(signoff({ status: 'Superseded', supersededReason: 'Reprocessed' }))).toBeNull();
    expect(activeSignoff(null)).toBeNull();
    expect(activeSignoff(undefined)).toBeNull();
  });
  test('the review stage reads who and when from the sign-off', () => {
    expect(reviewFromSignoff(signoff())).toEqual({ reviewedAtUtc: '2026-11-02T09:15:00Z', reviewedByName: 'Layla Al-Harbi' });
    expect(reviewFromSignoff(signoff({ status: 'Superseded' }))).toBeNull();
    expect(reviewFromSignoff(null)).toBeNull();
  });
});

test.describe('drill-down requests', () => {
  test('camelCase bridge keys map to the category names the API uses', () => {
    expect(categoryOfKey('newJoiner')).toBe('NEW_JOINER');
    expect(categoryOfKey('lateJoiner')).toBe('LATE_JOINER');
    expect(categoryOfKey('transferOut')).toBe('TRANSFER_OUT');
    expect(categoryOfKey('notPaid')).toBe('NOT_PAID');
    expect(categoryOfKey('unexplained')).toBe('UNEXPLAINED');
  });

  test('a volume number asks for its category; opening and closing ask for themselves', () => {
    expect(drillParams({ section: 'volume', category: 'NEW_JOINER' })).toEqual({ section: 'volume', category: 'NEW_JOINER' });
    expect(drillParams({ section: 'volume', category: 'CLOSING' })).toEqual({ section: 'volume', category: 'CLOSING' });
  });

  test('a value cell names its section kind, item and the add/exit category or driver bucket', () => {
    expect(drillParams({ section: 'value', kind: 'Earning', itemCode: 'BASIC', cell: { group: 'adds', key: 'newJoiner' } }))
      .toEqual({ section: 'value', kind: 'Earning', itemCode: 'BASIC', category: 'NEW_JOINER' });
    expect(drillParams({ section: 'value', kind: 'Earning', itemCode: 'BASIC', cell: { group: 'exits', key: 'leaver' } }))
      .toEqual({ section: 'value', kind: 'Earning', itemCode: 'BASIC', category: 'LEAVER' });
    expect(drillParams({ section: 'value', kind: 'Earning', itemCode: 'BASIC', cell: { group: 'continuing', key: 'package' } }))
      .toEqual({ section: 'value', kind: 'Earning', itemCode: 'BASIC', bucket: 'package' });
    expect(drillParams({ section: 'value', kind: 'Deduction', itemCode: 'GOSI_EE', cell: 'prior' }))
      .toEqual({ section: 'value', kind: 'Deduction', itemCode: 'GOSI_EE', bucket: 'prior' });
    expect(drillParams({ section: 'value', kind: 'Deduction', itemCode: 'GOSI_EE', cell: 'current' }))
      .toEqual({ section: 'value', kind: 'Deduction', itemCode: 'GOSI_EE', bucket: 'current' });
  });

  test('the flagged employees and a final-settlement cell have their own targets', () => {
    expect(drillParams({ section: 'value', flagged: true })).toEqual({ section: 'value', bucket: 'flagged' });
    expect(drillParams({ section: 'finalSettlement', cell: 'LEAVER_IN_BRIDGE' })).toEqual({ section: 'finalSettlement', category: 'LEAVER_IN_BRIDGE' });
    expect(drillParams({ section: 'finalSettlement', component: 'EOSB_GRATUITY' })).toEqual({ section: 'finalSettlement', itemCode: 'EOSB_GRATUITY' });
  });
});

test.describe('errors from the reconciliation endpoints', () => {
  const axiosErr = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });

  test('reads the error code, message and the extras the Approve refusal carries', () => {
    const e = parseReconError(axiosErr(409, { error: 'reconciliation_not_reviewed', message: 'Sign off first.', reason: 'Stale', processVersion: 4, signedVersion: 3 }));
    expect(e).toMatchObject({ code: 'reconciliation_not_reviewed', status: 409, reason: 'Stale', processVersion: 4, signedVersion: 3, message: 'Sign off first.' });
  });

  test('lists the explanations the server says are still missing', () => {
    const e = parseReconError(axiosErr(422, { error: 'variance_explanation_required', message: 'x', missingKeys: ['Earning:OVERTIME', '101'] }));
    expect(e.code).toBe('variance_explanation_required');
    expect(e.missingKeys).toEqual(['Earning:OVERTIME', '101']);
  });

  test('an error the UI does not know is "unknown", never mislabelled', () => {
    expect(parseReconError(axiosErr(500, { error: 'boom' })).code).toBe('unknown');
    expect(parseReconError(new Error('x')).code).toBe('unknown');
    expect(parseReconError(axiosErr(404, null)).status).toBe(404);
  });

  test('a code that is not in the contract is not trusted just because it is a string', () => {
    expect(parseReconError(axiosErr(409, { error: 'signoff_stale' })).code).toBe('signoff_stale');
    expect(parseReconError(axiosErr(409, { error: 'totally_made_up' })).code).toBe('unknown');
  });
});

test.describe('tolerant readers for loosely specified fields', () => {
  test('final-settlement cells come as a list or as a map', () => {
    expect(normalizeCells([{ code: 'OTHER', count: 2 }])).toEqual([{ code: 'OTHER', count: 2 }]);
    expect(normalizeCells({ LEFT_EARLIER: 1, OTHER: 0 })).toEqual([{ code: 'LEFT_EARLIER', count: 1 }, { code: 'OTHER', count: 0 }]);
    expect(normalizeCells(undefined)).toEqual([]);
  });

  test('off-cycle memo reads a count and gross, or a bare count', () => {
    expect(offCycleSummary({ count: 2, gross: 5000 })).toEqual({ count: 2, gross: 5000 });
    expect(offCycleSummary(3)).toEqual({ count: 3, gross: null });
    expect(offCycleSummary(undefined)).toEqual({ count: 0, gross: null });
  });
});
