'use client';

import { useEffect, useState, useRef, useCallback } from 'react';
import { useRouter, usePathname } from 'next/navigation';
import Link from 'next/link';
import {
  LayoutDashboard, Building2, UserPlus, Users, Shield, CreditCard,
  Zap, Brain, Megaphone, Headphones, Lock, FileCheck, FileText,
  Activity, Settings, Search, LogOut, ChevronRight,
  MonitorPlay, Menu, X, CheckCircle, AlertTriangle, Circle,
} from 'lucide-react';
import { Logo } from '@/src/components/Logo';
import { platformApi } from '@/src/api/platform';
import { PlatformToastProvider } from '@/src/components/platform/PlatformToast';
import { PlatformErrorBoundary } from '@/src/components/platform/PlatformErrorBoundary';

// ── Navigation Tree ───────────────────────────────────────────────────────────

const NAV_GROUPS = [
  {
    group: 'OVERVIEW',
    items: [
      { href: '/platform/dashboard', label: 'Command Center', icon: LayoutDashboard },
    ],
  },
  {
    group: 'TENANTS',
    items: [
      { href: '/platform/tenants', label: 'All Tenants',       icon: Building2 },
      { href: '/platform/tenants/new', label: 'Provision Tenant', icon: UserPlus },
    ],
  },
  {
    group: 'TEAM',
    items: [
      { href: '/platform/team',  label: 'Platform Team',       icon: Users },
      { href: '/platform/roles', label: 'Roles & Permissions', icon: Shield },
    ],
  },
  {
    group: 'REVENUE',
    items: [
      { href: '/platform/billing', label: 'Billing & Invoices', icon: CreditCard },
      { href: '/platform/plans',   label: 'Plans & Features',   icon: Zap },
      { href: '/platform/pricing', label: 'Pricing & Quotes',   icon: CreditCard },
    ],
  },
  {
    group: 'INTELLIGENCE',
    items: [
      { href: '/platform/ai-usage',   label: 'AI Usage & Cost',   icon: Brain },
      { href: '/platform/marketing',  label: 'Marketing & Leads', icon: Megaphone },
    ],
  },
  {
    group: 'SUPPORT',
    items: [
      { href: '/platform/support',          label: 'Support Center',   icon: Headphones },
      { href: '/platform/support-sessions', label: 'Support Sessions', icon: MonitorPlay },
    ],
  },
  {
    group: 'SECURITY',
    items: [
      { href: '/platform/security',   label: 'Security Center',   icon: Lock },
      { href: '/platform/compliance', label: 'Compliance Center', icon: FileCheck },
      { href: '/platform/audit-logs', label: 'Audit Logs',        icon: FileText },
    ],
  },
  {
    group: 'OPERATIONS',
    items: [
      { href: '/platform/system-health', label: 'System Health', icon: Activity },
      { href: '/platform/settings',      label: 'Settings',      icon: Settings },
    ],
  },
];

// ── JWT decode (display only — no verification) ───────────────────────────────

function decodeJwt(token: string): Record<string, unknown> | null {
  try {
    return JSON.parse(atob(token.split('.')[1]));
  } catch { return null; }
}

// ── Sidebar ───────────────────────────────────────────────────────────────────

