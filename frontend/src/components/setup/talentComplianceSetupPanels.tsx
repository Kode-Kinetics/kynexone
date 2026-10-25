'use client';

import dynamic from 'next/dynamic';
import { useSearchParams } from 'next/navigation';
import type { ComponentType } from 'react';
import type { ModuleSetupPanelProps } from './ModuleSetupSection';

/**
 * The Setup sections of Talent & employee services and Payroll & compliance (lib/setupModuleSections.ts): each is the
 * module's own editor, loaded only when its section is opened. Onboarding checklists is OnboardingPolicyWorkspace.
 */
type Panel = ComponentType<ModuleSetupPanelProps>;

export const PerformanceScorecards: Panel = dynamic(() => import('../../views/PerformancePage').then(module => {
  const Panel = () => <module.TemplatesTab />;
  return Panel;
}));

export const LetterTemplates: Panel = dynamic(() => import('../../views/HrLettersPage').then(module => {
  // The letter catalogue is read again whenever the Letters page opens, so there is nothing to refresh from here.
  const Panel = () => <module.TemplatesTab onChanged={() => {}} />;
  return Panel;
}));

export const EngagementPrivacy: Panel = dynamic(() => import('../../views/EngagementPage').then(module => {
  const Panel = () => <module.EngagementPrivacyPanel />;
  return Panel;
}));

export const TaxPolicies: Panel = dynamic(() => import('../../views/TaxPoliciesPage').then(module => {
  const Panel = () => <module.TaxPoliciesPage />;
  return Panel;
}));

export const ComplianceProfiles: Panel = dynamic(() => import('../../views/ComplianceProfilesPage').then(module => {
  const Panel = () => <module.ComplianceProfilesPage />;
  return Panel;
}));

export const SaudiCompliance: Panel = dynamic(() => import('../../views/SaudiComplianceConfig').then(module => {
  // `section=qiwa|gosi|wps|labor|documents`: the Saudi compliance action centre links to the part that needs attention.
  const Panel = () => <module.SaudiComplianceConfig initialSection={module.configSectionFrom(useSearchParams().get('section'))} />;
  return Panel;
}));
