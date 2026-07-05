'use client';

import { useState } from 'react';
import { ChevronLeft, ChevronRight, ChevronDown, LogOut, X } from 'lucide-react';
import { useRouter, usePathname } from 'next/navigation';
import { Avatar } from '../components/Avatar';
import { Logo } from '../components/Logo';
import { navigationGroups } from '../routes/navigation';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useCompany } from '../contexts/CompanyContext';
import { useLocale } from '../contexts/LocaleContext';

interface SidebarProps {
  isOpen: boolean;
  isCollapsed: boolean;
  onClose: () => void;
  onToggleCollapse: () => void;
}

export function Sidebar({ isOpen, isCollapsed, onClose, onToggleCollapse }: SidebarProps) {
  const router = useRouter();
  const pathname = usePathname();
  const { user, logout, hasPermission } = useAuth();
  const { isFeatureEnabled } = useFeatureFlags();
  const { t } = useLocale();

  // All groups expanded by default
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(
    () => new Set(navigationGroups.map((g) => g.label))
  );

  const toggleGroup = (label: string) => {
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  const { accountType, companies: accessibleCompanies } = useCompany();
  const canSeeGroupItem = accountType === 'Group' && accessibleCompanies.length >= 1;

  const canSee = (requiredPermissions?: string[]) => {
    if (!requiredPermissions || requiredPermissions.length === 0) return true;
    return requiredPermissions.some((p) => hasPermission(p));
  };

  const handleNav = (path?: string) => {
    if (path) router.push(path);
    onClose();
  };

  const handleLogout = async () => {
    await logout();
    router.replace('/login');
  };

  const allNavPaths = navigationGroups
    .flatMap((g) => g.items.map((i) => i.path))
    .filter((p): p is string => Boolean(p));

  const isActive = (path?: string) => {
    if (!path) return false;
    if (path === '/dashboard') return pathname === '/dashboard' || pathname === '/';
    if (pathname === path) return true;
    // Match as a prefix only when no more-specific sibling nav path also matches
    if (pathname?.startsWith(path + '/')) {
      const hasMoreSpecificMatch = allNavPaths.some(
        (p) => p !== path && p.startsWith(path + '/') && pathname.startsWith(p),
      );
      return !hasMoreSpecificMatch;
    }
    return false;
  };

  return (
    <>
      {/* Mobile overlay */}
      <div
        className={`fixed inset-0 z-30 bg-slate-950/60 backdrop-blur-sm transition-opacity duration-200 lg:hidden ${
          isOpen ? 'opacity-100' : 'pointer-events-none opacity-0'
        }`}
        onClick={onClose}
        aria-hidden="true"
      />

      <aside
        className={`fixed inset-y-0 left-0 z-40 flex flex-col border-r border-slate-200/70 bg-white/[0.93] backdrop-blur-xl transition-all duration-300 dark:border-white/[0.06] dark:bg-[#0c1120]/[0.90] lg:static lg:translate-x-0 ${
          isOpen ? 'translate-x-0' : '-translate-x-full'
        } ${isCollapsed ? 'lg:w-[60px]' : 'lg:w-[240px]'} w-[240px]`}
      >
        {/* Logo / header */}
        <div
          className={`relative flex h-[60px] shrink-0 items-center border-b border-slate-200/60 dark:border-white/[0.06] ${
            isCollapsed ? 'justify-center px-0' : 'justify-between px-4'
          }`}
        >
          <Logo collapsed={isCollapsed} />
          <div className="flex items-center gap-1">
            <button
              type="button"
              aria-label="Close navigation"
              onClick={onClose}
              className="grid h-7 w-7 place-items-center rounded-md text-slate-400 hover:bg-slate-100 lg:hidden dark:hover:bg-white/10"
            >
              <X className="h-3.5 w-3.5" />
            </button>
            {!isCollapsed && (
              <button
                type="button"
                aria-label="Collapse sidebar"
                onClick={onToggleCollapse}
                className="hidden h-8 w-8 place-items-center rounded-md text-slate-500 hover:bg-slate-100 hover:text-slate-800 lg:grid dark:text-slate-400 dark:hover:bg-white/10 dark:hover:text-slate-100"
              >
                <ChevronLeft className="h-5 w-5" />
              </button>
            )}
          </div>
        </div>

        {/* Navigation */}
        <nav aria-label="Primary navigation" className="flex-1 overflow-y-auto overflow-x-hidden py-3">
          {navigationGroups.map((group, gi) => {
            const visibleItems = group.items.filter(
              (item) =>
                canSee(item.requiredPermissions) &&
                (!item.requiredFeatureKey || isFeatureEnabled(item.requiredFeatureKey)) &&
                (!item.groupAccountOnly || canSeeGroupItem),
            );
            if (visibleItems.length === 0) return null;

            const isExpanded = isCollapsed || expandedGroups.has(group.label);
            const hasActiveItem = visibleItems.some((item) => isActive(item.path));

            return (
              <div key={group.label} className={gi > 0 ? 'mt-1' : ''}>

                {/* ── Group header ── */}
                {!isCollapsed ? (
                  <button
                    type="button"
                    onClick={() => toggleGroup(group.label)}
                    className={`group/hdr mb-0.5 flex w-full items-center justify-between rounded-lg px-3 py-1.5 transition-all duration-150 ${
                      isExpanded
                        ? 'hover:bg-slate-100/70 dark:hover:bg-white/[0.04]'
                        : 'hover:bg-slate-100/70 dark:hover:bg-white/[0.04]'
                    }`}
                  >
                    <span
                      className={`text-[10px] font-bold uppercase tracking-[0.14em] transition-colors duration-150 ${
                        hasActiveItem
                          ? 'text-sapphire dark:text-[#7AABFF]'
                          : 'text-slate-400 group-hover/hdr:text-slate-600 dark:text-slate-600 dark:group-hover/hdr:text-slate-400'
                      }`}
                    >
                      {t(group.label)}
                    </span>
                    <ChevronDown
                      className={`h-3 w-3 shrink-0 transition-all duration-200 ${
                        hasActiveItem
                          ? 'text-sapphire/60 dark:text-[#7AABFF]/60'
                          : 'text-slate-300 group-hover/hdr:text-slate-400 dark:text-slate-700 dark:group-hover/hdr:text-slate-500'
                      } ${isExpanded ? 'rotate-0' : '-rotate-90'}`}
                    />
                  </button>
                ) : (
                  gi > 0 && (
                    <div className="mx-3 mb-2 h-px bg-slate-100 dark:bg-white/[0.06]" />
                  )
                )}

                {/* ── Items — smooth CSS grid-row animation ── */}
                <div
                  className={`grid transition-[grid-template-rows] duration-200 ease-out ${
                    isExpanded ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]'
                  }`}
                >
                  <div className="overflow-hidden">
                    <div className="space-y-0.5 px-2 pb-1">
                      {visibleItems.map((item) => {
                        const Icon = item.icon;
                        const active = isActive(item.path);

                        return (
                          <button
                            key={item.label}
                            type="button"
                            title={isCollapsed ? t(item.label) : undefined}
                            onClick={() => handleNav(item.path)}
                            className={`nav-item group ${active ? 'nav-item-active' : 'nav-item-idle'} ${
                              isCollapsed ? 'justify-center' : ''
                            }`}
                          >
                            <span
                              className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-md transition-colors ${
                                active
                                  ? 'bg-sapphire/[0.12] text-sapphire dark:bg-sapphire/[0.18] dark:text-[#7AABFF]'
                                  : 'text-slate-400 group-hover:text-sapphire/70 dark:text-slate-500 dark:group-hover:text-[#7AABFF]/70'
                              }`}
                            >
                              <Icon className="h-4 w-4" />
                            </span>

                            {!isCollapsed && (
                              <span className="min-w-0 flex-1 truncate text-[13px]">{t(item.label)}</span>
                            )}

                            {item.badge != null && !isCollapsed && (
                              <span
                                className={`rounded-full px-1.5 py-0.5 text-[10px] font-bold leading-none ${
                                  active
                                    ? 'bg-sapphire/15 text-sapphire dark:bg-sapphire/20 dark:text-[#7AABFF]'
                                    : 'bg-sapphire/10 text-sapphire dark:bg-white/10 dark:text-slate-300'
                                }`}
                              >
                                {item.badge}
                              </span>
                            )}

                            {item.badge != null && isCollapsed && (
                              <span className="absolute right-2 top-2 h-1.5 w-1.5 rounded-full bg-sapphire" />
                            )}
                          </button>
                        );
                      })}
                    </div>
                  </div>
                </div>

              </div>
            );
          })}
        </nav>

        {/* Footer */}
        <div className="shrink-0 border-t border-slate-200/60 dark:border-white/[0.06]">
          {isCollapsed ? (
            <div className="flex flex-col items-center gap-2 py-3">
              <button
                type="button"
                aria-label="Expand sidebar"
                onClick={onToggleCollapse}
                className="hidden h-8 w-8 items-center justify-center rounded-md text-slate-500 hover:bg-slate-100 hover:text-slate-800 lg:flex dark:text-slate-400 dark:hover:bg-white/10 dark:hover:text-slate-100"
              >
                <ChevronRight className="h-5 w-5" />
              </button>
              <button
                type="button"
                aria-label={t('Sign out')}
                onClick={handleLogout}
                className="flex h-7 w-7 items-center justify-center rounded-md text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
              >
                <LogOut className="h-3.5 w-3.5" />
              </button>
            </div>
          ) : (
            <div className="p-3">
              <div className="flex items-center gap-2.5 rounded-lg border border-transparent px-2 py-2 transition hover:border-slate-200/70 hover:bg-white/70 dark:hover:border-white/[0.07] dark:hover:bg-white/[0.05]">
                <Avatar name={user?.fullName ?? 'User'} size="sm" />
                <div className="min-w-0 flex-1 text-left">
                  <p className="truncate text-[13px] font-semibold text-slate-800 dark:text-slate-100">
                    {user?.fullName ?? 'User'}
                  </p>
                  <p className="truncate text-[11px] text-slate-400 dark:text-slate-500">
                    {user?.roles[0] ?? 'Member'}
                  </p>
                </div>
                <button
                  type="button"
                  aria-label={t('Sign out')}
                  onClick={handleLogout}
                  className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-slate-300 hover:bg-rose-50 hover:text-rose-500 dark:text-slate-600 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
                >
                  <LogOut className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="mt-2 flex items-center justify-center gap-1.5">
                <Logo collapsed={false} size="sm" />
              </div>
            </div>
          )}
        </div>
      </aside>
    </>
  );
}
