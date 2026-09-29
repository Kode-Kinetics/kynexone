import {
  Building2,
  BarChart3,
  DatabaseBackup,
  MessageSquareText,
  BriefcaseBusiness,
  CalendarCheck,
  ClipboardList,
  Clock3,
  FileText,
  Gauge,
  Headphones,
  Landmark,
  Layers3,
  FileSignature,
  Network,
  Scale,
  Settings2,
  ShieldCheck,
  TimerReset,
  Timer,
  UserCircle2,
  UserMinus,
  UserRoundCog,
  UsersRound,
  WalletCards,
  KeyRound,
  CheckSquare2,
  HeartPulse,
  FileSpreadsheet,
} from 'lucide-react';
import type { NavGroup } from '../types/ui';

export const navigationGroups: NavGroup[] = [
  {
    label: 'Overview',
    items: [
      { label: 'Dashboard', icon: Gauge, path: '/dashboard', requiredPermissions: ['dashboard.read'] },
      { label: 'Group Overview', icon: Building2, path: '/group', requiredPermissions: ['dashboard.read'], groupAccountOnly: true },
      { label: 'Self-Service', icon: UserCircle2, path: '/ess', requiredPermissions: ['ess.read'] },
      { label: 'My Benefits', icon: HeartPulse, path: '/ess/benefits', requiredPermissions: ['ess.read'] },
    ],
  },
  {
    label: 'HR & Time',
    items: [
      { label: 'People', icon: UsersRound, path: '/people', requiredPermissions: ['employees.read'] },
      { label: 'Org Chart', icon: Network, path: '/org-chart', requiredPermissions: ['employees.read'] },
      { label: 'HR Letters', icon: FileSignature, path: '/hr-letters', requiredPermissions: ['employees.read', 'employees.write'] },
      { label: 'Attendance', icon: Clock3, path: '/attendance', requiredPermissions: ['attendance.read', 'attendance.write', 'attendance.kiosk'] },
      { label: 'Leave', icon: ClipboardList, path: '/leave', requiredPermissions: ['leave.read', 'leave.write'] },
      { label: 'Shifts & Rosters', icon: CalendarCheck, path: '/shifts', requiredPermissions: ['attendance.read'], requiredFeatureKey: 'shifts' },
      { label: 'Overtime', icon: TimerReset, path: '/overtime', requiredPermissions: ['overtime.read', 'overtime.write'], requiredFeatureKey: 'overtime' },
      { label: 'Timesheets', icon: Timer, path: '/timesheets', requiredPermissions: ['ess.read', 'attendance.read', 'manager.read'] },
    ],
  },
  {
    label: 'Finance & Talent',
    items: [
      { label: 'Payroll', icon: WalletCards, path: '/payroll', requiredPermissions: ['payroll.read'], requiredFeatureKey: 'payroll' },
      { label: 'Payslip Templates', icon: FileText, path: '/payroll/templates', requiredPermissions: ['payroll.read'], requiredFeatureKey: 'payslip_template_designer' },
      { label: 'Loans & Advances', icon: Landmark, path: '/loans', requiredPermissions: ['loans.read', 'loans.write'] },
      { label: 'Benefits', icon: HeartPulse, path: '/benefits', requiredPermissions: ['employees.write'] },
      { label: 'Recruitment', icon: BriefcaseBusiness, path: '/recruitment', requiredPermissions: ['recruitment.read', 'recruitment.write'], requiredFeatureKey: 'recruitment' },
      { label: 'Offboarding', icon: UserMinus, path: '/offboarding', requiredPermissions: ['employees.read', 'employees.write'] },
      { label: 'Performance', icon: BarChart3, path: '/performance', requiredPermissions: ['performance.read', 'performance.write'], requiredFeatureKey: 'performance' },
      { label: 'Compliance', icon: ShieldCheck, path: '/compliance', requiredPermissions: ['compliance.read', 'compliance.write'], requiredFeatureKey: 'compliance' },
    ],
  },
  {
    label: 'Insights & Reports',
    items: [
      { label: 'Assistant', icon: MessageSquareText, path: '/ai-assistant', requiredPermissions: ['ai.query', 'ai.insights_view'], requiredFeatureKey: 'ai_assistant' },
      { label: 'Reports & Analytics', icon: Layers3, path: '/reports', requiredPermissions: ['reports.read', 'reports.schedule'] },
    ],
  },
  {
    label: 'Administration',
    items: [
      { label: 'Compliance Profiles', icon: ShieldCheck, path: '/compliance-profiles', requiredPermissions: ['compliance.read'] },
      { label: 'Tax Policies', icon: Landmark, path: '/tax-policies', requiredPermissions: ['payroll.read'] },
      { label: 'Request Center', icon: Headphones, path: '/hr-requests', requiredPermissions: ['approvals.read', 'approvals.write', 'approvals.decide', 'ess.read'] },
      { label: 'Approvals', icon: CheckSquare2, path: '/approvals', requiredPermissions: ['approvals.read', 'approvals.decide'] },
      { label: 'User Management', icon: KeyRound, path: '/user-management', requiredPermissions: ['users.manage', 'roles.manage', 'security.manage'] },
      { label: 'Saudi Compliance', icon: ShieldCheck, path: '/saudi-compliance', requiredPermissions: ['compliance.read', 'qiwa.read'] },
      { label: 'GOSI Filing', icon: FileSpreadsheet, path: '/gosi-filing', requiredPermissions: ['payroll.read'] },
      { label: 'Tenant Admin', icon: Settings2, path: '/tenant-admin', requiredPermissions: ['security.manage'] },
      { label: 'Setup', icon: UserRoundCog, path: '/setup', requiredPermissions: ['organization.write'] },
      // Implementation-time screen: the consultant-facing front end for the opening-balance engine,
      // which until now had no caller at all and was driven from Postman.
      { label: 'Opening Balances', icon: DatabaseBackup, path: '/opening-balances', requiredPermissions: ['employees.write', 'payroll.write'] },
      // The other half of a go-live: reconciling a run against the register the OUTGOING system
      // produced for the same month. The engine was built and unit-proven with no caller.
      { label: 'Parallel Run Variance', icon: Scale, path: '/payroll/variance', requiredPermissions: ['payroll.read'] },
    ],
  },
];

