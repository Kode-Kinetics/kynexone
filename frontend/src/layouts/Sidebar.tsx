'use client';

import { useRef, useState } from 'react';
import { ChevronLeft, ChevronRight, ChevronDown, LogOut, X } from 'lucide-react';
import { useRouter, usePathname } from 'next/navigation';
import { Avatar } from '../components/Avatar';
import { Logo } from '../components/Logo';
import { navigationGroups, navigationHints } from '../routes/navigation';
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
  const { isFeatureEnabled, verdictForPath } = useFeatureFlags();
  const { t } = useLocale();

  // Hover / focus info tag for a menu entry. Rendered fixed beside the rail, because the nav
  // scrolls (overflow hidden) and would clip an absolutely positioned tag.
  const [tip, setTip] = useState<{ path: string; label: string; hint: string; top: number; left: number } | null>(null);
  const tipTimer = useRef<number | null>(null);
  // The element the visible tip belongs to. Kept so a scroll can REPOSITION the tip instead of
  // destroying it — see onScroll on the <nav> below.
  const tipAnchor = useRef<HTMLElement | null>(null);

  const placeTip = (el: HTMLElement, path: string, label: string, hint: string) => {
    const r = el.getBoundingClientRect();
    const rtl = document.documentElement.dir === 'rtl';
    setTip({ path, label, hint, top: r.top + r.height / 2, left: rtl ? window.innerWidth - r.left + 10 : r.right + 10 });
  };

  const showTip = (el: HTMLElement, path: string | undefined, label: string, delay: number) => {
    const hint = path ? navigationHints[path] : undefined;
    if (!path || !hint) return;
    if (tipTimer.current) window.clearTimeout(tipTimer.current);
    tipAnchor.current = el;
    // A zero delay is shown SYNCHRONOUSLY. Keyboard focus asks for 0, and deferring it by even one
    // tick lost the race against the scroll the browser performs to bring the focused item into
    // view: the scroll handler ran first and cleared the pending timer, so the tip never appeared.
    if (delay <= 0) { placeTip(el, path, label, hint); return; }
    tipTimer.current = window.setTimeout(() => placeTip(el, path, label, hint), delay);
  };

  const hideTip = () => {
    if (tipTimer.current) window.clearTimeout(tipTimer.current);
    tipAnchor.current = null;
    setTip(null);
  };

  /**
   * Scrolling the rail must not cancel a tip the KEYBOARD just opened.
   *
   * Tabbing to an item below the fold makes the browser scroll it into view, which fired this
   * handler and hid the tooltip that focus had opened a moment earlier — so keyboard users got no
   * navigation hints at all, while mouse users got them fine. The fix is to keep the tip while its
   * anchor still holds focus and simply move it to the anchor's new position; a scroll with nothing
   * focused is still a mouse scroll, and still dismisses.
   */
  const onNavScroll = () => {
    const el = tipAnchor.current;
    if (el && document.activeElement === el) {
      const hint = tip?.path ? navigationHints[tip.path] : undefined;
      if (tip && hint) placeTip(el, tip.path, tip.label, hint);
      return;
    }
    hideTip();
  };

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

      {/*
        The off-canvas position is a transform, and transforms stay physical even when the panel
        is anchored logically (`start-0`). Without the rtl: variant, a negative X translation
        hides the drawer off the LEFT edge in both directions — in Arabic that slides it straight
        across the viewport instead of off-screen.
      */}
      <aside
        className={`wg-glass fixed inset-y-0 start-0 z-40 flex flex-col border-e transition-all duration-300 lg:sticky lg:top-0 lg:h-screen lg:translate-x-0 ${
          isOpen ? 'translate-x-0' : '-translate-x-full max-lg:rtl:translate-x-full'
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
        <nav aria-label="Primary navigation" onScroll={onNavScroll} onKeyDown={(e) => { if (e.key === 'Escape') hideTip(); }} className="flex-1 overflow-y-auto overflow-x-hidden py-3">
          {navigationGroups.map((group, gi) => {
            // Module visibility is resolved from the item's PATH against the backend catalog,
            // not only from the hand-tagged `requiredFeatureKey`. Only 8 of ~35 items ever carried
            // that tag, so switching off Timesheets, Benefits, Loans or HR Letters left their nav
            // entries in place, leading straight to a page whose API refuses it.
            // `requiredFeatureKey` is still honoured as an explicit override.
            const visibleItems = group.items.filter(
              (item) =>
                canSee(item.requiredPermissions) &&
                (!item.requiredFeatureKey || isFeatureEnabled(item.requiredFeatureKey)) &&
                (!item.path || verdictForPath(item.path).allowed) &&
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
                            aria-describedby={tip?.path === item.path ? 'nav-tip' : undefined}
                            onMouseEnter={(e) => showTip(e.currentTarget, item.path, t(item.label), 350)}
                            onMouseLeave={hideTip}
                            onFocus={(e) => { if (e.currentTarget.matches(':focus-visible')) showTip(e.currentTarget, item.path, t(item.label), 0); }}
                            onBlur={hideTip}
                            onClick={() => { hideTip(); handleNav(item.path); }}
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
                              <span className="absolute end-2 top-2 h-1.5 w-1.5 rounded-full bg-sapphire" />
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
                <div className="min-w-0 flex-1 text-start">
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

      {tip && (
        <div id="nav-tip" role="tooltip"
          className="wg-tip pointer-events-none fixed z-[60] hidden w-[260px] -translate-y-1/2 rounded-xl border border-slate-200/80 bg-white px-3.5 py-2.5 shadow-[0_12px_32px_-12px_rgba(15,23,42,0.35)] lg:block dark:border-white/10 dark:bg-slate-900"
          style={{ top: tip.top, insetInlineStart: tip.left }}>
          <span aria-hidden className="absolute -start-[5px] top-1/2 h-2.5 w-2.5 -translate-y-1/2 rotate-45 border-b border-s border-slate-200/80 bg-white dark:border-white/10 dark:bg-slate-900" />
          <span className="block text-[13px] font-semibold text-slate-900 dark:text-white">{tip.label}</span>
          <span className="mt-0.5 block text-xs leading-snug text-slate-600 dark:text-slate-300">{tip.hint}</span>
        </div>
      )}
    </>
  );
}
