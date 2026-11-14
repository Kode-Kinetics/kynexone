import { expect, test } from '@playwright/test';
import { splitRunsByVoided } from '../src/lib/runOptions';
import { translate } from '../src/i18n/translations';

const run = (id: string, status: string) => ({ id, status });

// Two Voided Novembers were listed beside the live one in every run selector.
test('live runs come first in their given order and voided runs are set apart', () => {
  const { live, voided } = splitRunsByVoided([run('v1', 'Voided'), run('v2', 'Voided'), run('live', 'Locked'), run('draft', 'Draft')]);
  expect(live.map(r => r.id)).toEqual(['live', 'draft']);
  expect(voided.map(r => r.id)).toEqual(['v1', 'v2']);
});

test('a selector with no voided run has no voided group', () => {
  expect(splitRunsByVoided([run('a', 'Locked')]).voided).toEqual([]);
});

test('the voided group label has Arabic', () => {
  expect(translate('ar', 'Voided runs')).not.toBe('Voided runs');
});
