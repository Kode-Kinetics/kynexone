import { test, expect } from '@playwright/test';
import { electionOptions, electionsComplete, readErpPostedRefusal, readRequiredElections } from '../src/lib/voidElections';

// The void endpoint refuses (HTTP 422) a run whose net pay or statutory remittance already left the bank until the operator
// STATES what happened to that cash. This is the body the server actually sends (PayrollController.VoidRun +
// PayrollVoidService §5a): `detail.requiredElections` lists every outstanding election at once.
const settlementElection = {
  code: 'settlement_disposition_required', field: 'settlementDisposition', eventType: 'PayrollNetSettlement', amount: 30675.42,
  cashAccount: '1000 - Cash/Bank', period: '2026-11', referenceField: 'settlementReference', referenceRequiredIfRecalled: true,
  message: 'This run has already DISBURSED net pay …',
};
const gosiElection = {
  code: 'remittance_disposition_required', field: 'remittanceDisposition', eventType: 'PayrollRemit:GOSI', amount: 5725,
  cashAccount: '1000 - Cash/Bank', period: '2026-11', referenceField: 'remittanceReference', referenceRequiredIfRecalled: true,
  message: 'This run has already DISBURSED the GOSI remittance …',
};
const loanElection = { ...gosiElection, eventType: 'PayrollRemit:LOAN', amount: 666.66 };

const refusal = (elections: unknown[], status = 422) => ({
  response: {
    status,
    data: {
      error: 'remittance_disposition_required', message: 'This run has already DISBURSED …',
      detail: { requiredElections: elections, outstandingElectionCount: elections.length },
    },
  },
});

test('reads the 422 the server sends: one choice per request field, the money behind it listed', () => {
  const groups = readRequiredElections(refusal([gosiElection, settlementElection, loanElection]));
  expect(groups).not.toBeNull();
  expect(groups!.map(g => g.field)).toEqual(['settlementDisposition', 'remittanceDisposition']);
  const [settlement, remittance] = groups!;
  expect(settlement.referenceField).toBe('settlementReference');
  expect(settlement.total).toBeCloseTo(30675.42, 2);
  expect(settlement.items).toEqual([{ label: null, amount: 30675.42, period: '2026-11', cashAccount: '1000 - Cash/Bank' }]);
  expect(remittance.referenceField).toBe('remittanceReference');
  expect(remittance.items.map(i => i.label)).toEqual(['GOSI', 'LOAN']);
  expect(remittance.total).toBeCloseTo(6391.66, 2);
});

test('anything that is not an elections refusal is not read as one', () => {
  expect(readRequiredElections(refusal([gosiElection], 403))).toBeNull();
  expect(readRequiredElections({ response: { status: 422, data: { error: 'gl_period_closed', message: 'closed' } } })).toBeNull();
  expect(readRequiredElections({ response: { status: 422, data: { error: 'x', detail: { requiredElections: [] } } } })).toBeNull();
  expect(readRequiredElections(new Error('boom'))).toBeNull();
  expect(readRequiredElections(null)).toBeNull();
});

test('a reference-only refusal (disposition already sent) is still one choice group', () => {
  const groups = readRequiredElections(refusal([{
    code: 'settlement_recall_reference_required', field: 'settlementReference', eventType: 'PayrollNetSettlement', amount: 100,
    cashAccount: '1000 - Cash/Bank', period: '2026-11', disposition: 'FundsRecalled', message: 'A recall reference is required …',
  }]));
  expect(groups!.map(g => g.field)).toEqual(['settlementDisposition']);
});

test('the choices are complete only when every group has a choice and a recall carries its reference', () => {
  const groups = readRequiredElections(refusal([gosiElection, settlementElection]))!;
  expect(electionsComplete(groups, {})).toBe(false);
  expect(electionsComplete(groups, { settlementDisposition: 'FundsDisbursed' })).toBe(false);
  expect(electionsComplete(groups, { settlementDisposition: 'FundsDisbursed', remittanceDisposition: 'FundsDisbursed' })).toBe(true);
  expect(electionsComplete(groups, { settlementDisposition: 'FundsRecalled', remittanceDisposition: 'FundsDisbursed' })).toBe(false);
  expect(electionsComplete(groups, { settlementDisposition: 'FundsRecalled', settlementReference: '   ', remittanceDisposition: 'FundsDisbursed' })).toBe(false);
  expect(electionsComplete(groups, { settlementDisposition: 'FundsRecalled', settlementReference: 'RCL-1', remittanceDisposition: 'FundsDisbursed' })).toBe(true);
});

test('the options sent with the resubmitted void are exactly what was chosen; a reference is only sent with a recall', () => {
  expect(electionOptions({
    settlementDisposition: 'FundsRecalled', settlementReference: ' RCL-NET-1 ',
    remittanceDisposition: 'FundsDisbursed', remittanceReference: 'ignored',
  })).toEqual({
    settlementDisposition: 'FundsRecalled', settlementReference: 'RCL-NET-1', remittanceDisposition: 'FundsDisbursed',
  });
  expect(electionOptions({})).toEqual({});
});

// A run whose journal the ERP already holds is refused (HTTP 422, error "erp_already_posted") until the operator acknowledges they
// will raise the correcting document in the ERP as well. The server sends the reference in `detail`.
test('reads the erp_already_posted refusal and the reference the ERP holds', () => {
  const err = { response: { status: 422, data: { error: 'erp_already_posted', message: 'POSTED in the ERP', detail: { erpPostingStatus: 'Posted', erpPostingReference: 'SAP-1900001' } } } };
  expect(readErpPostedRefusal(err)).toEqual({ reference: 'SAP-1900001' });
  expect(readErpPostedRefusal({ response: { status: 422, data: { error: 'erp_already_posted', detail: { erpPostingStatus: 'Posted', erpPostingReference: null } } } })).toEqual({ reference: null });
});

test('any other refusal is not the ERP acknowledgement', () => {
  expect(readErpPostedRefusal({ response: { status: 422, data: { error: 'remittance_disposition_required' } } })).toBeNull();
  expect(readErpPostedRefusal({ response: { status: 500, data: { error: 'erp_already_posted' } } })).toBeNull();
  expect(readErpPostedRefusal(new Error('network'))).toBeNull();
  expect(readErpPostedRefusal(null)).toBeNull();
});
