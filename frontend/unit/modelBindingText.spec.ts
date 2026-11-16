import { expect, test } from '@playwright/test';
import { isModelBindingText } from '../src/lib/modelBindingText';
import { loanErrorMessage } from '../src/lib/loanWorkflow';
import { apiErrorReason } from '../src/api/client';
import { describeApiError } from '../src/lib/apiError';

// Demo R1 F1: ASP.NET's model-binding sentences are never shown to a person.

const RAW = {
  title: 'One or more validation errors occurred.',
  errors: {
    req: ['The req field is required.'],
    '$.effectiveFrom': ["The JSON value could not be converted to Zayra.Api.Controllers.PublishMatrixRequest. Path: $ | LineNumber: 0 | BytePositionInLine: 120."],
  },
};
const http = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });
const FALLBACK = 'Unable to publish benefits by grade.';

test('the binder sentences are recognised', () => {
  expect(isModelBindingText('The req field is required.')).toBe(true);
  expect(isModelBindingText('The JSON value could not be converted to X. Path: $.a | LineNumber: 0')).toBe(true);
});

test('a domain refusal that happens to say "required" is not hidden', () => {
  expect(isModelBindingText('The Reason field is required.')).toBe(false);
  expect(isModelBindingText('An effective date is required for a scheduled change.')).toBe(false);
});

test('Benefits by grade shows its own sentence, not the binder text', () => {
  expect(loanErrorMessage(http(400, RAW), FALLBACK)).toBe(FALLBACK);
  expect(loanErrorMessage(http(400, { detail: 'The JSON value could not be converted to X.' }), FALLBACK)).toBe(FALLBACK);
});

test('a real server sentence still comes through', () => {
  expect(loanErrorMessage(http(400, { message: 'A value already starts on that day.' }), FALLBACK)).toBe('A value already starts on that day.');
});

test('the shared mappers drop the binder text too', () => {
  expect(apiErrorReason(http(400, RAW), FALLBACK)).toBe(FALLBACK);
  expect(describeApiError(http(400, RAW))).not.toMatch(/req field|JSON value/);
});
