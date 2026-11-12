import { expect, test } from '@playwright/test';
import { raisedByViewer } from '../src/lib/requisitionDecision';

// Demo R1 F10: the author of a requisition is not offered Approve (the API refuses it).
test('the author is recognised, whatever the case of the id', () => {
  expect(raisedByViewer({ requestedByUserId: 'AAAA-1' }, 'aaaa-1')).toBe(true);
});

test('someone else, an unknown author or an unknown viewer is not the author', () => {
  expect(raisedByViewer({ requestedByUserId: 'aaaa-1' }, 'bbbb-2')).toBe(false);
  expect(raisedByViewer({ requestedByUserId: null }, 'bbbb-2')).toBe(false);
  expect(raisedByViewer({}, 'bbbb-2')).toBe(false);
  expect(raisedByViewer({ requestedByUserId: 'aaaa-1' }, null)).toBe(false);
});
