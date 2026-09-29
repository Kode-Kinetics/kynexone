'use client';

/**
 * Phone/tablet bottom navigation — glass, because content scrolls beneath it.
 * At most five destinations, each with an icon AND a label. Visibility follows the same
 * permission + module rules as the sidebar, so the bar never offers a page its API refuses.
 * "More" opens the full sidebar drawer rather than duplicating it.
 * Lives outside <main> (e2e helpers read the first <main> as the page body).
 */

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { CheckSquare2, Clock3, Gauge, Menu, UsersRound } from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useT } from '../hooks/useT';

const DESTINATIONS = [
  { label: 'Home', path: '/dashboard', icon: Gauge, perms: ['dashboard.read'] },
  { label: 'People', path: '/people', icon: UsersRound, perms: ['employees.read'] },
  { label: 'Time', path: '/attendance', icon: Clock3, perms: ['attendance.read', 'attendance.write', 'attendance.kiosk'] },
  { label: 'Actions', path: '/approvals', icon: CheckSquare2, perms: ['approvals.read', 'approvals.decide'] },
];

export function MobileBottomNav({ onOpenMore }: { onOpenMore: () => void }) {
  const t = useT();
  const pathname = usePathname() ?? '';
  const { hasPermission } = useAuth();
  const { verdictForPath } = useFeatureFlags();

  const items = DESTINATIONS.filter(
    (d) => d.perms.some((p) => hasPermission(p)) && verdictForPath(d.path).allowed,
  );

  const itemCls = (active: boolean) =>
    `wg-press flex min-w-0 flex-1 flex-col items-center justify-center gap-0.5 rounded-xl py-1.5 text-[11px] font-semibold outline-none focus-visible:ring-2 focus-visible:ring-sapphire ${
      active ? 'text-sapphire dark:text-blue-300' : 'text-slate-600 dark:text-slate-300'
    }`;

  return (
    <nav
      aria-label={t('Primary')}
      className="wg-glass fixed inset-x-0 bottom-0 z-30 border-t wg-safe-bottom lg:hidden"
    >
      <ul className="mx-auto flex h-16 max-w-lg items-stretch gap-1 px-2">
        {items.map((d) => {
          const active = pathname === d.path || pathname.startsWith(`${d.path}/`);
          const Icon = d.icon;
          return (
            <li key={d.path} className="flex min-w-0 flex-1">
              <Link href={d.path} aria-current={active ? 'page' : undefined} className={itemCls(active)}>
                <span className={`grid h-7 w-12 place-items-center rounded-full transition-colors duration-[140ms] ${active ? 'bg-sapphire/10 dark:bg-blue-400/15' : ''}`}>
                  <Icon className="h-[18px] w-[18px]" aria-hidden />
                </span>
                <span className="truncate">{t(d.label)}</span>
              </Link>
            </li>
          );
        })}
        <li className="flex min-w-0 flex-1">
          <button type="button" onClick={onOpenMore} className={itemCls(false)} aria-haspopup="dialog">
            <span className="grid h-7 w-12 place-items-center rounded-full">
              <Menu className="h-[18px] w-[18px]" aria-hidden />
            </span>
            <span className="truncate">{t('More')}</span>
          </button>
        </li>
      </ul>
    </nav>
  );
}
