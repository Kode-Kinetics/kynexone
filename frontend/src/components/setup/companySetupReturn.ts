/**
 * The company-setup branch of SetupWorkspaceReturn (docs/design/company-setup-wizard.md, Addendum B4).
 *
 * When a More settings entry in the wizard opens its own page, the link carries
 * `returnTo=companySetup&draft={id}&part={part}` (+ `company={id}` when completing an existing company). The URL is
 * the source of truth; a per-tab sessionStorage copy covers pages that drop query parameters as they navigate,
 * expires after 12 hours and is cleared when the wizard loads.
 *
 * Nothing here ever turns stored or query text into a link: the address is rebuilt from validated ids and an
 * allow-listed part, so there is no open redirect.
 */
export const COMPANY_SETUP_RETURN = 'companySetup';
export const RETURN_PARTS = ['costCentres', 'departments', 'grades', 'designations', 'workRules', 'leave', 'endOfService', 'moreSettings'] as const;
export type ReturnPart = (typeof RETURN_PARTS)[number];
export const COMPANY_SETUP_RETURN_KEY = 'kynexone-company-setup-return:v1';
export const COMPANY_SETUP_RETURN_TTL_MS = 12 * 60 * 60 * 1000;

export interface CompanySetupReturn {
  draftId: string;
  /** Absent for a new company (create mode). */
  companyId?: string;
  part: ReturnPart;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isGuid = (value: unknown): value is string => typeof value === 'string' && GUID.test(value);
const isPart = (value: unknown): value is ReturnPart => typeof value === 'string' && (RETURN_PARTS as readonly string[]).includes(value);

/** The two wizard routes: create (/setup/new-company) and complete an existing company (/setup/company/{id}/complete). */
export function isCompanySetupPath(pathname: string | null | undefined): boolean {
  return !!pathname && (pathname === '/setup/new-company' || /^\/setup\/company\/[^/]+\/complete$/.test(pathname));
}

/** Validated return, or null. Every id must be a GUID and the part on the allow-list. */
export function parseCompanySetupReturn(params: { get(name: string): string | null } | null | undefined): CompanySetupReturn | null {
  if (!params || params.get('returnTo') !== COMPANY_SETUP_RETURN) return null;
  const draftId = params.get('draft');
  const companyId = params.get('company');
  const part = params.get('part') ?? 'moreSettings';
  if (!isGuid(draftId) || !isPart(part) || (companyId !== null && !isGuid(companyId))) return null;
  return companyId ? { draftId, companyId, part } : { draftId, part };
}

/** The wizard address the bar links to, rebuilt from validated parts only. */
export function companySetupReturnHref(value: CompanySetupReturn): string {
  const draft = `draft=${encodeURIComponent(value.draftId)}&part=${encodeURIComponent(value.part)}`;
  return value.companyId
    ? `/setup/company/${encodeURIComponent(value.companyId)}/complete?${draft}`
    : `/setup/new-company?${draft}`;
}

/** `href` with the return parameters appended (an existing query and #fragment are kept). Invalid input: href unchanged. */
export function withCompanySetupReturn(href: string, value: CompanySetupReturn | null): string {
  if (!value || !isGuid(value.draftId) || !isPart(value.part) || (value.companyId !== undefined && !isGuid(value.companyId))) return href;
  const [beforeHash, hash = ''] = href.split('#', 2);
  const params = new URLSearchParams({ returnTo: COMPANY_SETUP_RETURN, ...(value.companyId ? { company: value.companyId } : {}), draft: value.draftId, part: value.part });
  return `${beforeHash}${beforeHash.includes('?') ? '&' : '?'}${params.toString()}${hash ? `#${hash}` : ''}`;
}

type Store = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
function session(): Store | null {
  try { return typeof window === 'undefined' ? null : window.sessionStorage; } catch { return null; }
}

export function rememberCompanySetupReturn(value: CompanySetupReturn, store: Store | null = session(), now = Date.now()): void {
  if (!store || !isGuid(value.draftId) || !isPart(value.part) || (value.companyId !== undefined && !isGuid(value.companyId))) return;
  try { store.setItem(COMPANY_SETUP_RETURN_KEY, JSON.stringify({ ...value, at: now })); } catch { /* storage blocked: the URL still carries it */ }
}

export function forgetCompanySetupReturn(store: Store | null = session()): void {
  try { store?.removeItem(COMPANY_SETUP_RETURN_KEY); } catch { /* ignore */ }
}

/** The stored fallback, or null when absent, malformed, invalid or older than 12 hours (then it is also removed). */
export function readCompanySetupReturn(store: Store | null = session(), now = Date.now()): CompanySetupReturn | null {
  if (!store) return null;
  let raw: string | null;
  try { raw = store.getItem(COMPANY_SETUP_RETURN_KEY); } catch { return null; }
  if (!raw) return null;
  try {
    const value = JSON.parse(raw) as Partial<CompanySetupReturn & { at: number }> | null;
    const at = typeof value?.at === 'number' ? value.at : NaN;
    const fresh = Number.isFinite(at) && now - at <= COMPANY_SETUP_RETURN_TTL_MS && at - now <= 60_000;
    const companyOk = value?.companyId === undefined || isGuid(value.companyId);
    if (fresh && isGuid(value?.draftId) && isPart(value?.part) && companyOk)
      return value.companyId ? { draftId: value.draftId, companyId: value.companyId, part: value.part } : { draftId: value.draftId, part: value.part };
  } catch { /* fall through */ }
  forgetCompanySetupReturn(store);
  return null;
}
