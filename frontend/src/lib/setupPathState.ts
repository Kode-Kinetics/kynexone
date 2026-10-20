import { SETUP_AREAS, type SetupAreaId } from './setupAreas';

export type SetupStudioPath = 'guided' | 'configuration';

export type SetupPathResolution = {
  path: SetupStudioPath;
  area: SetupAreaId | 'overview';
  setting: string | null;
  companyId: string | null;
  needsReplace: boolean;
  notice?: 'unauthorized-company' | 'setting-mismatch';
};

export type SetupPathContext = {
  authorizedCompanyIds: ReadonlySet<string>;
  selectedCompanyId: string | null;
};

const destinationById = new Map(SETUP_AREAS.flatMap(area => area.destinations.map(destination => [destination.id, destination] as const)));
const areaIds = new Set<SetupAreaId>(SETUP_AREAS.map(area => area.id));

export function resolveSetupPath(params: URLSearchParams, context: SetupPathContext): SetupPathResolution {
  const requestedPath = params.get('path');
  const requestedArea = params.get('area');
  const requestedSetting = params.get('setting');
  const requestedCompany = params.get('companyId');
  let needsReplace = requestedPath !== null && requestedPath !== 'guided' && requestedPath !== 'configuration';
  let notice: SetupPathResolution['notice'];

  let companyId: string | null;
  if (requestedCompany) {
    if (context.authorizedCompanyIds.has(requestedCompany)) companyId = requestedCompany;
    else {
      companyId = null;
      notice = 'unauthorized-company';
      needsReplace = true;
    }
  } else {
    companyId = context.selectedCompanyId && context.authorizedCompanyIds.has(context.selectedCompanyId)
      ? context.selectedCompanyId
      : null;
  }

  const hasLegacyConfigurationTarget = requestedArea !== null || requestedSetting !== null;
  const path: SetupStudioPath = requestedPath === 'configuration'
    ? 'configuration'
    : requestedPath === 'guided'
      ? 'guided'
      : params.get('mode') === 'starter'
        ? 'guided'
        : hasLegacyConfigurationTarget
          ? 'configuration'
          : 'guided';
  if (requestedPath === null && hasLegacyConfigurationTarget && params.get('mode') !== 'starter') needsReplace = true;

  let area: SetupAreaId | 'overview' = requestedArea && areaIds.has(requestedArea as SetupAreaId)
    ? requestedArea as SetupAreaId
    : 'overview';
  if (requestedArea && area === 'overview' && requestedArea !== 'overview') needsReplace = true;

  let setting: string | null = null;
  if (requestedSetting) {
    const destination = destinationById.get(requestedSetting);
    if (!destination || (area !== 'overview' && destination.areaId !== area)) {
      notice ??= 'setting-mismatch';
      needsReplace = true;
    } else {
      setting = destination.id;
      if (area === 'overview') area = destination.areaId;
    }
  }

  return { path, area, setting, companyId, needsReplace, ...(notice ? { notice } : {}) };
}

const preservedKeys = ['department', 'level', 'setupArea', 'source'] as const;

export function serializeSetupPath(state: SetupPathResolution, preserve: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams();
  for (const key of preservedKeys) {
    for (const value of preserve.getAll(key)) next.append(key, value);
  }
  next.set('tab', 'aiSetup');
  next.set('path', state.path);
  if (state.area !== 'overview') next.set('area', state.area);
  if (state.setting) next.set('setting', state.setting);
  if (state.companyId) next.set('companyId', state.companyId);
  return next;
}
