import { msg } from '../i18n/translations';
import { navigationItems } from '../routes/navigation';
import { canListCompanies, canOpenGlAndRates } from './setupAccess';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleAccess, type SetupModuleGroupId, type SetupModuleSection, type SetupModuleSectionId } from './setupModuleSections';

/**
 * Setup & Administration's own settings sections, in navigation order. The one source for each section's name and
 * description: the section navigation, the section header and the matching All setup areas destination all read
 * the label from here, so the three never disagree. Tab ids are the stable URL contract (`/setup?tab=…`).
 */
export type SetupSettingId = 'companies' | 'branches' | 'locations' | 'costCenters' | 'departments'
  | 'grades' | 'designations' | 'establishment'
  | 'gccSettings' | 'statutoryRules' | 'fiscalYears' | 'glMapping'
  | 'masterData' | 'numberingRules' | 'notificationTemplates' | 'emailConfig' | 'systemSettings' | 'adminAuditLogs';
export type SetupSettingGroupId = 'companyStructure' | 'jobsHeadcount' | 'payrollCompliance' | 'workspace';

export interface SetupSettingSection {
  id: SetupSettingId;
  label: string;
  description: string;
  /** The one navigation group the section lives in. Any group, so Notifications can sit with the employee services. */
  group: SetupSectionGroupId;
}

/** Any Setup section: Setup's own settings, or a module's configuration shown here (lib/setupModuleSections.ts). */
export type SetupSection = SetupSettingSection | SetupModuleSection;
export type SetupSectionId = SetupSettingId | SetupModuleSectionId;
export type SetupSectionGroupId = SetupSettingGroupId | SetupModuleGroupId;

/**
 * The navigation's groups, in order, mirroring the product menu so an administrator finds Leave's settings under
 * "Time & leave". "Get started" holds the setup tools (and Opening balances); the others hold the settings sections.
 */
export const SETUP_SECTION_GROUPS: readonly { id: SetupSectionGroupId; label: string }[] = [
  { id: 'getStarted', label: msg('Get started') },
  { id: 'companyStructure', label: msg('Company structure') },
  { id: 'jobsHeadcount', label: msg('Jobs & headcount') },
  { id: 'timeLeave', label: msg('Time & leave') },
  { id: 'payBenefits', label: msg('Pay & benefits') },
  { id: 'talentServices', label: msg('Talent & employee services') },
  { id: 'payrollCompliance', label: msg('Payroll & compliance') },
  { id: 'accessSecurity', label: msg('Users & access') },
  { id: 'workspace', label: msg('Workspace') },
];

export const SETUP_SETTING_SECTIONS: readonly SetupSettingSection[] = [
  { id: 'companies', label: msg('Companies'), description: msg('Legal entities, registration details and operating currencies.'), group: 'companyStructure' },
  { id: 'branches', label: msg('Branches'), description: msg('Company branches and their operating locations.'), group: 'companyStructure' },
  { id: 'locations', label: msg('Locations'), description: msg('Work sites, addresses and attendance geofences.'), group: 'companyStructure' },
  { id: 'costCenters', label: msg('Cost centres'), description: msg('Cost-centre codes used by departments, payroll and reporting.'), group: 'companyStructure' },
  { id: 'departments', label: msg('Departments'), description: msg('Reporting departments and their cost-center ownership.'), group: 'companyStructure' },
  { id: 'grades', label: msg('Grades'), description: msg('Career levels and the salary range for each grade.'), group: 'jobsHeadcount' },
  { id: 'designations', label: msg('Designations'), description: msg('Standard job titles and grade eligibility.'), group: 'jobsHeadcount' },
  { id: 'establishment', label: msg('Headcount & budget'), description: msg('Approved headcount and monthly budget for each department and level.'), group: 'jobsHeadcount' },
  { id: 'gccSettings', label: msg('Country rules'), description: msg('Per country: statutory settings (work week, WPS/SIF, end of service, document alerts) and payroll keys such as weekend days.'), group: 'payrollCompliance' },
  { id: 'statutoryRules', label: msg('Statutory rules'), description: msg('Tenant-wide effective-dated rules used by payroll and compliance.'), group: 'payrollCompliance' },
  { id: 'fiscalYears', label: msg('Fiscal years'), description: msg('Financial-year periods used by budgets and reporting.'), group: 'payrollCompliance' },
  { id: 'glMapping', label: msg('Payroll accounting'), description: msg('GL accounts, posting drivers, company rates, accounting periods and end-of-service provision.'), group: 'payrollCompliance' },
  { id: 'masterData', label: msg('Drop-down lists'), description: msg('The choices people pick in forms, such as contract type or marital status.'), group: 'workspace' },
  { id: 'numberingRules', label: msg('Record numbering'), description: msg('Prefixes and sequences for generated record numbers.'), group: 'workspace' },
  { id: 'notificationTemplates', label: msg('Notifications'), description: msg('Messages sent for employee and payroll events.'), group: 'talentServices' },
  { id: 'emailConfig', label: msg('Email delivery'), description: msg('Outbound email delivery and connection testing.'), group: 'workspace' },
  { id: 'systemSettings', label: msg('Workspace settings'), description: msg('Workspace-wide operational defaults.'), group: 'workspace' },
  { id: 'adminAuditLogs', label: msg('Audit log'), description: msg('Review recorded administrative changes and actors.'), group: 'workspace' },
];

