/**
 * Who sees and does what on the Performance screen. The server decides (the backend's
 * LegacyRolePermissionResolver and the seeded role bundles in AuthSeeder); this mirrors it so the
 * screen never offers a step the API will refuse.
 *
 *   performance.read          Open the module. The API limits every list to the caller's own record
 *                             (employee) or reporting line (manager). Seeded: Employee, Manager and up.
 *   performance.write         The line manager's steps: set and approve goals, write the manager
 *                             review, open a PIP. Seeded: Manager, HR Manager, HR Director, Admin.
 *   performance.approve       HR's decisions: calibrate, publish, decide appeals, PIP outcomes and
 *                             probation decisions, recommendation decisions, and the tenant-wide
 *                             overview and analytics. Never held by a line manager.
 *   performance.cycle_manage  HR's set-up: cycles and scorecards.
 */
export const PERFORMANCE = {
  read: 'performance.read',
  write: 'performance.write',
  approve: 'performance.approve',
  setup: 'performance.cycle_manage',
} as const;

/** Any performance key opens the module (menu entry and page gate); each tab then asks for its own. */
export const PERFORMANCE_MODULE_PERMISSIONS: string[] = [
  PERFORMANCE.read, PERFORMANCE.write, PERFORMANCE.approve, PERFORMANCE.setup,
];

export type PerformanceTab =
  | 'overview' | 'cycles' | 'my-reviews' | 'team-reviews' | 'goals' | 'templates'
  | 'calibration' | 'recommendations' | 'pip' | 'analytics' | 'feedback';

/** The keys that show each tab: any one of them is enough. */
export const PERFORMANCE_TAB_PERMISSIONS: Record<PerformanceTab, readonly string[]> = {
  overview: [PERFORMANCE.approve],
  cycles: [PERFORMANCE.write, PERFORMANCE.approve, PERFORMANCE.setup],
  'my-reviews': PERFORMANCE_MODULE_PERMISSIONS,
  'team-reviews': [PERFORMANCE.write, PERFORMANCE.approve],
  goals: PERFORMANCE_MODULE_PERMISSIONS,
  templates: [PERFORMANCE.setup],
  calibration: [PERFORMANCE.approve],
  recommendations: [PERFORMANCE.approve],
  pip: [PERFORMANCE.write, PERFORMANCE.approve],
  analytics: [PERFORMANCE.approve],
  feedback: PERFORMANCE_MODULE_PERMISSIONS,
};

type Has = (permission: string) => boolean;

export function canOpenPerformanceTab(tab: PerformanceTab, has: Has): boolean {
  return PERFORMANCE_TAB_PERMISSIONS[tab].some(has);
}

/** Where each audience starts: HR on the overview, a line manager on their team, an employee on their own review. */
const LANDING_PREFERENCE: PerformanceTab[] = ['overview', 'team-reviews', 'my-reviews'];

/** The tab to open: the one asked for when the caller may see it, otherwise the caller's usual starting tab. */
export function landingPerformanceTab(has: Has, requested?: PerformanceTab): PerformanceTab | null {
  if (requested && canOpenPerformanceTab(requested, has)) return requested;
  return LANDING_PREFERENCE.find(tab => canOpenPerformanceTab(tab, has))
    ?? (Object.keys(PERFORMANCE_TAB_PERMISSIONS) as PerformanceTab[]).find(tab => canOpenPerformanceTab(tab, has))
    ?? null;
}

/** The individual steps, named for what the user does rather than for the key behind them. */
export function performanceCapabilities(has: Has) {
  return {
    /** New cycle, launch, advance; scorecard templates. */
    setUpCycles: has(PERFORMANCE.setup),
    /** Write the manager review; set, approve and delete goals; open a PIP. */
    writeReviews: has(PERFORMANCE.write),
    /** Calibrate, publish, decide appeals, compute attendance, close a PIP, decide probation. */
    decideRatings: has(PERFORMANCE.approve),
  };
}
