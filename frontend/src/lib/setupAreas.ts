import { navigationItems } from '../routes/navigation';

export type SetupAreaId = 'organization' | 'recruitment' | 'onboarding' | 'time' | 'leave' | 'compensation' | 'payroll' | 'performance' | 'learning' | 'services' | 'offboarding' | 'compliance';

export interface SetupDestination {
  id: string;
  areaId: SetupAreaId;
  label: string;
  path: string;
  kind: 'settings' | 'templates' | 'operations';
  requiredPermissions: readonly string[];
  requiredFeatureKey?: string;
  editorRoles?: readonly string[];
  editorPermission?: string;
  scope: 'company' | 'tenant' | 'mixed';
  presentation: 'embedded' | 'route';
  assistance: 'existing-onboarding' | 'not-connected';
  businessGroup: { id: string; label: string };
  groupOrder: number;
  destinationOrder: number;
  effectiveTargetScope: 'company' | 'tenant' | 'group' | 'recordDependent';
}

export interface SetupAreaDefinition {
  id: SetupAreaId;
  label: string;
  description: string;
  destinations: readonly SetupDestination[];
}

type DestinationInput = Omit<SetupDestination, 'requiredPermissions' | 'requiredFeatureKey' | 'assistance' | 'effectiveTargetScope'> & {
  assistance?: SetupDestination['assistance'];
  effectiveTargetScope?: SetupDestination['effectiveTargetScope'];
};

type DestinationNavigation = Pick<SetupDestination, 'businessGroup' | 'groupOrder' | 'destinationOrder'>;

function navigation(id: string, label: string, groupOrder: number, destinationOrder: number): DestinationNavigation {
  return { businessGroup: { id, label }, groupOrder, destinationOrder };
}

const setupWriterPermissions = ['organization.write', 'organization.establishment.write'] as const;

// Setup's menu gate differs from its route and per-tab visibility. Mirror SetupPage's
// canWrite/canReadCompanies/canReadStatutoryRules permission branches for discovery;
// its additional role checks and each editor's write checks still apply at render/save.
function setupTabPermissions(id: string): readonly string[] {
  if (id === 'companies') return [...setupWriterPermissions, 'organization.read'];
  if (id === 'statutoryRules') return [...setupWriterPermissions, 'payroll.rates.read'];
  return setupWriterPermissions;
}

// Reuse canonical module permissions/flags. Setup resolves per-tab permissions above;
// dedicated subroutes inherit parent metadata. Route wrappers and APIs remain authoritative.
function destination(input: DestinationInput): SetupDestination {
  const pathname = input.path.split('?')[0];
  const route = navigationItems
    .filter(item => item.path && (pathname === item.path || pathname.startsWith(`${item.path}/`)))
    .sort((a, b) => (b.path?.length ?? 0) - (a.path?.length ?? 0))[0];
  if (!route) throw new Error(`Setup destination has no canonical route: ${input.path}`);
  return {
    ...input,
    assistance: input.assistance ?? 'not-connected',
    effectiveTargetScope: input.effectiveTargetScope ?? (input.scope === 'mixed' ? 'recordDependent' : input.scope),
    requiredPermissions: pathname === '/setup' ? setupTabPermissions(input.id) : route.requiredPermissions ?? [],
    requiredFeatureKey: route.requiredFeatureKey,
  };
}

function setting(areaId: SetupAreaId, id: string, label: string, scope: SetupDestination['scope'], nav: DestinationNavigation, effectiveTargetScope?: SetupDestination['effectiveTargetScope']): SetupDestination {
  return destination({ id, areaId, label, path: `/setup?tab=${id}`, kind: 'settings', scope, presentation: 'embedded', effectiveTargetScope, ...nav });
}

function route(areaId: SetupAreaId, id: string, label: string, path: string, kind: SetupDestination['kind'], scope: SetupDestination['scope'], nav: DestinationNavigation): SetupDestination {
  return destination({ id, areaId, label, path, kind, scope, presentation: 'route', ...nav });
}

