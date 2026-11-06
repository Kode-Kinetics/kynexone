/**
 * The Saudi Compliance action centre receives its "Fix" link from the server. Three of them are
 * `/people?filter=<name>`, but the People list has no `filter` key, so the user used to land on the unfiltered list while
 * the card promised "complete the missing fields for each blocked employee".
 *
 * No existing directory filter selects the people the card counts: `readiness=Blocked` is the activation gate (Active
 * employees are rarely Blocked) and `readiness=NotReady` is a superset of "IBAN missing or GOSI reference missing". A
 * filter that selects a different population is worse than none, so the dead key is dropped and the link opens the People
 * list, labelled for what it is (`complianceActionLabel`). A real filter needs the backend:
 * GET /api/employees?gap=qiwa_blocked|missing_iban|missing_gosi_ref.
 */
export const COMPLIANCE_PEOPLE_LABEL = 'Open People: incomplete records are marked';

export function resolveComplianceRoute(route: string): string {
  const [pathAndQuery, hash] = route.split('#');
  const [pathname, query = ''] = pathAndQuery.split('?');
  if (pathname !== '/people') return route;
  const params = new URLSearchParams(query);
  if (!params.has('filter')) return route;
  params.delete('filter');
  const q = params.toString();
  return `${pathname}${q ? `?${q}` : ''}${hash ? `#${hash}` : ''}`;
}

/** The English label (a translation key): People links say what they open; everything else keeps "Fix". */
export function complianceActionLabel(route: string): string {
  return route.split(/[?#]/)[0] === '/people' ? COMPLIANCE_PEOPLE_LABEL : 'Fix';
}