function Sidebar({ open, onClose }: { open: boolean; onClose: () => void }) {
  const pathname = usePathname();
  const router   = useRouter();

  function isActive(href: string) {
    if (href === '/platform/dashboard') return pathname === href;
    return pathname === href || pathname.startsWith(href + '/');
  }

  function logout() {
    localStorage.removeItem('platform_access_token');
    router.replace('/platform/login');
  }

  const token   = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
  const payload = token ? decodeJwt(token) : null;
  const email   = (payload?.email as string) ?? 'Platform Admin';
  const initials = email.slice(0, 2).toUpperCase();

  return (
    <>
      {/* Mobile backdrop */}
      {open && (
        <div
          className="fixed inset-0 z-30 bg-black/60 lg:hidden"
          onClick={onClose}
        />
      )}

      <aside
        className={`fixed top-0 left-0 z-40 h-screen w-60 bg-[#0d1117] border-r border-white/[0.06] flex flex-col
          transition-transform duration-200 lg:translate-x-0
          ${open ? 'translate-x-0' : '-translate-x-full'}`}
      >
        {/* Logo + env */}
        <div className="h-[52px] flex items-center gap-2.5 px-4 border-b border-white/[0.06] shrink-0">
          <Logo size="sm" />
          <div className="min-w-0 flex-1">
            <p className="text-[11px] font-bold text-white tracking-wide truncate">KynexOne</p>
            <p className="text-[10px] text-slate-600 truncate leading-none mt-0.5">Control Plane</p>
          </div>
          <span className="shrink-0 text-[9px] font-semibold bg-blue-500/15 text-blue-400 border border-blue-500/25 px-1.5 py-0.5 rounded uppercase tracking-wider">
            PROD
          </span>
        </div>

        {/* Nav */}
        <nav className="flex-1 overflow-y-auto py-3 space-y-4 scrollbar-thin scrollbar-track-transparent scrollbar-thumb-white/10">
          {NAV_GROUPS.map(g => (
            <div key={g.group}>
              <p className="px-4 mb-1 text-[10px] font-semibold text-slate-600 uppercase tracking-widest">
                {g.group}
              </p>
              {g.items.map(item => {
                const Icon    = item.icon;
                const active  = isActive(item.href);
                return (
                  <Link
                    key={item.href}
                    href={item.href}
                    onClick={onClose}
                    className={`flex items-center gap-2.5 mx-2 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-150
                      ${active
                        ? 'bg-blue-500/10 text-white border border-blue-500/20 shadow-[0_0_12px_rgba(59,130,246,0.08)]'
                        : 'text-slate-500 hover:text-slate-200 hover:bg-white/[0.05] hover:translate-x-0.5'
                      }`}
                  >
                    <Icon className={`h-3.5 w-3.5 shrink-0 transition-colors ${active ? 'text-blue-400' : ''}`} />
                    <span className="truncate">{item.label}</span>
                    {active && <ChevronRight className="h-3 w-3 ml-auto text-blue-400/60" />}
                  </Link>
                );
              })}
            </div>
          ))}
        </nav>

        {/* Footer: system status + user */}
        <div className="border-t border-white/[0.06] px-3 py-3 space-y-2 shrink-0">
          {/* System status */}
          <div className="flex items-center gap-2 px-2 py-1.5 rounded-lg bg-emerald-500/5 border border-emerald-500/10">
            <span className="relative flex h-2 w-2 shrink-0">
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-60" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-500" />
            </span>
            <span className="text-[11px] text-emerald-400 font-medium">All Systems Operational</span>
          </div>
          {/* User row */}
          <div className="flex items-center gap-2.5 px-2">
            <div className="h-6 w-6 rounded-full bg-blue-500/20 border border-blue-500/30 flex items-center justify-center shrink-0">
              <span className="text-[9px] font-bold text-blue-400">{initials}</span>
            </div>
            <div className="min-w-0 flex-1">
              <p className="text-[11px] font-medium text-slate-300 truncate">{email}</p>
              <p className="text-[10px] text-slate-600 leading-none">Platform Admin</p>
            </div>
            <button
              type="button"
              onClick={logout}
              title="Sign out"
              className="text-slate-600 hover:text-rose-400 transition-colors ml-auto"
            >
              <LogOut className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      </aside>
    </>
  );
}

// ── Top Command Bar ───────────────────────────────────────────────────────────

