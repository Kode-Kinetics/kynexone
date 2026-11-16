import { test, expect } from '@playwright/test';
import { opensLeaveDrawer } from '../src/components/dashboard/dashboardModel';
import { pickServerMessage } from '../src/lib/glCostAccounting';

// The leave drawer decides a request inline, so it must only open for a real leave request. The
// row's entity type (the queue's `module`) decides that, never the words in its title.
test.describe('dashboard leave drawer: which rows open it', () => {
  test('only a LeaveRequest entity opens the drawer', () => {
    expect(opensLeaveDrawer('LeaveRequest')).toBe(true);
    expect(opensLeaveDrawer('leaverequest')).toBe(true);
  });
  test('leave-related entities that are not leave requests do not', () => {
    expect(opensLeaveDrawer('LeaveEncashmentRequest')).toBe(false);
    expect(opensLeaveDrawer('LeaveCancellationRequest')).toBe(false);
    expect(opensLeaveDrawer('Leave')).toBe(false);
    expect(opensLeaveDrawer('EmployeeChangeRequest')).toBe(false);
    expect(opensLeaveDrawer('')).toBe(false);
  });
});

test.describe('dashboard leave drawer: the server refusal in the viewer language', () => {
  const body = { message: 'Balance is below the request.', messageAr: 'الرصيد أقل من المطلوب.' };
  test('Arabic prefers messageAr, English prefers message, each falls back to the other', () => {
    expect(pickServerMessage(body, 'ar')).toBe(body.messageAr);
    expect(pickServerMessage(body, 'en')).toBe(body.message);
    expect(pickServerMessage({ message: 'x' }, 'ar')).toBe('x');
    expect(pickServerMessage({}, 'en')).toBeNull();
  });
});
