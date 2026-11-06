import { serializePeopleDirectoryState, type PeopleDirectoryState } from './peopleRoutes';

/**
 * The Saudi Compliance action centre receives its "Fix" link from the server. Three of them are
 * `/people?filter=<name>`, but the People list has no `filter` key (it parses `readiness`,
 * `gapType`, `status`, ...), so the user used to land on the full unfiltered list while the card
 * said "complete the missing fields for each blocked employee".
 *
 * Each server filter is mapped onto the closest directory filter that exists. The readiness filter
 * is the server's own worklist of employees whose required details are incomplete:
 *   qiwa_blocked      -> readiness=Blocked    (missing a field the activation gate requires: ID, DOB, job title)
 *   missing_iban      -> readiness=NotReady   (pay details are incomplete; NotReady = anything not Ready)
 *   missing_gosi_ref  -> readiness=NotReady   (GOSI reference / salary structure are pay-readiness items)
 * `gapType` is not used: it filters import gaps (e.g. "bank details to verify"), which is a different
 * population from "IBAN missing or invalid". An unknown filter name is dropped rather than carried
 * to a page that ignores it. The server may later send readiness directly; that passes through.
 */
const PEOPLE_FILTERS: Readonly<Record<string, PeopleDirectoryState>> = {
  qiwa_blocked: { readiness: 'Blocked' },
  missing_iban: { readiness: 'NotReady' },
  missing_gosi_ref: { readiness: 'NotReady' },
};

export function resolveComplianceRoute(route: string): string {
  const [pathAndQuery, hash] = route.split('#');
  const [pathname, query = ''] = pathAndQuery.split('?');
  if (pathname !== '/people') return route;
  const params = new URLSearchParams(query);
  const name = params.get('filter');
  if (name === null) return route;
  params.delete('filter');
  const mapped = PEOPLE_FILTERS[name];
  if (mapped) for (const [key, value] of serializePeopleDirectoryState(mapped)) if (!params.has(key)) params.set(key, value);
  const q = params.toString();
  return `${pathname}${q ? `?${q}` : ''}${hash ? `#${hash}` : ''}`;
}