/**
 * The order of the sections inside the navigation, group by group (placement audit 2026-10-25). Each group's sections
 * follow this list, not the order the two catalogues happen to be declared in: Workspace opens with Language & region,
 * Branding, Modules and Plan & billing and ends with the Audit log. A section not listed here keeps its declared
 * order after the listed ones, so a new section never disappears.
 */
export const SETUP_SECTION_ORDER: readonly SetupSectionId[] = [
  'openingBalances',
  'companies', 'branches', 'locations', 'costCenters', 'departments',
  'grades', 'designations', 'establishment',
  'shiftDefinitions', 'shiftPolicy', 'attendanceRules', 'attendanceDevices', 'overtimePolicies', 'leaveTypes', 'leavePolicies', 'holidayCalendars',
  'benefitPlans', 'loanTypes', 'loanPolicies', 'advancePolicy', 'bonusTypes', 'payslipTemplates', 'payrollRules',
  'onboardingChecklists', 'onboardingChecklistTemplates', 'hireTemplates', 'performanceScorecards', 'letterTemplates', 'engagementPrivacy', 'notificationTemplates',
  'glMapping', 'fiscalYears', 'gccSettings', 'statutoryRules', 'taxPolicies', 'complianceProfiles', 'saudiCompliance',
  'users', 'roles', 'permissionMatrix', 'permissions', 'permissionGrantors', 'approvalAuthorities', 'approvalDelegations', 'signInSecurity', 'accessLog',
  'localization', 'branding', 'modules', 'planBilling', 'masterData', 'helpText', 'numberingRules', 'emailConfig', 'systemSettings', 'adminAuditLogs',
];

