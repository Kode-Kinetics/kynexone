import { expect, test } from '@playwright/test';
import { SUBSCRIPTION_ALERT_HREF, isOnPlanAndBilling } from '../src/lib/subscriptionAlert';
import { legacySetupHref } from '../src/lib/setupLegacyRoutes';

test('a 402 is sent to Setup → Plan and billing with the subscription alert', () => {
  expect(SUBSCRIPTION_ALERT_HREF).toBe('/setup?tab=planBilling&alert=subscription');
});

test('the old Tenant Admin alert address leads to the same place', () => {
  expect(legacySetupHref('/tenant-admin', new URLSearchParams('alert=subscription'))).toBe(SUBSCRIPTION_ALERT_HREF);
});

test('a 402 on Plan and billing itself is not redirected again, anywhere else it is', () => {
  // When the subscription is inactive the screen's own calls answer 402 as well: redirecting from it would loop.
  expect(isOnPlanAndBilling('/setup', '?tab=planBilling&alert=subscription')).toBe(true);
  expect(isOnPlanAndBilling('/setup', '?tab=planBilling')).toBe(true);
  expect(isOnPlanAndBilling('/setup/', '?tab=planBilling')).toBe(true);
  expect(isOnPlanAndBilling('/setup', '?tab=users')).toBe(false);
  expect(isOnPlanAndBilling('/setup', '')).toBe(false);
  expect(isOnPlanAndBilling('/dashboard', '?tab=planBilling')).toBe(false);
});
