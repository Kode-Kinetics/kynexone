import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { offerNextAction, offeredButtons, offerStatusLabelKey, approverPickerBlock, type OfferStateInput } from '../src/lib/offerWorkspaceState';
import { interviewStartIsPast, interviewFeedbackRows, handoffLabel, senderSourceKey, fromLine, applicationDir } from '../src/lib/recruitmentFixesC';

const allow = (names: string[], reasons: Record<string, string> = {}) =>
  Object.fromEntries(['editDraft', 'revise', 'freeze', 'withdraw', 'recordResponse', 'issue'].map(n => [n, { allowed: names.includes(n), reason: names.includes(n) ? null : reasons[n] ?? 'blocked' }]));
const state = (over: Partial<OfferStateInput>): OfferStateInput => ({ status: 'Draft', frozen: false, approvals: [], capabilities: allow(['editDraft', 'revise', 'freeze', 'withdraw']), dirty: false, viewerCanRequestApproval: false, viewerIsPendingApprover: false, ...over });

const APPROVAL_REQUIRED = 'This offer needs approval before it can be sent. Request approval from an HR Manager or Admin who did not write the offer.';
const NOBODY = { viewerCanRequestApproval: false, viewerIsPendingApprover: false };
const HR = { viewerCanRequestApproval: true, viewerIsPendingApprover: false };
const full = (over: Partial<OfferStateInput>): OfferStateInput => state({ ...NOBODY, ...over });

test.describe('offer workspace status and next action follow the real offer', () => {
  test('a draft that has not been frozen keeps "Draft" and says to freeze it', () => {
    expect(offerStatusLabelKey(state({}))).toBeNull();
    expect(offerNextAction(full({})).key).toBe('Freeze original');
  });

  test('after Freeze: HR can request approval from the Approvals tab; anyone else is told who can', () => {
    const frozen = { frozen: true, capabilities: allow(['revise', 'withdraw'], { issue: APPROVAL_REQUIRED }) };
    expect(offerStatusLabelKey(full(frozen))).toBe('Draft, original frozen');
    expect(offerNextAction(full({ ...frozen, ...HR }))).toMatchObject({ key: 'Request approval', kind: 'button', button: 'request-approval' });
    expect(offerNextAction(full(frozen))).toMatchObject({ key: 'An HR Manager or Admin has to request approval', kind: 'info' });
  });

  test('a frozen draft the API lets you send says Send review', () => {
    expect(offerNextAction(full({ frozen: true, capabilities: allow(['revise', 'issue']) })).key).toBe('Send review');
  });

  test('while an approval is pending: the approver is asked to decide, everyone else waits', () => {
    const pending = { status: 'PendingApproval', frozen: true, approvals: [{ status: 'Pending' }], capabilities: allow(['revise', 'withdraw']) };
    expect(offerNextAction(full(pending))).toMatchObject({ key: 'Waiting for the approver to decide', kind: 'info' });
    expect(offerNextAction(full({ ...pending, viewerIsPendingApprover: true }))).toMatchObject({ key: 'Review and decide', kind: 'button', button: 'decide' });
  });

  test('after an approver rejects, the status says so and a revision is the next action only for who may create one', () => {
    const rejected = { frozen: true, approvals: [{ status: 'Rejected' }] };
    expect(offerStatusLabelKey(full({ ...rejected, capabilities: allow(['revise']) }))).toBe('Rejected by an approver');
    expect(offerNextAction(full({ ...rejected, capabilities: allow(['revise', 'withdraw']) }))).toMatchObject({ key: 'Create revision', button: 'revise' });
    expect(offerNextAction(full({ ...rejected, capabilities: allow([]) })).kind).toBe('info');
  });

  test('unsaved edits come first; a sent offer awaiting the response says to record it', () => {
    expect(offerNextAction(full({ dirty: true })).key).toBe('Save draft');
    expect(offerNextAction(full({ status: 'Sent', frozen: true, capabilities: allow(['revise', 'recordResponse']) })).key).toBe('Record acceptance');
  });

  test('an approved offer that its approver cannot send says someone else has to', () => {
    const s = full({ status: 'Approved', frozen: true, approvals: [{ status: 'Approved' }], capabilities: allow(['revise'], { issue: 'You approved this offer, so someone else has to send it.' }) });
    expect(offerNextAction(s)).toMatchObject({ key: 'Someone else has to send this offer', kind: 'info' });
  });

  test('the next action always names a button the viewer is offered, in every state, as author and non-author', () => {
    const caps: Record<string, string[]> = {
      draft: ['editDraft', 'revise', 'freeze', 'withdraw'], frozen: ['revise', 'withdraw'], approved: ['revise', 'withdraw', 'issue'], sent: ['revise', 'withdraw', 'recordResponse'], none: [],
    };
    const cases: [string, Partial<OfferStateInput>][] = [
      ['draft', { capabilities: allow(caps.draft) }],
      ['dirty draft', { capabilities: allow(caps.draft), dirty: true }],
      ['frozen needing approval', { frozen: true, capabilities: allow(caps.frozen, { issue: APPROVAL_REQUIRED }) }],
      ['pending', { status: 'PendingApproval', frozen: true, approvals: [{ status: 'Pending' }], capabilities: allow(caps.frozen) }],
      ['rejected', { frozen: true, approvals: [{ status: 'Rejected' }], capabilities: allow(caps.frozen) }],
      ['approved', { status: 'Approved', frozen: true, approvals: [{ status: 'Approved' }], capabilities: allow(caps.approved) }],
      ['approved, cannot send', { status: 'Approved', frozen: true, approvals: [{ status: 'Approved' }], capabilities: allow(['revise'], { issue: 'You approved this offer, so someone else has to send it.' }) }],
      ['sent', { status: 'Sent', frozen: true, capabilities: allow(caps.sent) }],
      ['nothing allowed', { frozen: true, capabilities: allow(caps.none) }],
    ];
    for (const [name, over] of cases) {
      for (const viewer of [NOBODY, HR, { viewerCanRequestApproval: false, viewerIsPendingApprover: true }, { viewerCanRequestApproval: true, viewerIsPendingApprover: true }]) {
        const input = state({ ...over, ...viewer });
        const next = offerNextAction(input);
        if (next.kind === 'button') expect(offeredButtons(input), `${name} / ${JSON.stringify(viewer)}`).toContain(next.button);
        else expect(next.kind, name).toMatch(/info|none/);
      }
    }
  });

  test('"Request approval" is never named when the viewer has no such button', () => {
    const input = full({ frozen: true, capabilities: allow(['revise', 'withdraw'], { issue: APPROVAL_REQUIRED }) });
    expect(offeredButtons(input)).not.toContain('request-approval');
    expect(offerNextAction(input).key).not.toBe('Request approval');
  });
});

