import test from 'node:test';
import assert from 'node:assert/strict';
import { riyadhBusinessDate, riyadhBusinessMonth } from '../src/utils/businessDate.ts';

test('Riyadh business date advances before UTC midnight', () => {
  const instant = new Date('2026-09-18T22:30:00.000Z');
  assert.equal(riyadhBusinessDate(instant), '2026-09-19');
  assert.equal(riyadhBusinessMonth(instant), '2026-09');
});

test('Riyadh business date handles year boundary', () => {
  assert.equal(riyadhBusinessDate(new Date('2026-12-31T22:00:00.000Z')), '2027-01-01');
});