/** Discovery only: available means the destination can be opened, never configured or editable. */
export const SETUP_AREAS: readonly SetupAreaDefinition[] = [
  {
    id: 'organization', label: 'Organization and core HR',
    description: 'Company structure, shared reference data, employment records and contracts.',
    destinations: [
      setting('organization', 'companies', 'Companies', 'company', navigation('legal-entities-structure', 'Legal entities and structure', 0, 0), 'group'),
      setting('organization', 'branches', 'Branches', 'company', navigation('legal-entities-structure', 'Legal entities and structure', 0, 1)),
      setting('organization', 'departments', 'Departments', 'tenant', navigation('legal-entities-structure', 'Legal entities and structure', 0, 2)),
      setting('organization', 'designations', 'Designations', 'tenant', navigation('legal-entities-structure', 'Legal entities and structure', 0, 3)),
      setting('organization', 'grades', 'Grades', 'tenant', navigation('legal-entities-structure', 'Legal entities and structure', 0, 4)),
      setting('organization', 'costCenters', 'Cost centers', 'mixed', navigation('financial-structure', 'Financial structure', 1, 0)),
      setting('organization', 'establishment', 'Cost centres and budget', 'mixed', navigation('financial-structure', 'Financial structure', 1, 1)),
      setting('organization', 'masterData', 'Employee Reference Lists', 'tenant', navigation('employee-data-definitions', 'Employee data definitions', 2, 0)),
      route('organization', 'people', 'Employee records and contracts', '/people', 'operations', 'company', navigation('related-operations', 'Related operations', 3, 0)),
      route('organization', 'contractRenewals', 'Contract renewals', '/contract-renewals', 'operations', 'company', navigation('related-operations', 'Related operations', 3, 1)),
    ],
  },
  {
    id: 'recruitment', label: 'Recruitment',
    description: 'Hiring, assessments, offers and workforce planning in the recruitment workspace.',
    destinations: [route('recruitment', 'recruitment', 'Recruitment and workforce planning', '/recruitment', 'operations', 'mixed', navigation('recruitment-operations', 'Recruitment operations', 0, 0))],
  },
  {
    id: 'onboarding', label: 'Onboarding and confirmation',
    description: 'Shared onboarding checklists and tasks; probation and confirmation remain in Performance.',
    destinations: [destination({ id: 'onboarding', areaId: 'onboarding', label: 'Onboarding checklists', path: '/recruitment/onboarding', kind: 'templates', scope: 'tenant', presentation: 'embedded', editorRoles: ['Admin', 'HR Manager'], editorPermission: 'recruitment.write', assistance: 'existing-onboarding', ...navigation('templates-tasks', 'Templates and tasks', 0, 0) })],
  },
  {
    id: 'time', label: 'Time and attendance',
    description: 'Work locations, shifts, attendance, overtime and project timesheets.',
    destinations: [
      setting('time', 'locations', 'Locations and geofences', 'mixed', navigation('work-configuration', 'Work configuration', 0, 0)),
      route('time', 'attendance', 'Attendance operations', '/attendance', 'operations', 'company', navigation('related-operations', 'Related operations', 1, 0)),
      route('time', 'shifts', 'Shift definitions and policy', '/shifts', 'settings', 'mixed', navigation('work-configuration', 'Work configuration', 0, 1)),
      route('time', 'overtime', 'Overtime requests', '/overtime', 'operations', 'company', navigation('related-operations', 'Related operations', 1, 1)),
      route('time', 'timesheets', 'Project timesheets', '/timesheets', 'operations', 'company', navigation('related-operations', 'Related operations', 1, 2)),
    ],
  },
  {
    id: 'leave', label: 'Leave and absence',
    description: 'Leave types, policies and holidays within the existing leave workspace.',
    destinations: [
      ...[['leaveTypes', 'Leave types'], ['leavePolicies', 'Leave policies'], ['holidayCalendars', 'Holiday calendars']].map(([id, label], index) => destination({ id, label, areaId: 'leave', path: '/leave', kind: 'settings', scope: 'tenant', presentation: 'embedded', editorRoles: ['Admin', 'HR Manager'], ...navigation('policy-definitions', 'Policy definitions', 0, index) })),
      route('leave', 'leave', 'Leave policies and holidays', '/leave', 'settings', 'mixed', navigation('related-workspace', 'Related workspace', 1, 0)),
    ],
  },
  {
    id: 'compensation', label: 'Compensation and benefits',
    description: 'Benefit plans, grade entitlements, loans and merit planning.',
    destinations: [
      route('compensation', 'benefits', 'Benefit plans and enrollments', '/benefits', 'settings', 'mixed', navigation('plans-entitlements', 'Plans and entitlements', 0, 0)),
      route('compensation', 'gradeBenefits', 'Benefits by grade', '/benefits/by-grade', 'settings', 'mixed', navigation('plans-entitlements', 'Plans and entitlements', 0, 1)),
      route('compensation', 'loans', 'Loan and advance policies', '/loans', 'settings', 'mixed', navigation('plans-entitlements', 'Plans and entitlements', 0, 2)),
      route('compensation', 'merit', 'Merit planning and benchmarks', '/compensation', 'settings', 'mixed', navigation('plans-entitlements', 'Plans and entitlements', 0, 3)),
    ],
  },
  {
    id: 'payroll', label: 'Payroll',
    description: 'Salary structures, posting accounts, rates, fiscal years and payslip layouts.',
    destinations: [
      setting('payroll', 'glMapping', 'GL and rates', 'mixed', navigation('posting-calendars', 'Posting and calendars', 0, 0)),
      setting('payroll', 'fiscalYears', 'Fiscal years', 'tenant', navigation('posting-calendars', 'Posting and calendars', 0, 1)),
      route('payroll', 'payroll', 'Payroll and salary structures', '/payroll', 'settings', 'mixed', navigation('payroll-configuration', 'Payroll configuration', 1, 0)),
      route('payroll', 'payslipTemplates', 'Payslip templates', '/payroll/templates', 'templates', 'tenant', navigation('payroll-configuration', 'Payroll configuration', 1, 1)),
    ],
  },
  {
    id: 'performance', label: 'Performance and talent',
    description: 'Review cycles, templates, probation, calibration and succession development.',
    destinations: [
      route('performance', 'performance', 'Performance cycles and templates', '/performance', 'settings', 'mixed', navigation('cycles-templates', 'Cycles and templates', 0, 0)),
      route('performance', 'succession', 'Succession and development', '/performance/succession', 'operations', 'mixed', navigation('related-operations', 'Related operations', 1, 0)),
    ],
  },
  {
    id: 'learning', label: 'Learning and development',
    description: 'Course catalogues, learning paths, assignment rules and certification records.',
    destinations: [route('learning', 'learning', 'Learning catalogue and rules', '/learning', 'settings', 'mixed', navigation('catalogue-rules', 'Catalogue and rules', 0, 0))],
  },
  {
    id: 'services', label: 'Employee services and relations',
    description: 'Employee requests, letters and engagement; restricted case procedures are not a connected settings form.',
    destinations: [
      route('services', 'hrRequests', 'Request center', '/hr-requests', 'operations', 'company', navigation('related-operations', 'Related operations', 1, 0)),
      route('services', 'hrLetters', 'HR letters', '/hr-letters', 'operations', 'company', navigation('related-operations', 'Related operations', 1, 1)),
      route('services', 'engagement', 'Engagement templates and privacy', '/engagement', 'templates', 'mixed', navigation('templates-communication', 'Templates and communication', 0, 0)),
      setting('services', 'notificationTemplates', 'Notification templates', 'tenant', navigation('templates-communication', 'Templates and communication', 0, 1)),
    ],
  },
  {
    id: 'offboarding', label: 'Offboarding and settlement',
    description: 'Exits, clearance tasks and final settlement in the existing operational workflow.',
    destinations: [route('offboarding', 'offboarding', 'Offboarding and settlement', '/offboarding', 'operations', 'company', navigation('exit-operations', 'Exit operations', 0, 0))],
  },
  {
    id: 'compliance', label: 'Compliance and statutory administration',
    description: 'Country rules, compliance profiles, tax policies, documentation and filings.',
    destinations: [
      setting('compliance', 'gccSettings', 'GCC settings', 'tenant', navigation('rules-profiles', 'Rules and profiles', 0, 0)),
      setting('compliance', 'statutoryRules', 'Statutory rules', 'tenant', navigation('rules-profiles', 'Rules and profiles', 0, 1)),
      route('compliance', 'complianceProfiles', 'Compliance profiles', '/compliance-profiles', 'settings', 'mixed', navigation('rules-profiles', 'Rules and profiles', 0, 2)),
      route('compliance', 'taxPolicies', 'Tax policies', '/tax-policies', 'settings', 'mixed', navigation('rules-profiles', 'Rules and profiles', 0, 3)),
      route('compliance', 'compliance', 'Documents and renewals', '/compliance', 'operations', 'company', navigation('filings-evidence', 'Filings and evidence', 1, 0)),
      route('compliance', 'saudiCompliance', 'Saudi compliance', '/saudi-compliance', 'operations', 'company', navigation('filings-evidence', 'Filings and evidence', 1, 1)),
      route('compliance', 'gosiFiling', 'GOSI filing', '/gosi-filing', 'operations', 'company', navigation('filings-evidence', 'Filings and evidence', 1, 2)),
      destination({ id: 'adminAuditLogs', areaId: 'compliance', label: 'Administrative audit logs', path: '/setup?tab=adminAuditLogs', kind: 'operations', scope: 'mixed', presentation: 'embedded', ...navigation('filings-evidence', 'Filings and evidence', 1, 3) }),
    ],
  },
];

