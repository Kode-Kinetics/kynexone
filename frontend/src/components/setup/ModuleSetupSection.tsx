'use client';

import dynamic from 'next/dynamic';
import type { ComponentType } from 'react';
import type { CompanyDto } from '../../api/organization';
import type { SetupModuleSectionId } from '../../lib/setupModuleSections';

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

const LeaveTypes = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.LeaveTypesTab onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const LeavePolicies = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.PoliciesTab onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const HolidayCalendars = dynamic(() => import('../../views/LeavePage').then(module => {
  const Panel = ({ onDirtyChange }: ModuleSetupPanelProps) => <module.HolidayCalendarTab onDirtyChange={onDirtyChange} />;
  return Panel;
}));
const OnboardingChecklists = dynamic(() => import('./OnboardingPolicyWorkspace').then(module => {
  const Panel = ({ onDirtyChange, companies }: ModuleSetupPanelProps) => <module.OnboardingPolicyWorkspace companies={companies ?? []} onDirtyChange={onDirtyChange ?? (() => {})} />;
  return Panel;
}));

/** Not moved yet: the section exists, its module's editor is still to be brought here. */
function pending(id: SetupModuleSectionId): Panel {
  const Pending = () => <p data-testid="module-section-pending" className="surface p-6 text-sm text-slate-600">{id}</p>;
  return Pending;
}

const PANELS: Record<SetupModuleSectionId, Panel> = {
  openingBalances: pending('openingBalances'),
  leaveTypes: LeaveTypes,
  leavePolicies: LeavePolicies,
  holidayCalendars: HolidayCalendars,
  shiftDefinitions: pending('shiftDefinitions'),
  shiftPolicy: pending('shiftPolicy'),
  overtimePolicies: pending('overtimePolicies'),
  attendanceDevices: pending('attendanceDevices'),
  benefitPlans: pending('benefitPlans'),
  loanTypes: pending('loanTypes'),
  loanPolicies: pending('loanPolicies'),
  advancePolicy: pending('advancePolicy'),
  bonusTypes: pending('bonusTypes'),
  payslipTemplates: pending('payslipTemplates'),
  onboardingChecklists: OnboardingChecklists,
  performanceScorecards: pending('performanceScorecards'),
  letterTemplates: pending('letterTemplates'),
  engagementPrivacy: pending('engagementPrivacy'),
  taxPolicies: pending('taxPolicies'),
  complianceProfiles: pending('complianceProfiles'),
  saudiCompliance: pending('saudiCompliance'),
  countryPayrollKeys: pending('countryPayrollKeys'),
  users: pending('users'),
  roles: pending('roles'),
  permissionMatrix: pending('permissionMatrix'),
  permissions: pending('permissions'),
  permissionGrantors: pending('permissionGrantors'),
  approvalAuthorities: pending('approvalAuthorities'),
  approvalDelegations: pending('approvalDelegations'),
  signInSecurity: pending('signInSecurity'),
  accessLog: pending('accessLog'),
  localization: pending('localization'),
  branding: pending('branding'),
  helpText: pending('helpText'),
  modules: pending('modules'),
  planBilling: pending('planBilling'),
};

export function ModuleSetupSection({ id, ...props }: ModuleSetupPanelProps & { id: SetupModuleSectionId }) {
  const Panel = PANELS[id];
  return <div data-module-section={id} className="min-w-0"><Panel {...props} /></div>;
}
