import { test, expect } from '@playwright/test';
import { paymentActionRefusal, paymentActionsAllowed, PAYMENT_ACTION_DENIED } from '../src/lib/payrollPaymentAccess';

// Creating a payment batch, the WPS status, settling and remitting all need payroll.export, which only Admin holds.
// The server answers a bare 403 with no body, and the screen used to read that as "the run must be Locked first".

const forbidden = { response: { status: 403, data: '' } };

test('a 403 on a payment action is a permission refusal, named for the action, never a lock instruction', () => {
  expect(paymentActionRefusal(forbidden, 'create')).toBe(PAYMENT_ACTION_DENIED.create);
  expect(paymentActionRefusal(forbidden, 'create')).toMatch(/permission to create payment batches/);
  expect(paymentActionRefusal(forbidden, 'create')).not.toMatch(/Locked/);
  expect(paymentActionRefusal(forbidden, 'wps')).toMatch(/WPS/);
  expect(paymentActionRefusal(forbidden, 'settle')).toMatch(/settle/);
  expect(paymentActionRefusal(forbidden, 'remit')).toMatch(/remit/);
});

test('every refusal tells the person who to ask', () => {
  for (const action of ['create', 'wps', 'settle', 'remit'] as const) {
    expect(PAYMENT_ACTION_DENIED[action]).toMatch(/ask an administrator/);
  }
});

test('only a 403 is a permission refusal; other failures keep their own message', () => {
  expect(paymentActionRefusal({ response: { status: 400, data: { message: 'x' } } }, 'create')).toBeNull();
  expect(paymentActionRefusal({ response: { status: 422, data: {} } }, 'create')).toBeNull();
  expect(paymentActionRefusal(new Error('x'), 'create')).toBeNull();
  expect(paymentActionRefusal(undefined, 'create')).toBeNull();
});

test('a 403 that carries the server\'s own sentence keeps it (a separation-of-duties refusal explains itself)', () => {
  expect(paymentActionRefusal({ response: { status: 403, data: { message: 'You generated this file, so you cannot accept it.' } } }, 'wps'))
    .toBe('You generated this file, so you cannot accept it.');
});

test('the actions are available exactly to holders of payroll.export', () => {
  expect(paymentActionsAllowed(p => p === 'payroll.export')).toBe(true);
  expect(paymentActionsAllowed(p => p === 'payroll.lock')).toBe(false);
  expect(paymentActionsAllowed(() => false)).toBe(false);
});