function CommandBar({ onMenuOpen }: { onMenuOpen: () => void }) {
  const [search, setSearch]             = useState('');
  const [searchOpen, setSearchOpen]     = useState(false);
  const [searchResults, setSearchResults] = useState<{ id: string; name: string; slug: string; plan?: string }[]>([]);
  const [searching, setSearching]       = useState(false);
  const router  = useRouter();
  const ref     = useRef<HTMLDivElement>(null);

  const doSearch = useCallback(async (q: string) => {
    if (!q.trim()) { setSearchResults([]); return; }
    setSearching(true);
    try {
      const tenants = await platformApi.listTenants();
      const filtered = tenants
        .filter(t => t.name.toLowerCase().includes(q.toLowerCase()) || t.slug.toLowerCase().includes(q.toLowerCase()))
        .slice(0, 6)
        .map(t => ({ id: t.id, name: t.name, slug: t.slug, plan: t.subscription?.plan }));
      setSearchResults(filtered);
    } catch { setSearchResults([]); }
    finally { setSearching(false); }
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => doSearch(search), 250);
    return () => clearTimeout(timer);
  }, [search, doSearch]);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setSearchOpen(false);
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  return (
    <header className="fixed top-0 left-0 right-0 lg:left-60 h-[52px] z-20 bg-[#0d1117]/95 border-b border-white/[0.06] backdrop-blur-sm flex items-center gap-3 px-4">
      {/* Mobile menu button */}
      <button type="button" onClick={onMenuOpen} aria-label="Open navigation menu" className="lg:hidden text-slate-500 hover:text-white transition-colors">
        <Menu className="h-5 w-5" />
      </button>

      {/* Search */}
      <div className="relative flex-1 max-w-sm" ref={ref}>
        <div className="relative">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            onFocus={() => setSearchOpen(true)}
            placeholder="Search tenants…"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg pl-8 pr-3 py-1.5 text-sm text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 focus:bg-white/[0.06] transition-colors"
          />
          {searching && (
            <div className="absolute right-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 border border-t-transparent border-sapphire rounded-full animate-spin" />
          )}
        </div>
        {searchOpen && (search.trim() || searchResults.length > 0) && (
          <div className="absolute top-full left-0 right-0 mt-1.5 bg-[#161b22] border border-white/10 rounded-xl overflow-hidden shadow-2xl shadow-black/60 z-50">
            {searchResults.length === 0 && search.trim() && !searching && (
              <p className="px-4 py-3 text-xs text-slate-500">No tenants match &ldquo;{search}&rdquo;</p>
            )}
            {searchResults.map(t => (
              <button
                key={t.id}
                type="button"
                className="w-full flex items-center gap-3 px-4 py-2.5 hover:bg-white/[0.05] transition-colors text-left"
                onClick={() => {
                  setSearchOpen(false);
                  setSearch('');
                  router.push(`/platform/tenants/${t.id}`);
                }}
              >
                <div className="h-6 w-6 rounded-md bg-slate-800 border border-white/10 flex items-center justify-center shrink-0">
                  <span className="text-[9px] font-bold text-slate-400">{t.name.slice(0, 2).toUpperCase()}</span>
                </div>
                <div className="min-w-0">
                  <p className="text-sm text-white font-medium truncate">{t.name}</p>
                  <p className="text-[11px] text-slate-600 font-mono">/{t.slug}</p>
                </div>
                {t.plan && (
                  <span className="ml-auto text-[10px] text-slate-500 bg-white/5 px-1.5 py-0.5 rounded">
                    {t.plan}
                  </span>
                )}
              </button>
            ))}
          </div>
        )}
      </div>

      <div className="ml-auto flex items-center gap-2">
        {/* Status indicator */}
        <div className="hidden sm:flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-emerald-500/8 border border-emerald-500/15">
          <span className="relative flex h-1.5 w-1.5 shrink-0">
            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-50" />
            <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-emerald-500" />
          </span>
          <span className="text-[11px] font-medium text-emerald-400">Operational</span>
        </div>

        {/* Notifications — endpoint not yet implemented; button hidden until /api/platform/notifications exists */}
      </div>
    </header>
  );
}

// ── Shell ─────────────────────────────────────────────────────────────────────

function PlatformShell({ children }: { children: React.ReactNode }) {
  const router   = useRouter();
  const pathname = usePathname();
  const [checked, setChecked]       = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(false);

  const isLoginPage = pathname === '/platform/login';

  useEffect(() => {
    if (isLoginPage) { setChecked(true); return; }
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); }
    else { setChecked(true); }
  }, [isLoginPage, router]);

  if (!checked) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-[#0d1117]">
        <div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  if (isLoginPage) return <>{children}</>;

  return (
    <PlatformToastProvider>
      <div className="min-h-screen bg-[#0a0e14] text-white">
        <Sidebar open={sidebarOpen} onClose={() => setSidebarOpen(false)} />
        <CommandBar onMenuOpen={() => setSidebarOpen(true)} />
        <main className="lg:pl-60 pt-[52px] min-h-screen">
          <div className="max-w-[1440px] mx-auto px-5 py-6 animate-fade-in">
            <PlatformErrorBoundary fallbackRoute="/platform/dashboard">
              {children}
            </PlatformErrorBoundary>
          </div>
        </main>
      </div>
    </PlatformToastProvider>
  );
}

export default function PlatformLayout({ children }: { children: React.ReactNode }) {
  return <PlatformShell>{children}</PlatformShell>;
}
