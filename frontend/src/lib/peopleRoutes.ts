export type EmployeeSection =
  | 'overview'
  | 'personal'
  | 'employment'
  | 'pay-benefits'
  | 'compliance-documents'
  | 'access-onboarding'
  | 'activity';

export interface PeopleDirectoryState {
  search?: string;
  status?: string;
  readiness?: string;
  access?: string;
  importBatchId?: string;
  gapType?: string;
  view?: 'current' | 'ex';
  page?: number;
  anchor?: string;
}

export interface EmployeeWorkspaceHrefOptions {
  section?: EmployeeSection;
  panel?: string;
  edit?: boolean;
  mode?: 'guided';
  returnTo?: string;
}

type DirectoryStringKey = Exclude<keyof PeopleDirectoryState, 'view' | 'page' | 'anchor'>;

const DIRECTORY_STRING_KEYS: readonly DirectoryStringKey[] = [
  'search',
  'status',
  'readiness',
  'access',
  'importBatchId',
  'gapType',
];

const LEGACY_TAB_TARGETS: Readonly<Record<string, { section: EmployeeSection; panel?: string }>> = {
  personal: { section: 'personal' },
  employment: { section: 'employment', panel: 'details' },
  payroll: { section: 'pay-benefits', panel: 'payroll' },
  benefits: { section: 'pay-benefits', panel: 'benefits' },
  package: { section: 'pay-benefits', panel: 'package' },
  deductions: { section: 'pay-benefits', panel: 'deductions' },
  compliance: { section: 'compliance-documents', panel: 'compliance' },
  documents: { section: 'compliance-documents', panel: 'documents' },
  history: { section: 'activity', panel: 'history' },
  transfers: { section: 'employment', panel: 'transfers' },
  development: { section: 'activity', panel: 'development' },
};

function nonEmpty(value: string | null): string | undefined {
  return value === null || value.length === 0 ? undefined : value;
}

export function parsePeopleDirectoryState(params: URLSearchParams): PeopleDirectoryState {
  const state: PeopleDirectoryState = {};

  for (const key of DIRECTORY_STRING_KEYS) {
    const value = nonEmpty(params.get(key));
    if (value !== undefined) state[key] = value;
  }

  const view = params.get('view');
  if (view === 'current' || view === 'ex') state.view = view;

  const rawPage = params.get('page');
  if (rawPage !== null && /^[1-9]\d*$/.test(rawPage)) {
    const page = Number(rawPage);
    if (Number.isSafeInteger(page)) state.page = page;
  }

  const anchor = nonEmpty(params.get('anchor'));
  if (anchor !== undefined) state.anchor = anchor;

  return state;
}

export function serializePeopleDirectoryState(state: PeopleDirectoryState): URLSearchParams {
  const params = new URLSearchParams();

  for (const key of DIRECTORY_STRING_KEYS) {
    const value = state[key];
    if (typeof value === 'string' && value.length > 0) params.set(key, value);
  }

  if (state.view === 'current' || state.view === 'ex') params.set('view', state.view);
  if (Number.isSafeInteger(state.page) && (state.page ?? 0) > 0) params.set('page', String(state.page));
  if (typeof state.anchor === 'string' && state.anchor.length > 0) params.set('anchor', state.anchor);

  return params;
}

export function buildPeopleReturnTo(state: PeopleDirectoryState): string {
  const query = serializePeopleDirectoryState(state).toString();
  return query ? `/people?${query}` : '/people';
}

function hasMalformedPercentEncoding(value: string): boolean {
  return /%(?![0-9a-f]{2})/i.test(value);
}

function sanitizeInternalPeopleReturnTo(raw: string): string | undefined {
  if (!raw.startsWith('/') || raw.startsWith('//') || raw !== raw.trim() || hasMalformedPercentEncoding(raw)) {
    return undefined;
  }
  try {
    const target = new URL(raw, 'https://people-return.invalid');
    if (target.origin !== 'https://people-return.invalid' || target.pathname !== '/people' || target.hash) {
      return undefined;
    }
    return buildPeopleReturnTo(parsePeopleDirectoryState(target.searchParams));
  } catch {
    return undefined;
  }
}

export function validatePeopleReturnTo(raw: string | null | undefined, browserOrigin: string): string {
  if (!raw || raw !== raw.trim() || raw.startsWith('//') || hasMalformedPercentEncoding(raw)) return '/people';

  try {
    const base = new URL(browserOrigin);
    const target = new URL(raw, base.origin);
    if (target.origin !== base.origin || target.username || target.password) return '/people';
    if (target.pathname !== '/people' || target.hash) return '/people';
    return buildPeopleReturnTo(parsePeopleDirectoryState(target.searchParams));
  } catch {
    return '/people';
  }
}

export function employeeWorkspaceHref(employeeId: number, options: EmployeeWorkspaceHrefOptions = {}): string {
  if (!Number.isSafeInteger(employeeId) || employeeId <= 0) {
    throw new RangeError('Employee ID must be a positive integer.');
  }

  const editing = options.edit === true || options.mode === 'guided';
  const path = `/people/${employeeId}${editing ? '/edit' : ''}`;
  const params = new URLSearchParams();
  if (editing && options.mode) params.set('mode', options.mode);
  if (options.section) params.set('section', options.section);
  if (options.panel) params.set('panel', options.panel);
  const returnTo = options.returnTo ? sanitizeInternalPeopleReturnTo(options.returnTo) : undefined;
  if (returnTo) params.set('returnTo', returnTo);
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}

export function legacyEmployeeTarget(params: URLSearchParams): string | null {
  const rawEmployeeId = params.get('employeeId');
  if (!rawEmployeeId || !/^[1-9]\d*$/.test(rawEmployeeId)) return null;
  const employeeId = Number(rawEmployeeId);
  if (!Number.isSafeInteger(employeeId)) return null;

  const target = LEGACY_TAB_TARGETS[params.get('tab') ?? ''] ?? { section: 'overview' as const };
  const directoryState = parsePeopleDirectoryState(params);
  const hasDirectoryState = Object.keys(directoryState).length > 0;
  return employeeWorkspaceHref(employeeId, {
    ...target,
    returnTo: hasDirectoryState ? buildPeopleReturnTo(directoryState) : undefined,
  });
}
