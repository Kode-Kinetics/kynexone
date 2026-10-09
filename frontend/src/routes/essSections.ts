import {
  CalendarDays,
  HeartPulse,
  Inbox,
  LayoutDashboard,
  WalletCards,
  type LucideIcon,
} from 'lucide-react';
import type { NavItem } from '../types/ui';

/**
 * The employee's own workspace at /ess. Every login is an employee first, so these pages live
 * inside Self-Service as sections and tabs rather than as separate sidebar entries: the sidebar
 * keeps one "Self-Service" link and the workspace carries its own navigation (EssWorkspaceNav).
 *
 * Every page here is gated on ess.read (app/(dashboard)/ess/…/page.tsx) and reads only the
 * caller's own record. A page with a feature key shows only while that tenant flag is on.
 */
export interface EssPage {
  /** The page's own name, as its heading and the command palette show it. */
  label: string;
  /** The shorter name on the tab inside its section. */
  tab: string;
  path: string;
  /** What the page is for, in the employee's words. */
  hint: string;
  requiredFeatureKey?: string;
  /** Shown only when one of the user's companies is in one of these countries (ISO 3166 alpha-2). */
  countries?: string[];
}

export interface EssSection {
  id: 'overview' | 'pay' | 'time' | 'requests' | 'benefits';
  label: string;
  icon: LucideIcon;
  pages: EssPage[];
}

export const ESS_HOME_PATH = '/ess';
export const ESS_DOCUMENTS_PATH = '/ess/documents';
export const ESS_JAWAZAT_PATH = '/ess/jawazat';
export const ESS_BENEFITS_PATH = '/ess/benefits';

export const ESS_SECTIONS: EssSection[] = [
  {
    id: 'overview',
    label: 'Overview',
    icon: LayoutDashboard,
    pages: [
      { label: 'Self-Service', tab: 'Overview', path: ESS_HOME_PATH, hint: 'Your day at a glance: what needs you, your balances and what is coming up.' },
      { label: 'My employee details', tab: 'My details', path: '/ess/onboarding', hint: 'Complete your contact details and share documents securely with HR.' },
    ],
  },
  {
    id: 'pay',
    label: 'Pay',
    icon: WalletCards,
    pages: [
      { label: 'My Payslips', tab: 'Payslips', path: '/ess/payslips', hint: 'Your payslips by month, with every line and a PDF to download.' },
      // Release A (release_a opt-in flag): the employee's own package and deductions.
      { label: 'My package', tab: 'Package', path: '/ess/package', hint: 'Your pay and the benefits fixed for your contract year.', requiredFeatureKey: 'release_a' },
      { label: 'My deductions', tab: 'Deductions', path: '/ess/deductions', hint: 'What is deducted from your pay, why, and what is left to repay.', requiredFeatureKey: 'release_a' },
    ],
  },
  {
    id: 'time',
    label: 'Leave and time',
    icon: CalendarDays,
    pages: [
      { label: 'My Leave', tab: 'Leave', path: '/ess/leave', hint: 'Your leave balances and requests. Apply for leave and cancel a request still waiting for approval.' },
      { label: 'My Overtime', tab: 'Overtime', path: '/ess/overtime', hint: 'Request overtime you have worked and follow it through approval.', requiredFeatureKey: 'overtime' },
    ],
  },
  {
    id: 'requests',
    label: 'Requests',
    icon: Inbox,
    pages: [
      { label: 'My HR Requests', tab: 'HR requests', path: '/ess/requests', hint: 'Ask HR for something, follow your requests, and reply to HR on each one.' },
      { label: 'My letters', tab: 'Letters', path: ESS_DOCUMENTS_PATH, hint: 'Ask HR for a salary certificate or another letter, and download it once issued.' },
      // A Saudi exit and re-entry visa (Jawazat): meaningless to a company outside the Kingdom.
      { label: 'Jawazat Requests', tab: 'Exit and re-entry visa', path: ESS_JAWAZAT_PATH, hint: 'Exit and re-entry requests, internal review and travel notifications.', countries: ['SA'] },
    ],
  },
  {
    id: 'benefits',
    label: 'Benefits',
    icon: HeartPulse,
    pages: [
      { label: 'My Benefits', tab: 'Benefits', path: ESS_BENEFITS_PATH, hint: 'The benefits you are enrolled in and what they cover.' },
    ],
  },
];

export const essPages: EssPage[] = ESS_SECTIONS.flatMap((s) => s.pages);

/** The pages of a section this tenant has switched on, for a user whose companies are in `countryCodes`. */
export function visibleEssPages(section: EssSection, isFeatureEnabled: (key: string) => boolean, countryCodes: string[] = []): EssPage[] {
  return section.pages.filter((p) =>
    (!p.requiredFeatureKey || isFeatureEnabled(p.requiredFeatureKey))
    && (!p.countries || p.countries.some((c) => countryCodes.includes(c))));
}

/** Whether a page is offered to this user: flag, country and module (`verdictForPath`) all allow it. */
export function essPageAllowed(
  path: string,
  isFeatureEnabled: (key: string) => boolean,
  countryCodes: string[],
  verdictForPath: (path: string) => { allowed: boolean },
): boolean {
  const section = ESS_SECTIONS.find((s) => s.pages.some((p) => p.path === path));
  return !!section && visibleEssPages(section, isFeatureEnabled, countryCodes).some((p) => p.path === path) && verdictForPath(path).allowed;
}

/** The page the pathname is on: an exact match, or the page a deeper path sits under. */
export function activeEssPage(pathname: string): EssPage | undefined {
  return essPages.find((p) => p.path === pathname)
    ?? essPages.filter((p) => p.path !== ESS_HOME_PATH && pathname.startsWith(`${p.path}/`))
      .sort((a, b) => b.path.length - a.path.length)[0];
}

export function activeEssSection(pathname: string): EssSection | undefined {
  const page = activeEssPage(pathname);
  return page ? ESS_SECTIONS.find((s) => s.pages.includes(page)) : undefined;
}

/** The workspace pages as menu items, so the command palette still finds "My Payslips" and the rest. */
export const essNavItems: NavItem[] = ESS_SECTIONS.flatMap((s) =>
  s.pages
    .filter((p) => p.path !== ESS_HOME_PATH)
    .map((p) => ({ label: p.label, icon: s.icon, path: p.path, requiredPermissions: ['ess.read'], requiredFeatureKey: p.requiredFeatureKey })),
);
