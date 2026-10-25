/**
 * Where someone whose subscription is inactive or expired is sent (a 402 from the API): Setup → Plan and billing, with
 * the alert. It was Tenant Admin's subscription tab; /tenant-admin?alert=subscription still arrives here through the
 * redirect in middleware.ts.
 */
export const SUBSCRIPTION_ALERT_HREF = '/setup?tab=planBilling&alert=subscription';

/**
 * Whether the address bar is already on Plan and billing. A 402 is not sent there again: when the subscription is
 * inactive that screen's own calls also answer 402, so a redirect from it to itself would reload forever.
 */
export function isOnPlanAndBilling(pathname: string, search: string): boolean {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return path === '/setup' && new URLSearchParams(search).get('tab') === 'planBilling';
}
