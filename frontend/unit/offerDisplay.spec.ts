import { expect, test } from '@playwright/test';
import { enumLabel } from '../src/i18n/enumLabel';
import { translate, type MessageParams } from '../src/i18n/translations';
import { offerApprovalNote } from '../src/lib/offerDisplay';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);

const API_ADVICE = 'Open the offer workspace and choose explicit email delivery or record confirmed external sending.';

test.describe('offer approval note', () => {
  test('a Sent offer shows its real state, never the stale send advice', () => {
    const note = offerApprovalNote('Sent', API_ADVICE);
    expect(note).toBe("Sent to the candidate; awaiting the candidate's response.");
    expect(note).not.toContain('Open the offer workspace');
    expect(ar(note!)).not.toBe(note);
  });

  test('accepted and declined offers say so', () => {
    expect(offerApprovalNote('Accepted', API_ADVICE)).toBe('The candidate accepted this offer.');
    expect(offerApprovalNote('Declined', API_ADVICE)).toBe('The candidate declined this offer.');
  });

  test('an offer that is not sent yet keeps the advice, translated', () => {
    expect(offerApprovalNote('Approved', API_ADVICE)).toBe(API_ADVICE);
    expect(offerApprovalNote('Draft', API_ADVICE)).toBe(API_ADVICE);
    expect(ar(API_ADVICE)).not.toBe(API_ADVICE);
  });

  test('a closed offer with nothing to say shows nothing', () => {
    expect(offerApprovalNote('Withdrawn', API_ADVICE)).toBeNull();
    expect(offerApprovalNote('Expired', API_ADVICE)).toBeNull();
    expect(offerApprovalNote('Draft', null)).toBeNull();
  });
});

test.describe('offer event and status labels', () => {
  const events = ['EmailIssued', 'EmailTransportResult', 'EmailQueued', 'IssuanceReserved', 'ApprovalDecided', 'ApprovalRequested', 'Frozen', 'DraftSaved'];

  test('every known offer event has an English and an Arabic label', () => {
    for (const e of events) {
      const english = enumLabel(en, 'OfferEvent', e);
      const arabic = enumLabel(ar, 'OfferEvent', e);
      expect(english, e).not.toMatch(/^[A-Z][a-z]+[A-Z]/);
      expect(english, e).toMatch(/\s/);
      expect(arabic, e).not.toBe(english);
      expect(arabic, e).toMatch(/[؀-ۿ]/);
    }
    expect(enumLabel(en, 'OfferEvent', 'DraftSaved')).toBe('Draft saved');
  });

  test('an unknown event falls back to a humanised name', () => {
    expect(enumLabel(en, 'OfferEvent', 'SomethingNewHappened')).toBe('Something new happened');
  });

  test('the shared status map covers the offer and approval statuses in Arabic', () => {
    for (const s of ['PendingApproval', 'Sent', 'Accepted', 'Declined', 'Approved', 'Draft', 'Expired', 'Withdrawn']) {
      expect(enumLabel(ar, 'Status', s), s).toMatch(/[؀-ۿ]/);
    }
    expect(enumLabel(en, 'Status', 'PendingApproval')).toBe('Pending approval');
  });
});