export function resolveSetupArea(value: string | null): SetupAreaId | 'overview' {
  return SETUP_AREAS.find(area => area.id === value)?.id ?? 'overview';
}

export interface SetupAccessContext {
  hasPermission: (permission: string) => boolean;
  hasRole?: (role: string) => boolean;
  isFeatureEnabled: (key: string) => boolean;
  verdictForPath: (path: string) => { allowed: boolean };
  flagStatus: 'loading' | 'ready' | 'error';
  isCompanyScopedGroup: boolean;
}

const companyScopedSetupIds = new Set(['companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']);

export function destinationAvailability(destination: SetupDestination, context: SetupAccessContext): 'available' | 'restricted' | 'unavailable' | 'checking' | 'unknown' {
  if (destination.requiredPermissions.length > 0 && !destination.requiredPermissions.some(context.hasPermission)) return 'restricted';
  if (destination.editorRoles && !destination.editorRoles.some(role => context.hasRole?.(role))) return 'restricted';
  if (destination.editorPermission && !context.hasPermission(destination.editorPermission)) return 'restricted';
  if (context.isCompanyScopedGroup && (
    destination.scope === 'tenant'
    || (destination.presentation === 'embedded' && !companyScopedSetupIds.has(destination.id))
  )) return 'restricted';
  // Path ownership is server supplied even for destinations without a feature key.
  if (context.flagStatus === 'loading') return 'checking';
  if (context.flagStatus === 'error') return 'unknown';
  if (destination.requiredFeatureKey && !context.isFeatureEnabled(destination.requiredFeatureKey)) return 'unavailable';
  if (!context.verdictForPath(destination.path.split('?')[0]).allowed) return 'unavailable';
  return 'available';
}
