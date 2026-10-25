'use client';

import dynamic from 'next/dynamic';
import { Component, type ComponentType, type ReactNode } from 'react';
import { useT } from '../../hooks/useT';
import type { CompanyDto } from '../../api/organization';
import type { SetupModuleSectionId } from '../../lib/setupModuleSections';
import {
  ComplianceProfiles, EngagementPrivacy, LetterTemplates, PerformanceScorecards, SaudiCompliance, TaxPolicies,
} from './talentComplianceSetupPanels';
import {
  AccessLog, ApprovalAuthorities, ApprovalDelegations, Branding, CountryPayrollKeys, HelpText, Localization, Modules, OpeningBalances,
  PermissionGrantors, PermissionMatrix, Permissions, PlanBilling, Roles, SignInSecurity, Users,
} from './accessWorkspaceSetupPanels';

/**
 * A module's configuration screen, shown as its Setup section (lib/setupModuleSections.ts). Each panel is the module's
 * own editor, loaded only when its section is opened, so Setup never carries a second copy of a screen.
 */
export interface ModuleSetupPanelProps {
  /** Unsaved edits: Setup's leave guard asks before the section is left. */
  onDirtyChange?: (dirty: boolean) => void;
  /** The company chosen in Setup's scope, when there is one. */
  companyId?: string | null;
  /** The companies Setup has listed for this reader (empty when it may not list them). */
  companies?: CompanyDto[];
}

type Panel = ComponentType<ModuleSetupPanelProps>;

// Time & leave. Leave types and policies filter to the company chosen in Setup's scope; holidays show that company's
// calendars beside the workspace-wide ones.
const LeaveTypes = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange, companyId }: ModuleSetupPanelProps) => <module.LeaveTypesTab onDirtyChange={onDirtyChange} initialCompanyFilter={companyId ?? ''} />;
  return Panel;
}));
const LeavePolicies = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange, companyId }: ModuleSetupPanelProps) => <module.PoliciesTab onDirtyChange={onDirtyChange} initialCompanyFilter={companyId ?? ''} />;
  return Panel;
}));
const HolidayCalendars = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange, companyId }: ModuleSetupPanelProps) => <module.HolidayCalendarTab onDirtyChange={onDirtyChange} companyId={companyId ?? undefined} />;
  return Panel;
}));
const ShiftDefinitions = dynamic(() => import('../../views/ShiftsPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.ShiftSetupPanel section="definitions" onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const ShiftPolicy = dynamic(() => import('../../views/ShiftsPage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.ShiftSetupPanel section="policy" onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const AttendanceRules = dynamic(() => import('../attendance/AttendanceRulesPanel').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.AttendanceRulesPanel onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const OvertimePolicies = dynamic(() => import('../../views/OvertimePage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.PoliciesTab onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const AttendanceDevices = dynamic(() => import('../attendance/AttendanceDevicesPanel').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.AttendanceDevicesPanel onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const OnboardingChecklists = dynamic(() => import('./OnboardingPolicyWorkspace').then(module => {
  const Panel = ({ onDirtyChange, companies }: ModuleSetupPanelProps) => <module.OnboardingPolicyWorkspace companies={companies ?? []} onDirtyChange={onDirtyChange ?? (() => {})} />;
  return Panel;
}));

const BenefitPlans = dynamic(() => import('../benefits/BenefitPlansPanel').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.BenefitPlansPanel />;
  return Panel;
}));
const LoanTypes = dynamic(() => import('../../views/LoansPage').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.LoanTypesTab />;
  return Panel;
}));
const LoanPolicies = dynamic(() => import('../loans/LoanPoliciesPanel').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.LoanPoliciesPanel />;
  return Panel;
}));
const AdvancePolicy = dynamic(() => import('../../views/LoansPage').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.AdvancePolicyTab />;
  return Panel;
}));
const BonusTypes = dynamic(() => import('../../views/LoansPage').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.BonusTypesTab />;
  return Panel;
}));
const PayslipTemplates = dynamic(() => import('../../views/PayslipTemplatesPage').then(module => {
  const Panel = (_props: ModuleSetupPanelProps) => <module.PayslipTemplatesPanel />;
  return Panel;
}));

