import test from 'node:test';
import assert from 'node:assert/strict';
import { documentReviewState } from '../src/features/documents/documentReview.ts';

test('the status HR set on a document maps to a tone and a string key; unknown means uploaded', () => {
  assert.deepEqual(documentReviewState('Verified'), { tone: 'success', key: 'verified' });
  assert.deepEqual(documentReviewState('approved'), { tone: 'success', key: 'verified' });
  assert.deepEqual(documentReviewState('Pending'), { tone: 'info', key: 'pending' });
  assert.deepEqual(documentReviewState('REJECTED'), { tone: 'danger', key: 'rejected' });
  assert.deepEqual(documentReviewState(undefined), { tone: 'neutral', key: 'uploaded' });
  assert.deepEqual(documentReviewState('Expired'), { tone: 'neutral', key: 'uploaded' });
});
