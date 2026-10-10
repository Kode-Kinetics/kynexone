import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ADDRESSEE_MAX,
  PURPOSE_MAX,
  letterFileName,
  letterRequestBody,
  letterServerError,
  letterStatus,
  letterTypeName,
  openLetterTypes,
  validateLetterDraft,
  type LetterRequest,
  type LetterType,
} from '../src/features/letters/letterRequest.ts';

const TYPES: LetterType[] = [
  { letterType: 'SalaryCertificate', nameEn: 'Salary Certificate', nameAr: 'شهادة راتب' },
  { letterType: 'ExperienceCertificate', nameEn: 'Experience Certificate', nameAr: '' },
];

function request(overrides: Partial<LetterRequest>): LetterRequest {
  return {
    id: 'r1', letterType: 'SalaryCertificate', language: 'bilingual', purpose: '', addresseeName: '',
    status: 'Pending', createdAtUtc: '2026-10-10T10:00:00Z', decidedAtUtc: null, decisionNote: '',
    referenceNumber: null, isIssued: false, ...overrides,
  };
}

test('a draft needs an offered letter type that has no open request', () => {
  const draft = { letterType: null, language: 'bilingual' as const, purpose: '', addresseeName: '' };
  assert.deepEqual(validateLetterDraft(draft, TYPES, new Set()).errors, { letterType: 'required' });
  assert.equal(validateLetterDraft({ ...draft, letterType: 'Payslip' }, TYPES, new Set()).errors.letterType, 'notOffered');
  const open = openLetterTypes([request({ status: 'Pending' }), request({ id: 'r2', letterType: 'ExperienceCertificate', status: 'Issued', isIssued: true })]);
  assert.deepEqual([...open], ['SalaryCertificate']);
  assert.equal(validateLetterDraft({ ...draft, letterType: 'SalaryCertificate' }, TYPES, open).errors.letterType, 'alreadyOpen');
  const ok = validateLetterDraft({ ...draft, letterType: 'ExperienceCertificate' }, TYPES, open);
  assert.equal(ok.ok, true);
});

test('optional text is length-checked and sent trimmed, or as null when empty', () => {
  const base = { letterType: 'SalaryCertificate', language: 'ar' as const, purpose: '', addresseeName: '' };
  assert.equal(validateLetterDraft({ ...base, purpose: 'x'.repeat(PURPOSE_MAX + 1) }, TYPES, new Set()).errors.purpose, 'tooLong');
  assert.equal(validateLetterDraft({ ...base, addresseeName: 'x'.repeat(ADDRESSEE_MAX + 1) }, TYPES, new Set()).errors.addresseeName, 'tooLong');
  assert.deepEqual(letterRequestBody({ ...base, purpose: '  Bank account  ', addresseeName: '   ' }), {
    letterType: 'SalaryCertificate', language: 'ar', purpose: 'Bank account', addresseeName: null,
  });
  assert.throws(() => letterRequestBody({ ...base, letterType: null }));
});

test('server statuses map to a tone and a plain-language key; unknown never reads as issued', () => {
  assert.deepEqual(letterStatus(request({ status: 'Pending' })), { key: 'pending', tone: 'warning' });
  assert.deepEqual(letterStatus(request({ status: 'Issued', isIssued: true })), { key: 'issued', tone: 'success' });
  assert.deepEqual(letterStatus(request({ status: 'Declined' })), { key: 'declined', tone: 'danger' });
  assert.deepEqual(letterStatus(request({ status: 'Cancelled' })), { key: 'cancelled', tone: 'neutral' });
  assert.deepEqual(letterStatus(request({ status: 'Weird' })), { key: 'unknown', tone: 'neutral' });
});

test('names come from the tenant template, Arabic when configured, else English', () => {
  assert.equal(letterTypeName('SalaryCertificate', TYPES, 'ar'), 'شهادة راتب');
  assert.equal(letterTypeName('ExperienceCertificate', TYPES, 'ar'), 'Experience Certificate');
  assert.equal(letterTypeName('Missing', TYPES, 'en'), null);
});

test('refusals map to message keys, and the cache file name has no path characters', () => {
  assert.equal(letterServerError(409, 'duplicate_pending_request'), 'duplicate');
  assert.equal(letterServerError(409, 'template_not_configured'), 'notConfigured');
  assert.equal(letterServerError(409, 'reprint_unavailable'), 'reprintUnavailable');
  assert.equal(letterServerError(503, undefined), 'busy');
  assert.equal(letterServerError(400, undefined), 'notLinked');
  assert.equal(letterServerError(500, undefined), 'generic');
  assert.equal(letterFileName({ id: 'abc', referenceNumber: 'SAL-CERT-2026/00042' }), 'SAL-CERT-2026-00042.pdf');
  assert.equal(letterFileName({ id: '../../etc', referenceNumber: null }), 'etc.pdf');
});