/** Not moved yet: the section exists, its module's editor is still to be brought here. */
function pending(id: SetupModuleSectionId): Panel {
  const Pending = () => <p data-testid="module-section-pending" className="surface p-6 text-sm text-slate-600">{id}</p>;
  return Pending;
}

const PANELS: Record<SetupModuleSectionId, Panel> = {
  openingBalances: OpeningBalances,
  leaveTypes: LeaveTypes,
  leavePolicies: LeavePolicies,
  holidayCalendars: HolidayCalendars,
  shiftDefinitions: ShiftDefinitions,
  shiftPolicy: ShiftPolicy,
  attendanceRules: AttendanceRules,
  overtimePolicies: OvertimePolicies,
  attendanceDevices: AttendanceDevices,
  benefitPlans: BenefitPlans,
  loanTypes: LoanTypes,
  loanPolicies: LoanPolicies,
  advancePolicy: AdvancePolicy,
  bonusTypes: BonusTypes,
  payslipTemplates: PayslipTemplates,
  onboardingChecklists: OnboardingChecklists,
  performanceScorecards: PerformanceScorecards,
  letterTemplates: LetterTemplates,
  engagementPrivacy: EngagementPrivacy,
  taxPolicies: TaxPolicies,
  complianceProfiles: ComplianceProfiles,
  saudiCompliance: SaudiCompliance,
  countryPayrollKeys: CountryPayrollKeys,
  users: Users,
  roles: Roles,
  permissionMatrix: PermissionMatrix,
  permissions: Permissions,
  permissionGrantors: PermissionGrantors,
  approvalAuthorities: ApprovalAuthorities,
  approvalDelegations: ApprovalDelegations,
  signInSecurity: SignInSecurity,
  accessLog: AccessLog,
  localization: Localization,
  branding: Branding,
  helpText: HelpText,
  modules: Modules,
  planBilling: PlanBilling,
};

function SectionFailed({ onRetry }: { onRetry: () => void }) {
  const t = useT();
  return (
    <div role="alert" className="surface flex flex-wrap items-center justify-between gap-3 p-6 text-sm text-slate-700 dark:text-slate-200">
      <p>{t('This section could not be shown. Try again, or reload the page.')}</p>
      <button type="button" className="btn-secondary" onClick={onRetry}>{t('Try again')}</button>
    </div>
  );
}

let sectionFailed = false;

/**
 * Whether a module editor has failed to render on this page. React's recovery from a render error can trip the app
 * router's hooks on the next in-page navigation (React error #310, seen on production builds), so after a failure
 * Setup moves to the next section with a full load of the same address instead.
 */
export function setupSectionHasFailed(): boolean {
  return sectionFailed;
}

/**
 * Each module's editor fails on its own: an error while one renders is shown in its section, with Try again, and the
 * rest of Setup (its navigation, the other sections) keeps working instead of the whole page falling over.
 */
class SectionBoundary extends Component<{ children: ReactNode }, { failed: boolean; attempt: number }> {
  state = { failed: false, attempt: 0 };
  static getDerivedStateFromError() {
    sectionFailed = true;
    return { failed: true };
  }
  render() {
    if (this.state.failed) return <SectionFailed onRetry={() => this.setState(state => ({ failed: false, attempt: state.attempt + 1 }))} />;
    return <div key={this.state.attempt} className="min-w-0">{this.props.children}</div>;
  }
}

export function ModuleSetupSection({ id, ...props }: ModuleSetupPanelProps & { id: SetupModuleSectionId }) {
  const Panel = PANELS[id];
  return <div data-module-section={id} className="min-w-0"><SectionBoundary key={id}><Panel {...props} /></SectionBoundary></div>;
}
