import test from 'node:test';
import assert from 'node:assert/strict';
import {
  applyState,
  chooseRepaymentMethod,
  estimatedInstallment,
  installmentCeiling,
  installmentStatus,
  loanStatus,
  loansAccessFromError,
  parseAmountInput,
  reasonDisplay,
  repaymentMethodFor,
  summarizeLoan,
  validateApplication,
  type Loan,
  type LoanEligibility,
  type LoanInstallment,
  type OfferedLoanType,
} from '../src/features/loans/loanRules.ts';

const TYPE: OfferedLoanType = {
  loanTypeId: 't1', code: 'PERSONAL', nameEn: 'Personal Loan', nameAr: 'قرض شخصي',
  gradeLimited: true, offered: true, reasonCode: null, reasonText: null,
};

function eligibility(overrides: Partial<LoanEligibility>): LoanEligibility {
  return {
    eligible: true, reasons: [], codes: [], maxAvailableAmount: 16000, available: 16000,
    currency: 'SAR', policyId: 'p1', preview: true, ...overrides,
  };
}

test('ineligible types carry the server reasons and never an Apply state', () => {
  const state = applyState(TYPE, eligibility({
    eligible: false,
    reasons: ['Probation must be completed before applying.'],
    codes: ['Probation'],
  }), 24);
  assert.equal(state.kind, 'ineligible');
  assert.deepEqual(state.kind === 'ineligible' && state.reasons, [{ code: 'Probation', text: 'Probation must be completed before applying.' }]);
  // Ineligible with no reason given is still ineligible.
  assert.equal(applyState(TYPE, eligibility({ eligible: false }), 24).kind, 'ineligible');
});

test('not offered, still checking, check failed, nothing left to borrow, eligible', () => {
  const notOffered = applyState({ ...TYPE, offered: false, reasonCode: 'LoanTypeNotOffered', reasonText: "Loans of this type aren't offered by your company." }, undefined, null);
  assert.equal(notOffered.kind, 'notOffered');
  assert.equal(applyState(TYPE, undefined, 24).kind, 'checking');
  assert.equal(applyState(TYPE, null, 24).kind, 'unknown');
  assert.equal(applyState(TYPE, eligibility({ available: 0 }), 24).kind, 'ineligible');
  assert.deepEqual(applyState(TYPE, eligibility({}), 24), { kind: 'eligible', available: 16000, currency: 'SAR', maxInstallments: 24 });
});

test('English shows the server sentence; Arabic shows the translated code, else the server sentence', () => {
  const probation = { code: 'Probation', text: 'Probation must be completed before applying.' };
  assert.deepEqual(reasonDisplay(probation, 'en'), { kind: 'server', text: probation.text });
  assert.deepEqual(reasonDisplay(probation, 'ar'), { kind: 'code', code: 'Probation' });
  assert.deepEqual(reasonDisplay({ code: 'BrandNewCode', text: 'Something new.' }, 'ar'), { kind: 'server', text: 'Something new.' });
  assert.deepEqual(reasonDisplay({ code: null, text: '' }, 'ar'), { kind: 'generic' });
  assert.deepEqual(reasonDisplay({ code: 'NothingAvailable', text: '' }, 'en'), { kind: 'code', code: 'NothingAvailable' });
});

test('the policy picks the repayment method; the server-named policy wins over a guess', () => {
  assert.equal(chooseRepaymentMethod(['BankTransfer', 'PayrollDeduction']), 'PayrollDeduction');
  assert.equal(chooseRepaymentMethod(['Cash']), 'Cash');
  assert.equal(chooseRepaymentMethod([]), 'BankTransfer');
  const policies = [
    { id: 'pA', loanTypeId: 't1', maxInstallments: 24, allowedRepaymentMethods: ['PayrollDeduction'] },
    { id: 'pB', loanTypeId: 't1', maxInstallments: 12, allowedRepaymentMethods: ['BankTransfer'] },
  ];
  assert.equal(repaymentMethodFor(policies, 't1').method, 'BankTransfer'); // two candidates: no guess
  assert.equal(repaymentMethodFor(policies, 't1', 'pA').method, 'PayrollDeduction');
  assert.equal(repaymentMethodFor(policies.slice(1), 't1').policy?.id, 'pB');
  assert.equal(installmentCeiling(24, 12), 12);
  assert.equal(installmentCeiling(null, 0), null);
});

