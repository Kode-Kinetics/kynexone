import { expect, test } from '@playwright/test';
import { enumLabel, normaliseEnumValue } from '../src/i18n/enumLabel';
import { describeApiError } from '../src/lib/apiError';
import { translate, type MessageParams } from '../src/i18n/translations';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);

test.describe('enum labels', () => {
  test('a workflow-specific value uses its own family', () => {
    expect(enumLabel(en, 'LeaveRequestStatus', 'PendingManagerApproval')).toBe('Pending manager approval');
    expect(enumLabel(ar, 'LeaveRequestStatus', 'PendingManagerApproval')).toBe('بانتظار موافقة المدير');
    expect(enumLabel(ar, 'PayrollRunStatus', 'PendingFinanceReview')).toBe('بانتظار المراجعة المالية');
  });

  test('a shared value falls back to the Status family', () => {
    expect(enumLabel(ar, 'LoanStatus', 'Approved')).toBe('معتمد');
    expect(enumLabel(en, 'OvertimeStatus', 'SentBack')).toBe('Sent back');
  });

  test('values with spaces or odd casing are normalised before lookup', () => {
    expect(normaliseEnumValue('Half day')).toBe('HalfDay');
    expect(normaliseEnumValue('on leave')).toBe('OnLeave');
    expect(enumLabel(ar, 'AttendanceStatus', 'Half day')).toBe('نصف يوم');
  });

  test('an unknown value degrades to readable English, never blank or raw PascalCase', () => {
    expect(enumLabel(ar, 'LoanStatus', 'AwaitingTreasurySignOff')).toBe('Awaiting treasury sign off');
    expect(enumLabel(en, 'Status', '')).toBe('—');
    expect(enumLabel(en, 'Status', null)).toBe('—');
  });
});

test.describe('API error sentences', () => {
  const axiosError = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });

  test('never renders the raw response body', () => {
    const msg = describeApiError(axiosError(400, { weird: { nested: true } }), en);
    expect(msg).not.toContain('{');
    expect(msg).not.toContain('weird');
    expect(msg).toBe('The server returned an error (HTTP 400).');
  });

  test('a specific backend code is translated', () => {
    expect(describeApiError(axiosError(409, { code: 'request_not_pending', message: 'Request is Approved.' }), ar))
      .toBe('لم يعد هذا الطلب قيد الانتظار، لذا لا يمكن تغييره.');
    expect(describeApiError(axiosError(422, { code: 'gl_period_closed', message: 'x' }), en)).toContain('accounting period is closed');
  });

  test('a generic code keeps the server’s specific sentence', () => {
    expect(describeApiError(axiosError(400, { code: 'bad_request', message: 'Loan amount exceeds the policy limit.' }), ar))
      .toBe('Loan amount exceeds the policy limit.');
  });

  test('a 403 keeps the domain reason it was sent with, e.g. a separation-of-duties refusal', () => {
    expect(describeApiError(axiosError(403, { code: 'forbidden', message: 'You cannot decide a request you submitted.' }), en))
      .toBe('You cannot decide a request you submitted.');
  });

  test('validation problem details list the field messages', () => {
    expect(describeApiError(axiosError(400, { title: 'One or more validation errors occurred.', errors: { Amount: ['Amount must be positive.'] } }), en))
      .toBe('Amount must be positive.');
  });

  test('server errors are generic, translated, and carry the trace reference', () => {
    const msg = describeApiError(axiosError(500, { code: 'internal_error', message: 'An unexpected error occurred.', traceId: '0HN5:0001' }), ar);
    expect(msg).toBe('حدث خطأ من جهتنا. يرجى إعادة المحاولة. المرجع: 0HN5:0001');
  });

  test('status and network fallbacks', () => {
    expect(describeApiError({ isAxiosError: true }, en)).toContain('could not be reached');
    expect(describeApiError(axiosError(403, null), ar)).toBe('ليست لديك صلاحية لهذا الإجراء.');
    expect(describeApiError(axiosError(502, '<html>Bad gateway</html>'), en)).toBe('The service is temporarily unavailable. Retry in a minute.');
    expect(describeApiError(new Error('Request failed with status code 500'), en)).toBe('Something went wrong. Please retry.');
  });
});