test.describe('the approver picker', () => {
  test('is blocked, with a reason, while an approval is pending', () => {
    expect(approverPickerBlock('PendingApproval', [{ status: 'Pending' }])).toBe('An approval is already waiting for a decision, so nobody else can be asked now. Wait for the decision, or create a revision to start again.');
    expect(approverPickerBlock('Draft', [{ status: 'Pending' }])).not.toBeNull();
  });
  test('is open for a draft with no pending approval', () => {
    expect(approverPickerBlock('Draft', [])).toBeNull();
  });
});

test.describe('interviews', () => {
  test('a start time in the past is refused, a future or blank one is not', () => {
    const now = new Date('2026-11-19T12:00:00');
    expect(interviewStartIsPast('2026-11-19T11:00', now)).toBe(true);
    expect(interviewStartIsPast('2026-11-18T23:59', now)).toBe(true);
    expect(interviewStartIsPast('2026-11-19T12:30', now)).toBe(false);
    expect(interviewStartIsPast('', now)).toBe(false);
  });

  test('submitted feedback is shown newest first with the interviewer, rating, recommendation, notes and time', () => {
    const rows = interviewFeedbackRows([
      { id: 'a', interviewerName: 'Liu Wei', overallScore: 3, recommendation: 'Hold', notes: 'Needs mentoring', submittedAtUtc: '2026-11-19T10:00:00Z' },
      { id: 'b', interviewerName: 'Dairy HR Manager', overallScore: 5, recommendation: 'Proceed', notes: 'Strong', submittedAtUtc: '2026-11-19T11:00:00Z' },
    ] as never);
    expect(rows.map(r => r.id)).toEqual(['b', 'a']);
    expect(rows[0]).toMatchObject({ interviewer: 'Dairy HR Manager', rating: 5, recommendation: 'Proceed', notes: 'Strong' });
  });

  test('"Proceed" has an Arabic label like the other recommendations', () => {
    expect(translate('ar', 'enum.InterviewRecommendation.Proceed')).toMatch(/[؀-ۿ]/);
  });
});

test.describe('compensation handoff labels', () => {
  test('raw enum text becomes plain words in English and Arabic', () => {
    for (const raw of ['NeedsReview', 'AcceptedOffer', 'ConfirmedLegacyPlacement', 'NamedProcess', 'OfferOnlyBenefit']) {
      const en = handoffLabel(raw);
      expect(en).not.toBe(raw);
      expect(en).not.toMatch(/^[A-Z][a-z]+[A-Z]/);
      expect(translate('ar', en)).toMatch(/[؀-ۿ]/);
    }
    expect(handoffLabel('NeedsReview')).toBe('Needs review');
    expect(handoffLabel('AcceptedOffer')).toBe('Accepted offer');
  });
});

test.describe('strings added for the recruitment fixes have Arabic', () => {
  test('the new keys translate', () => {
    for (const key of ['Draft, original frozen', 'Rejected by an approver', 'Waiting for the approver to decide', 'Someone else has to send this offer', 'An HR Manager or Admin has to request approval', 'Review and decide', 'Not scored yet', 'Sent from your company email', 'Sent from the platform email', 'Two-step sign-in is required for your role', 'Remind me later', 'Set up two-step sign-in']) {
      expect(translate('ar', key), key).toMatch(/[؀-ۿ]/);
    }
  });
});

test.describe('send dialog sender text is plain', () => {
  test('the sender source is a plain sentence, never the raw API value', () => {
    expect(senderSourceKey('Tenant')).toBe('Sent from your company email');
    expect(senderSourceKey('Platform (relay:Env;address:Env;name:Env)')).toBe('Sent from the platform email');
    expect(senderSourceKey('Something else')).toBeNull();
  });
  test('the From line puts the name first and the address after it', () => {
    expect(fromLine('HR Team', 'hr@example.invalid')).toBe('HR Team <hr@example.invalid>');
    expect(fromLine(null, 'hr@example.invalid')).toBe('hr@example.invalid');
    expect(fromLine('HR Team', null)).toBe('HR Team');
  });
});

test('a select showing a name reads in the direction of that name', () => {
  expect(applicationDir('Alice Almutairi')).toBe('ltr');
  expect(applicationDir('أليس المطيري')).toBe('rtl');
  expect(applicationDir('')).toBeUndefined();
});