/** Sorts sections into {@link SETUP_SECTION_ORDER}; unlisted sections keep their relative order at the end. */
export function inSetupOrder<T extends { id: string }>(sections: readonly T[]): T[] {
  const rank = (id: string) => { const i = (SETUP_SECTION_ORDER as readonly string[]).indexOf(id); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
  return sections.map((section, index) => ({ section, index })).sort((a, b) => rank(a.section.id) - rank(b.section.id) || a.index - b.index).map(x => x.section);
}

const settingLabel = (id: SetupSettingId) => SETUP_SETTING_SECTIONS.find(section => section.id === id)!.label;

/** The settings a company-scoped group administrator owns: everything else is shared by the whole tenant. Departments, designations
 * and grades are per company since 2026-10-23 (Addendum B), so a company super admin manages their own company's rows. */
export const COMPANY_OWNED_SETTING_IDS: ReadonlySet<string> = new Set<SetupSettingId>(['companies', 'branches', 'departments', 'designations', 'grades', 'costCenters', 'glMapping', 'adminAuditLogs']);

/** An area of "All setup areas" IS a navigation group (one taxonomy): the settings sections the navigation shows under that
 *  group, plus the module pages that belong with them. Group ids are the URL contract (`area=`). */
export type SetupAreaId = Exclude<SetupSectionGroupId, 'getStarted'>;

/** Area ids of the former 12-area taxonomy, so a saved link (`area=leave`) lands on the navigation group that now holds it. */
export const LEGACY_SETUP_AREA_IDS: Readonly<Record<string, SetupAreaId>> = {
  organization: 'companyStructure', recruitment: 'jobsHeadcount', onboarding: 'talentServices', time: 'timeLeave', leave: 'timeLeave',
  compensation: 'payBenefits', payroll: 'payrollCompliance', performance: 'talentServices', learning: 'talentServices',
  services: 'talentServices', offboarding: 'talentServices', compliance: 'payrollCompliance',
};

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
  /** A module's configuration shown in Setup: available exactly when its Setup section is (moduleSectionAvailable). */
  moduleSection?: SetupModuleSection;
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

function setting(areaId: SetupAreaId, id: SetupSettingId, scope: SetupDestination['scope'], nav: DestinationNavigation, effectiveTargetScope?: SetupDestination['effectiveTargetScope']): SetupDestination {
  return destination({ id, areaId, label: settingLabel(id), path: `/setup?tab=${id}`, kind: 'settings', scope, presentation: 'embedded', effectiveTargetScope, ...nav });
}

/** A module's configuration, opened as its Setup section. */
function moduleSetting(areaId: SetupAreaId, id: SetupModuleSectionId, scope: SetupDestination['scope'], nav: DestinationNavigation): SetupDestination {
  const section = SETUP_MODULE_SECTIONS.find(item => item.id === id);
  if (!section) throw new Error(`Unknown Setup section: ${id}`);
  return {
    id, areaId, label: section.label, path: `/setup?tab=${id}`, kind: 'settings', scope, presentation: 'embedded',
    assistance: id === 'onboardingChecklists' ? 'existing-onboarding' : 'not-connected',
    effectiveTargetScope: scope === 'mixed' ? 'recordDependent' : scope,
    requiredPermissions: section.permissions, requiredFeatureKey: section.featureKey, editorRoles: section.roles,
    moduleSection: section,
    ...nav,
  };
}

function route(areaId: SetupAreaId, id: string, label: string, path: string, kind: SetupDestination['kind'], scope: SetupDestination['scope'], nav: DestinationNavigation): SetupDestination {
  return destination({ id, areaId, label, path, kind, scope, presentation: 'route', ...nav });
}

/** The scope of a section's data, for the "company or tenant" note in All setup areas. Sections not listed are tenant-wide. */
const SECTION_SCOPE: Readonly<Record<string, SetupDestination['scope']>> = {
  companies: 'company', branches: 'company', departments: 'company', designations: 'company', grades: 'company',
  costCenters: 'mixed', establishment: 'mixed', locations: 'mixed', glMapping: 'mixed',
  leaveTypes: 'mixed', leavePolicies: 'mixed', holidayCalendars: 'mixed', benefitPlans: 'mixed', loanPolicies: 'mixed',
  letterTemplates: 'mixed', complianceProfiles: 'mixed', taxPolicies: 'mixed', saudiCompliance: 'company', adminAuditLogs: 'mixed',
};

/** Module pages that sit beside a group's settings (their daily work, or a configuration that has not moved into Setup). */
const GROUP_ROUTES: Readonly<Record<SetupAreaId, readonly ((nav: DestinationNavigation) => SetupDestination)[]>> = {
  companyStructure: [
    nav => route('companyStructure', 'people', 'Employee records and contracts', '/people', 'operations', 'company', nav),
    nav => route('companyStructure', 'contractRenewals', 'Contract renewals', '/contract-renewals', 'operations', 'company', nav),
  ],
  jobsHeadcount: [nav => route('jobsHeadcount', 'recruitment', 'Recruitment and workforce planning', '/recruitment', 'operations', 'mixed', nav)],
  timeLeave: [
    nav => route('timeLeave', 'attendance', 'Attendance operations', '/attendance', 'operations', 'company', nav),
    nav => route('timeLeave', 'shifts', 'Shift rosters', '/shifts', 'operations', 'mixed', nav),
    nav => route('timeLeave', 'overtime', 'Overtime requests', '/overtime', 'operations', 'company', nav),
    nav => route('timeLeave', 'timesheets', 'Project timesheets', '/timesheets', 'operations', 'company', nav),
    nav => route('timeLeave', 'leave', 'Leave requests and balances', '/leave', 'operations', 'mixed', nav),
  ],
  payBenefits: [
    nav => route('payBenefits', 'gradeBenefits', 'Benefits by grade', '/benefits/by-grade', 'settings', 'mixed', nav),
    nav => route('payBenefits', 'merit', 'Merit planning and benchmarks', '/compensation', 'settings', 'mixed', nav),
    nav => route('payBenefits', 'payroll', 'Payroll and salary structures', '/payroll', 'settings', 'mixed', nav),
    nav => route('payBenefits', 'benefits', 'Benefit enrolments', '/benefits', 'operations', 'mixed', nav),
    nav => route('payBenefits', 'loans', 'Loans and advances', '/loans', 'operations', 'mixed', nav),
  ],
  talentServices: [
    nav => route('talentServices', 'learning', 'Learning catalogue and rules', '/learning', 'settings', 'mixed', nav),
    nav => route('talentServices', 'engagement', 'Engagement survey templates', '/engagement', 'templates', 'mixed', nav),
    nav => route('talentServices', 'performance', 'Performance cycles', '/performance', 'operations', 'mixed', nav),
    nav => route('talentServices', 'succession', 'Succession and development', '/performance/succession', 'operations', 'mixed', nav),
    nav => route('talentServices', 'hrRequests', 'Request center', '/hr-requests', 'operations', 'company', nav),
    nav => route('talentServices', 'hrLetters', 'HR letters', '/hr-letters', 'operations', 'company', nav),
    nav => route('talentServices', 'offboarding', 'Offboarding and settlement', '/offboarding', 'operations', 'company', nav),
  ],
  payrollCompliance: [
    nav => route('payrollCompliance', 'compliance', 'Documents and renewals', '/compliance', 'operations', 'company', nav),
    nav => route('payrollCompliance', 'saudiComplianceDashboard', 'Saudization and Qiwa status', '/saudi-compliance', 'operations', 'company', nav),
    nav => route('payrollCompliance', 'gosiFiling', 'GOSI filing', '/gosi-filing', 'operations', 'company', nav),
  ],
  accessSecurity: [],
  workspace: [],
};

const AREA_DESCRIPTIONS: Readonly<Record<SetupAreaId, string>> = {
  companyStructure: 'Legal entities, branches, locations, cost centres and departments.',
  jobsHeadcount: 'Grades, job titles, approved headcount and budget, and hiring.',
  timeLeave: 'Shifts, attendance, overtime, leave types, policies and holidays.',
  payBenefits: 'Benefit plans, loans, advances, bonuses, payslip layouts and payroll.',
  talentServices: 'Onboarding, reviews, letters, surveys, notifications, learning and exits.',
  payrollCompliance: 'Accounting, fiscal years, country rules, statutory rules, taxes and compliance profiles.',
  accessSecurity: 'Users, roles, permissions, approvals and sign-in security.',
  workspace: 'Language, branding, modules, billing, lists, numbering, email and the audit log.',
};

/**
 * All setup areas, DERIVED from the navigation: one area per navigation group, holding exactly the sections the
 * navigation shows in that group (in SETUP_SECTION_ORDER), then the module pages that belong beside them. There is no
 * second taxonomy, so a section can never sit in one place in the menu and another in the overview.
 * Discovery only: available means the destination can be opened, never configured or editable.
 */
function buildSetupAreas(): readonly SetupAreaDefinition[] {
  const sections = inSetupOrder<SetupSection>([...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS]);
  return SETUP_SECTION_GROUPS.filter(group => group.id !== 'getStarted').map(group => {
    const areaId = group.id as SetupAreaId;
    const own = sections.filter(section => section.group === group.id);
    const routes = GROUP_ROUTES[areaId];
    const settingsRoutes = routes.map((make, index) => ({ index, make })).filter(item => item.make(navigation('x', 'x', 0, 0)).kind === 'settings');
    const operationRoutes = routes.map((make, index) => ({ index, make })).filter(item => item.make(navigation('x', 'x', 0, 0)).kind !== 'settings');
    const configuration = (order: number) => navigation('configuration', 'Settings', 0, order);
    const destinations: SetupDestination[] = own.map((section, order) => {
      const scope = SECTION_SCOPE[section.id] ?? 'tenant';
      const isModule = SETUP_MODULE_SECTIONS.some(item => item.id === section.id);
      const effective = section.id === 'companies' ? 'group' as const : undefined;
      return isModule ? moduleSetting(areaId, section.id as SetupModuleSectionId, scope, configuration(order)) : setting(areaId, section.id as SetupSettingId, scope, configuration(order), effective);
    });
    settingsRoutes.forEach((item, i) => destinations.push(item.make(configuration(own.length + i))));
    operationRoutes.forEach((item, i) => destinations.push(item.make(navigation('related-operations', 'Related operations', 1, i))));
    return { id: areaId, label: group.label, description: AREA_DESCRIPTIONS[areaId], destinations };
  });
}

export const SETUP_AREAS: readonly SetupAreaDefinition[] = buildSetupAreas();

export function resolveSetupArea(value: string | null): SetupAreaId | 'overview' {
  if (value && value in LEGACY_SETUP_AREA_IDS) return LEGACY_SETUP_AREA_IDS[value];
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


export function destinationAvailability(destination: SetupDestination, context: SetupAccessContext): 'available' | 'restricted' | 'unavailable' | 'checking' | 'unknown' {
  // A module's configuration opens for the same people as its Setup section, and follows its module's switch.
  const section = destination.moduleSection;
  if (section) {
    const hasRole = (role: string) => context.hasRole?.(role) ?? false;
    if (!moduleSectionAvailable(section, { ...context, hasRole, flagStatus: 'ready', isFeatureEnabled: () => true, verdictForPath: () => ({ allowed: true }) })) return 'restricted';
    if (context.flagStatus === 'loading') return 'checking';
    if (context.flagStatus === 'error') return 'unknown';
    return moduleSectionAvailable(section, { ...context, hasRole }) ? 'available' : 'unavailable';
  }
  if (destination.requiredPermissions.length > 0 && !destination.requiredPermissions.some(context.hasPermission)) return 'restricted';
  if (destination.editorRoles && !destination.editorRoles.some(role => context.hasRole?.(role))) return 'restricted';
  if (destination.editorPermission && !context.hasPermission(destination.editorPermission)) return 'restricted';
  if (context.isCompanyScopedGroup && (
    destination.scope === 'tenant'
    || (destination.presentation === 'embedded' && !COMPANY_OWNED_SETTING_IDS.has(destination.id))
  )) return 'restricted';
  // Path ownership is server supplied even for destinations without a feature key.
  if (context.flagStatus === 'loading') return 'checking';
  if (context.flagStatus === 'error') return 'unknown';
  if (destination.requiredFeatureKey && !context.isFeatureEnabled(destination.requiredFeatureKey)) return 'unavailable';
  if (!context.verdictForPath(destination.path.split('?')[0]).allowed) return 'unavailable';
  return 'available';
}

// ── Who sees which Setup section ──────────────────────────────────────────────────────────────────────────────────

/** The setup tools at the top of the navigation ("Get started"). */
export type SetupToolId = 'guidedSetup' | 'allSetupAreas' | 'importOrganization' | 'policyLibrary';

export const SETUP_TOOLS: Record<SetupToolId, { label: string; description: string; href: string }> = {
  guidedSetup: { label: msg('Guided setup'), description: msg('Create a company, or finish setting one up, one screen at a time.'), href: '/setup?tab=aiSetup&path=guided' },
  allSetupAreas: { label: msg('All setup areas'), description: msg('Every setup area, with its settings and related workspaces.'), href: '/setup?tab=aiSetup&path=configuration&area=overview' },
  importOrganization: { label: msg('Import organization'), description: msg('Load companies, branches, departments and jobs from a file.'), href: '/setup?tab=importOrganization' },
  policyLibrary: { label: msg('Policy library'), description: msg('Policy documents your company has uploaded.'), href: '/setup?tab=policyLibrary' },
};

export interface SetupNavigationAccess extends SetupModuleAccess {
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
  /** A group account's administrator who is limited to some companies. */
  isCompanyScopedGroup: boolean;
  /** The tenant has policy intake switched on (the Policy library exists only then). */
  policyIntake: boolean;
}

export interface SetupNavigationModel {
  canWrite: boolean;
  canReadCompanies: boolean;
  canReadStatutoryRules: boolean;
  canReadGlAndRates: boolean;
  canManagePolicies: boolean;
  /** "Get started" entries, in order. Empty for a reader. */
  tools: SetupToolId[];
  /** The settings sections this user can open, in navigation order: Setup's own, then the modules' configuration. */
  settings: SetupSection[];
}

/**
 * The one answer to "which Setup sections can this user open". Setup's navigation, its "Find a setting" search and
 * the ⌘K palette's Settings group all read it, so nothing is ever offered that the navigation would not show.
 */
export function setupNavigationModel(access: SetupNavigationAccess): SetupNavigationModel {
  const { hasPermission, hasRole, isCompanyScopedGroup } = access;
  const canManagePolicies = access.policyIntake && hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));
  const canWrite = hasPermission('organization.write') || hasPermission('organization.establishment.write');
  // A reader sees Companies only when the server will list them for its role (setupAccess.ts), and GL & rates when it
  // holds any GL or rates permission: Payroll Manager and Finance hold those, and used to get a page with no sections.
  const canReadCompanies = canWrite || (hasPermission('organization.read') && canListCompanies({ hasPermission, hasRole }));
  const canReadStatutoryRules = hasPermission('payroll.rates.read') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('Auditor'));
  const canReadGlAndRates = canOpenGlAndRates({ hasPermission, hasRole });
  const writable = isCompanyScopedGroup ? SETUP_SETTING_SECTIONS.filter(section => COMPANY_OWNED_SETTING_IDS.has(section.id)) : SETUP_SETTING_SECTIONS;
  const own: SetupSection[] = canWrite
    ? [...writable]
    : SETUP_SETTING_SECTIONS.filter(section => (section.id === 'companies' && canReadCompanies)
      || (section.id === 'statutoryRules' && canReadStatutoryRules) || (section.id === 'glMapping' && canReadGlAndRates));
  // A module's configuration opens for whoever its old tab opened for, whether or not they may edit the organisation.
  const settings = inSetupOrder([...own, ...SETUP_MODULE_SECTIONS.filter(section => moduleSectionAvailable(section, access))]);
  const tools: SetupToolId[] = canWrite ? [
    'guidedSetup',
    'allSetupAreas',
    ...(!isCompanyScopedGroup ? ['importOrganization' as const] : []),
    ...(canManagePolicies ? ['policyLibrary' as const] : []),
  ] : [];
  return { canWrite, canReadCompanies, canReadStatutoryRules, canReadGlAndRates, canManagePolicies, tools, settings };
}