test('application form: amount parsing (Arabic digits), limits and the instalment estimate', () => {
  assert.equal(parseAmountInput('١٢٬٥٠٠٫٥'), 12500.5);
  assert.equal(parseAmountInput('4,800'), 4800);
  assert.equal(parseAmountInput('12.345'), null);
  assert.equal(parseAmountInput('abc'), null);
  const limits = { available: 16000, maxInstallments: 24 };
  assert.deepEqual(validateApplication({ amountText: '', installments: 12 }, limits).errors, { amount: 'required' });
  assert.equal(validateApplication({ amountText: '20000', installments: 12 }, limits).errors.amount, 'aboveLimit');
  assert.equal(validateApplication({ amountText: '1000', installments: 30 }, limits).errors.installments, 'outOfRange');
  assert.equal(validateApplication({ amountText: '0.05', installments: 12 }, limits).errors.amount, 'tooSmall');
  const ok = validateApplication({ amountText: '4800', installments: 12 }, limits);
  assert.equal(ok.ok, true);
  assert.equal(estimatedInstallment(ok.amount, 12), 400);
  assert.equal(estimatedInstallment(1000, 3), 333.33);
});

test('loan summary uses the server balance and finds the next unpaid instalment', () => {
  const loan: Loan = {
    id: 'l1', loanTypeId: 't1', loanTypeName: 'Personal Loan', loanNumber: 'LN-1', requestedAmount: 4800,
    approvedAmount: 4800, requestedInstallments: 12, approvedInstallments: 12, installmentAmount: 400,
    totalRepaid: 2000, outstandingBalance: 2800, status: 'Active', rejectionReason: null,
    repaymentMethod: 'PayrollDeduction', currency: 'SAR', createdAtUtc: '2026-04-20T00:00:00Z',
  };
  const rows: LoanInstallment[] = Array.from({ length: 12 }, (_, i) => ({
    id: `i${i + 1}`, installmentNumber: i + 1, dueDate: `2026-${String(i + 5).padStart(2, '0')}-25`.replace('2026-13', '2027-01').replace('2026-14', '2027-02').replace('2026-15', '2027-03').replace('2026-16', '2027-04'),
    amountDue: 400, amountPaid: i < 5 ? 400 : 0, status: i < 5 ? 'Paid' : 'Pending', paidDate: null,
  }));
  const summary = summarizeLoan(loan, rows);
  assert.equal(summary.remaining, 2800);
  assert.equal(summary.paidCount, 5);
  assert.equal(summary.scheduledCount, 12);
  assert.equal(summary.next?.installmentNumber, 6);
  assert.equal(summary.next?.dueDate, '2026-10-25');
  assert.ok(Math.abs((summary.progress ?? 0) - 2000 / 4800) < 1e-9);
  assert.equal(installmentStatus({ status: 'Pending', amountDue: 400, amountPaid: 100 }).key, 'partial');
  assert.equal(installmentStatus({ status: 'Cancelled', amountDue: 400, amountPaid: 0 }).key, 'cancelled');
  assert.equal(loanStatus('Active').key, 'active');
  assert.equal(loanStatus('Mystery').key, 'unknown');
});

test('a switched-off module and a missing permission are different honest states', () => {
  assert.equal(loansAccessFromError(403, 'feature_not_enabled'), 'moduleOff');
  assert.equal(loansAccessFromError(403, 'loans_read_forbidden'), 'forbidden');
  assert.equal(loansAccessFromError(400, 'no_linked_employee'), 'notLinked');
  assert.equal(loansAccessFromError(500, undefined), 'error');
});
