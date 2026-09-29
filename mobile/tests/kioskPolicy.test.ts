import test from 'node:test';
import assert from 'node:assert/strict';
import { kioskPunchLabel, nextKioskPunch } from '../src/features/attendance/kioskPolicy.ts';

test('kiosk alternates caller punch state', () => {
  assert.equal(nextKioskPunch(undefined), 'CLOCK_IN');
  assert.equal(kioskPunchLabel(undefined), 'Clock In');
  assert.equal(nextKioskPunch({ status: 'PRESENT', currentlyActive: true }), 'CLOCK_OUT');
  assert.equal(kioskPunchLabel({ status: 'PRESENT', currentlyActive: true }), 'Clock Out');
});