/**
 * Whether Setup has anything for this user. The route's permissions are alternatives and some are broad (an employee
 * reads their own leave and attendance), so the menu entry, the top-bar gear and the ⌘K palette ask this instead.
 */
export function setupHasEntries(access: SetupNavigationAccess): boolean {
  const model = setupNavigationModel(access);
  return model.tools.length > 0 || model.settings.length > 0;
}

/**
 * Sections that answer neighbouring questions and should point at each other: the administrative change log and the
 * sign-in log are both "who did what", so each links to the other (placement audit 2026-10-25).
 */
export const SETUP_RELATED_SECTIONS: Partial<Record<SetupSectionId, { id: SetupSectionId; label: string; why: string }>> = {
  adminAuditLogs: { id: 'accessLog', label: msg('Sign-in and access log'), why: msg('Looking for sign-ins, failed attempts or access changes?') },
  accessLog: { id: 'adminAuditLogs', label: msg('Audit log'), why: msg('Looking for who changed a setting?') },
};

/**
 * Where a link to a section this person cannot open should go instead: their first available section, never a dead end.
 * Null when the address is fine (the section is open to them, is not a section address, or there is nowhere else to go).
 */
export function unavailableSectionRedirect(requested: string | null | undefined, openable: readonly { id: string }[], isSection: (id: string) => boolean): SetupSectionId | null {
  if (!requested || !isSection(requested)) return null;
  if (openable.some(section => section.id === requested)) return null;
  return (openable[0]?.id as SetupSectionId | undefined) ?? null;
}