export const navigationItems = navigationGroups.flatMap((g) => g.items);

/**
 * One-line explanations shown when a menu entry is hovered or focused: what the screen is for,
 * in the words an HR admin would use. Keyed by path so the menu definition above stays compact.
 */
export const navigationHints: Record<string, string> = {
  '/dashboard': 'Today at a glance: payroll, attendance, approvals and document expiries.',
  '/group': 'Headcount, payroll and compliance across every company in the group.',
  '/ess': 'Your own payslips, leave balance, requests and documents.',
  '/ess/benefits': 'The benefits you are enrolled in and what they cover.',
  '/people': 'Employee records: profiles, contracts, documents and job history.',
  '/org-chart': 'Who reports to whom, by department and manager.',
  '/hr-letters': 'Generate salary certificates, experience letters and other HR letters.',
  '/attendance': 'Daily punches, absences and late arrivals, with corrections.',
  '/leave': 'Leave requests, balances and the team leave calendar.',
  '/shifts': 'Plan shifts and rosters, and assign people to them.',
  '/overtime': 'Overtime requests and approved hours that flow into payroll.',
  '/timesheets': 'Hours worked per project or task, submitted for approval.',
  '/payroll': 'Run, review, approve and lock monthly payroll.',
  '/payroll/templates': 'Design the layout of the payslips employees receive.',
  '/loans': 'Salary advances and loans, with their repayment schedules.',
  '/benefits': 'Benefit plans offered to employees, and who is enrolled.',
  '/recruitment': 'Open positions, candidates and the hiring pipeline.',
  '/offboarding': 'Resignations and exits: clearance, final settlement and end of service.',
  '/performance': 'Goals, review cycles and ratings.',
  '/compliance': 'Expiring iqamas, passports and permits, and missing documents.',
  '/ai-assistant': 'Ask questions about your workforce data in plain language.',
  '/reports': 'Standard and custom reports, with scheduled delivery.',
  '/compliance-profiles': 'Country and company rules that compliance checks run against.',
  '/tax-policies': 'Tax and contribution rules applied during payroll.',
  '/hr-requests': 'Requests raised by employees to HR, and their status.',
  '/approvals': 'Everything waiting for your decision, oldest first.',
  '/user-management': 'Users, roles and what each role is allowed to do.',
  '/saudi-compliance': 'Saudization (Nitaqat), Qiwa and Mudad status for the company.',
  '/gosi-filing': 'Prepare and check the monthly GOSI contribution filing.',
  '/tenant-admin': 'Company-wide settings: security, localisation and modules.',
  '/setup': 'Companies, branches, departments and job titles.',
  '/opening-balances': 'Load leave, loan and end-of-service balances when going live.',
  '/payroll/variance': 'Compare a payroll run with the previous system’s register for the same month.',
};
