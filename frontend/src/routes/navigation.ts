import {
  AlertOctagon,
  BarChart3,
  Bot,
  BriefcaseBusiness,
  CalendarCheck,
  ClipboardList,
  Clock3,
  FileText,
  Gauge,
  Headphones,
  Landmark,
  Layers3,
  Network,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  TimerReset,
  UserCircle2,
  UserPlus,
  UserRoundCog,
  UsersRound,
  WalletCards,
  KeyRound,
  CheckSquare2,
} from 'lucide-react';
import type { NavGroup } from '../types/ui';

export const navigationGroups: NavGroup[] = [
  {
    label: 'Overview',
    items: [
      { label: 'Dashboard', icon: Gauge, path: '/dashboard', requiredPermissions: ['dashboard.read'] },
      { label: 'Self-Service', icon: UserCircle2, path: '/ess', requiredPermissions: ['ess.read'] },
    ],
  },
  {
    label: 'HR & Time',
    items: [
      { label: 'People', icon: UsersRound, path: '/people', requiredPermissions: ['employees.read'] },
      { label: 'Org Chart', icon: Network, path: '/org-chart', requiredPermissions: ['employees.read'] },
      { label: 'Attendance', icon: Clock3, path: '/attendance', requiredPermissions: ['attendance.read', 'attendance.write', 'attendance.kiosk'] },
      { label: 'Leave', icon: ClipboardList, path: '/leave', requiredPermissions: ['leave.read', 'leave.write'] },
      { label: 'Shifts & Rosters', icon: CalendarCheck, path: '/shifts', requiredPermissions: ['attendance.read'], requiredFeatureKey: 'shifts' },
      { label: 'Overtime', icon: TimerReset, path: '/overtime', requiredPermissions: ['overtime.read', 'overtime.write'], requiredFeatureKey: 'overtime' },
    ],
  },
  {
    label: 'Finance & Talent',
    items: [
      { label: 'Payroll', icon: WalletCards, path: '/payroll', requiredPermissions: ['payroll.read'], requiredFeatureKey: 'payroll' },
      { label: 'Payslip Templates', icon: FileText, path: '/payroll/templates', requiredPermissions: ['payroll.read'], requiredFeatureKey: 'payslip_template_designer' },
      { label: 'Loans & Advances', icon: Landmark, path: '/loans', requiredPermissions: ['loans.read', 'loans.write'] },
      { label: 'Recruitment', icon: BriefcaseBusiness, path: '/recruitment', requiredPermissions: ['recruitment.read', 'recruitment.write'], requiredFeatureKey: 'recruitment' },
      { label: 'Onboarding', icon: UserPlus, path: '/recruitment/onboarding', requiredPermissions: ['recruitment.read', 'recruitment.write'], requiredFeatureKey: 'recruitment' },
      { label: 'Performance', icon: BarChart3, path: '/performance', requiredPermissions: ['performance.read', 'performance.write'], requiredFeatureKey: 'performance' },
      { label: 'Calibration', icon: SlidersHorizontal, path: '/performance/calibration', requiredPermissions: ['performance.read', 'performance.write'], requiredFeatureKey: 'performance' },
      { label: 'PIP & Probation', icon: AlertOctagon, path: '/performance/pip', requiredPermissions: ['performance.read', 'performance.write'], requiredFeatureKey: 'performance' },
      { label: 'Compliance', icon: ShieldCheck, path: '/compliance', requiredPermissions: ['compliance.read', 'compliance.write'], requiredFeatureKey: 'compliance' },
    ],
  },
  {
    label: 'Intelligence',
    items: [
      { label: 'AI Assistant', icon: Bot, path: '/ai-assistant', requiredPermissions: ['ai.query', 'ai.insights_view'], requiredFeatureKey: 'ai_assistant' },
      { label: 'Reports & Analytics', icon: Layers3, path: '/reports', requiredPermissions: ['reports.read', 'reports.schedule'] },
    ],
  },
  {
    label: 'Administration',
    items: [
      { label: 'Request Center', icon: Headphones, path: '/hr-requests', requiredPermissions: ['approvals.read', 'approvals.write', 'approvals.decide', 'ess.read'] },
      { label: 'Approvals', icon: CheckSquare2, path: '/approvals', requiredPermissions: ['approvals.read', 'approvals.decide'] },
      { label: 'User Management', icon: KeyRound, path: '/user-management', requiredPermissions: ['users.manage', 'roles.manage', 'security.manage'] },
      { label: 'Saudi Compliance', icon: ShieldCheck, path: '/saudi-compliance', requiredPermissions: ['compliance.read', 'qiwa.read'] },
      { label: 'Tenant Admin', icon: Settings2, path: '/tenant-admin', requiredPermissions: ['security.manage'] },
      { label: 'Setup', icon: UserRoundCog, path: '/setup', requiredPermissions: ['organization.write'] },
    ],
  },
];

export const navigationItems = navigationGroups.flatMap((g) => g.items);
