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
  scope: 'company' | 'tenant' | 'mixed';
  presentation: 'embedded' | 'route';
  assistance: 'existing-onboarding' | 'not-connected';
}

export interface SetupAreaDefinition {
  id: SetupAreaId;
  label: string;
  description: string;
  destinations: readonly SetupDestination[];
}

type DestinationInput = Omit<SetupDestination, 'requiredPermissions' | 'requiredFeatureKey' | 'assistance'> & {
  assistance?: SetupDestination['assistance'];
};

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
    requiredPermissions: pathname === '/setup' ? setupTabPermissions(input.id) : route.requiredPermissions ?? [],
    requiredFeatureKey: route.requiredFeatureKey,
  };
}

function setting(areaId: SetupAreaId, id: string, label: string, scope: SetupDestination['scope']): SetupDestination {
  return destination({ id, areaId, label, path: `/setup?tab=${id}`, kind: 'settings', scope, presentation: 'embedded' });
}

function route(areaId: SetupAreaId, id: string, label: string, path: string, kind: SetupDestination['kind'], scope: SetupDestination['scope']): SetupDestination {
  return destination({ id, areaId, label, path, kind, scope, presentation: 'route' });
}

/** Discovery only: available means the destination can be opened, never configured or editable. */
export const SETUP_AREAS: readonly SetupAreaDefinition[] = [
  {
    id: 'organization', label: 'Organization and core HR',
    description: 'Company structure, shared reference data, employment records and contracts.',
    destinations: [
      setting('organization', 'companies', 'Companies', 'company'),
      setting('organization', 'branches', 'Branches', 'company'),
      setting('organization', 'departments', 'Departments', 'tenant'),
      setting('organization', 'designations', 'Designations', 'tenant'),
      setting('organization', 'grades', 'Grades', 'tenant'),
      setting('organization', 'costCenters', 'Cost centers', 'mixed'),
      setting('organization', 'establishment', 'Cost centres and budget', 'mixed'),
      setting('organization', 'masterData', 'Master data', 'tenant'),
      route('organization', 'people', 'Employee records and contracts', '/people', 'operations', 'company'),
      route('organization', 'contractRenewals', 'Contract renewals', '/contract-renewals', 'operations', 'company'),
    ],
  },
  {
    id: 'recruitment', label: 'Recruitment',
    description: 'Hiring, assessments, offers and workforce planning in the recruitment workspace.',
    destinations: [route('recruitment', 'recruitment', 'Recruitment and workforce planning', '/recruitment', 'operations', 'mixed')],
  },
  {
    id: 'onboarding', label: 'Onboarding and confirmation',
    description: 'Shared onboarding checklists and tasks; probation and confirmation remain in Performance.',
    destinations: [destination({ id: 'onboarding', areaId: 'onboarding', label: 'Onboarding checklists', path: '/recruitment/onboarding', kind: 'templates', scope: 'tenant', presentation: 'route', assistance: 'existing-onboarding' })],
  },
  {
    id: 'time', label: 'Time and attendance',
    description: 'Work locations, shifts, attendance, overtime and project timesheets.',
    destinations: [
      setting('time', 'locations', 'Locations and geofences', 'mixed'),
      route('time', 'attendance', 'Attendance operations', '/attendance', 'operations', 'company'),
      route('time', 'shifts', 'Shift definitions and policy', '/shifts', 'settings', 'mixed'),
      route('time', 'overtime', 'Overtime requests', '/overtime', 'operations', 'company'),
      route('time', 'timesheets', 'Project timesheets', '/timesheets', 'operations', 'company'),
    ],
  },
  {
    id: 'leave', label: 'Leave and absence',
    description: 'Leave types, policies and holidays within the existing leave workspace.',
    destinations: [route('leave', 'leave', 'Leave policies and holidays', '/leave', 'settings', 'mixed')],
  },
  {
    id: 'compensation', label: 'Compensation and benefits',
    description: 'Benefit plans, grade entitlements, loans and merit planning.',
    destinations: [
      route('compensation', 'benefits', 'Benefit plans and enrollments', '/benefits', 'settings', 'mixed'),
      route('compensation', 'gradeBenefits', 'Benefits by grade', '/benefits/by-grade', 'settings', 'mixed'),
      route('compensation', 'loans', 'Loan and advance policies', '/loans', 'settings', 'mixed'),
      route('compensation', 'merit', 'Merit planning and benchmarks', '/compensation', 'settings', 'mixed'),
    ],
  },
  {
    id: 'payroll', label: 'Payroll',
    description: 'Salary structures, posting accounts, rates, fiscal years and payslip layouts.',
    destinations: [
      setting('payroll', 'glMapping', 'GL and rates', 'mixed'),
      setting('payroll', 'fiscalYears', 'Fiscal years', 'tenant'),
      route('payroll', 'payroll', 'Payroll and salary structures', '/payroll', 'settings', 'mixed'),
      route('payroll', 'payslipTemplates', 'Payslip templates', '/payroll/templates', 'templates', 'tenant'),
    ],
  },
  {
    id: 'performance', label: 'Performance and talent',
    description: 'Review cycles, templates, probation, calibration and succession development.',
    destinations: [
      route('performance', 'performance', 'Performance cycles and templates', '/performance', 'settings', 'mixed'),
      route('performance', 'succession', 'Succession and development', '/performance/succession', 'operations', 'mixed'),
    ],
  },
  {
    id: 'learning', label: 'Learning and development',
    description: 'Course catalogues, learning paths, assignment rules and certification records.',
    destinations: [route('learning', 'learning', 'Learning catalogue and rules', '/learning', 'settings', 'mixed')],
  },
  {
    id: 'services', label: 'Employee services and relations',
    description: 'Employee requests, letters and engagement; restricted case procedures are not a connected settings form.',
    destinations: [
      route('services', 'hrRequests', 'Request center', '/hr-requests', 'operations', 'company'),
      route('services', 'hrLetters', 'HR letters', '/hr-letters', 'operations', 'company'),
      route('services', 'engagement', 'Engagement templates and privacy', '/engagement', 'templates', 'mixed'),
      setting('services', 'notificationTemplates', 'Notification templates', 'tenant'),
    ],
  },
  {
    id: 'offboarding', label: 'Offboarding and settlement',
    description: 'Exits, clearance tasks and final settlement in the existing operational workflow.',
    destinations: [route('offboarding', 'offboarding', 'Offboarding and settlement', '/offboarding', 'operations', 'company')],
  },
  {
    id: 'compliance', label: 'Compliance and statutory administration',
    description: 'Country rules, compliance profiles, tax policies, documentation and filings.',
    destinations: [
      setting('compliance', 'gccSettings', 'GCC settings', 'tenant'),
      setting('compliance', 'statutoryRules', 'Statutory rules', 'tenant'),
      route('compliance', 'complianceProfiles', 'Compliance profiles', '/compliance-profiles', 'settings', 'mixed'),
      route('compliance', 'taxPolicies', 'Tax policies', '/tax-policies', 'settings', 'mixed'),
      route('compliance', 'compliance', 'Documents and renewals', '/compliance', 'operations', 'company'),
      route('compliance', 'saudiCompliance', 'Saudi compliance', '/saudi-compliance', 'operations', 'company'),
      route('compliance', 'gosiFiling', 'GOSI filing', '/gosi-filing', 'operations', 'company'),
      destination({ id: 'adminAuditLogs', areaId: 'compliance', label: 'Administrative audit logs', path: '/setup?tab=adminAuditLogs', kind: 'operations', scope: 'mixed', presentation: 'embedded' }),
    ],
  },
];

export function resolveSetupArea(value: string | null): SetupAreaId | 'overview' {
  return SETUP_AREAS.find(area => area.id === value)?.id ?? 'overview';
}

export interface SetupAccessContext {
  hasPermission: (permission: string) => boolean;
  isFeatureEnabled: (key: string) => boolean;
  verdictForPath: (path: string) => { allowed: boolean };
  flagStatus: 'loading' | 'ready' | 'error';
  isCompanyScopedGroup: boolean;
}

const companyScopedSetupIds = new Set(['companies', 'branches', 'costCenters', 'glMapping', 'adminAuditLogs']);

export function destinationAvailability(destination: SetupDestination, context: SetupAccessContext): 'available' | 'restricted' | 'unavailable' | 'checking' | 'unknown' {
  if (destination.requiredPermissions.length > 0 && !destination.requiredPermissions.some(context.hasPermission)) return 'restricted';
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
