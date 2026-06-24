'use client';

import { Bell, BellOff, MessageSquareText, CheckCheck, Globe, LogOut, Menu, Moon, Sun, Trash2, UserCircle2, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { Avatar } from '../components/Avatar';
import { useAuth } from '../contexts/AuthContext';
import { useLocale, LOCALES } from '../contexts/LocaleContext';
import { notificationsApi } from '../api/notifications';
import type { NotificationItem } from '../api/notifications';
import type { ThemeMode } from '../types/ui';

function LanguageSwitcher() {
  const { locale, setLocale, t } = useLocale();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  const current = LOCALES.find((l) => l.code === locale) ?? LOCALES[0];

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-label={t('Change language')}
        className="flex h-8 items-center gap-1.5 rounded-lg border border-slate-200/80 bg-white/60 px-2.5 text-xs font-semibold text-slate-600 backdrop-blur-sm transition hover:border-slate-300 hover:bg-white/90 dark:border-white/[0.08] dark:bg-white/[0.05] dark:text-slate-300 dark:hover:bg-white/[0.10]"
      >
        <Globe className="h-3.5 w-3.5 text-slate-400 dark:text-slate-500" />
        <span className="uppercase">{current.code}</span>
      </button>

      {open && (
        <div className="absolute left-0 top-full z-50 mt-2 w-44 overflow-hidden rounded-xl border border-slate-200/80 bg-white/[0.92] shadow-2xl backdrop-blur-xl dark:border-white/[0.08] dark:bg-[#0c1120]/[0.92]">
          {LOCALES.map((loc) => (
            <button
              key={loc.code}
              type="button"
              onClick={() => { setLocale(loc.code); setOpen(false); }}
              className={`flex w-full items-center justify-between px-4 py-2.5 text-sm transition hover:bg-slate-50 dark:hover:bg-white/[0.04] ${locale === loc.code ? 'bg-sapphire/[0.06] font-semibold text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}
            >
              <span>{loc.native}</span>
              {locale === loc.code && <span className="h-1.5 w-1.5 rounded-full bg-sapphire dark:bg-cyanAccent" />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

interface TopBarProps {
  theme: ThemeMode;
  onToggleTheme: () => void;
  onOpenSidebar: () => void;
  onOpenSearch: () => void;
  onAskKynexOne: () => void;
}

function timeAgo(utc: string) {
  const diff = Math.floor((Date.now() - new Date(utc).getTime()) / 1000);
  if (diff < 60) return 'just now';
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return `${Math.floor(diff / 86400)}d ago`;
}

function NotificationPanel({ onClose, onCountChange }: { onClose: () => void; onCountChange: (n: number) => void }) {
  const { t } = useLocale();
  const [items, setItems] = useState<NotificationItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [markingAll, setMarkingAll] = useState(false);

  const refresh = () => {
    notificationsApi.list()
      .then((data) => { setItems(data); onCountChange(data.filter(n => n.status === 'Unread').length); })
      .catch(() => {})
      .finally(() => setLoading(false));
  };

  useEffect(() => { refresh(); }, []);

  const markRead = async (id: string) => {
    await notificationsApi.markRead(id).catch(() => {});
    setItems((prev) => {
      const next = prev.map((n) => n.id === id ? { ...n, status: 'Read' } : n);
      onCountChange(next.filter(n => n.status === 'Unread').length);
      return next;
    });
  };

  const dismiss = async (id: string) => {
    await notificationsApi.dismiss(id).catch(() => {});
    setItems((prev) => {
      const next = prev.filter((n) => n.id !== id);
      onCountChange(next.filter(n => n.status === 'Unread').length);
      return next;
    });
  };

  const markAllRead = async () => {
    setMarkingAll(true);
    await notificationsApi.markAllRead().catch(() => {});
    setItems((prev) => prev.map((n) => ({ ...n, status: 'Read' })));
    onCountChange(0);
    setMarkingAll(false);
  };

  const unread = items.filter((n) => n.status === 'Unread');

  return (
    <div className="absolute right-0 top-full z-50 mt-2 w-[340px] rounded-xl border border-slate-200/80 bg-white/[0.92] shadow-2xl backdrop-blur-xl dark:border-white/[0.08] dark:bg-[#0c1120]/[0.92]">
      {/* Header */}
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
        <div className="flex items-center gap-2">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Notifications')}</p>
          {unread.length > 0 && (
            <span className="rounded-full bg-sapphire px-1.5 py-0.5 text-[10px] font-bold text-white">{unread.length} new</span>
          )}
        </div>
        <div className="flex items-center gap-1">
          {unread.length > 0 && (
            <button
              type="button"
              onClick={markAllRead}
              disabled={markingAll}
              title={t('All read')}
              aria-label={t('All read')}
              className="flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-medium text-slate-500 transition hover:bg-slate-100 hover:text-sapphire disabled:opacity-50 dark:text-slate-400 dark:hover:bg-white/10 dark:hover:text-cyanAccent"
            >
              <CheckCheck className="h-3.5 w-3.5" />
              {t('All read')}
            </button>
          )}
          <button type="button" onClick={onClose} aria-label="Close" className="grid h-6 w-6 place-items-center rounded-md text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      </div>

      {/* Body */}
      <div className="max-h-[360px] overflow-y-auto">
        {loading && (
          <div className="flex justify-center py-10">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        )}
        {!loading && items.length === 0 && (
          <div className="flex flex-col items-center py-10 text-center">
            <BellOff className="mb-3 h-8 w-8 text-slate-200 dark:text-slate-700" />
            <p className="text-sm font-medium text-slate-400 dark:text-slate-500">{t('All caught up')}</p>
            <p className="mt-0.5 text-xs text-slate-300 dark:text-slate-600">{t('No notifications to show')}</p>
          </div>
        )}
        {!loading && items.map((n) => (
          <div
            key={n.id}
            className={`group flex items-start gap-3 px-4 py-3 transition-colors hover:bg-slate-50 dark:hover:bg-white/[0.03] ${
              n.status === 'Unread' ? 'bg-sapphire/[0.03] dark:bg-sapphire/[0.05]' : ''
            }`}
          >
            {/* Unread dot */}
            <div className="mt-1.5 shrink-0">
              {n.status === 'Unread'
                ? <span className="block h-2 w-2 rounded-full bg-sapphire" />
                : <span className="block h-2 w-2 rounded-full bg-transparent" />
              }
            </div>
            <div className="min-w-0 flex-1">
              <p className={`text-xs font-semibold ${n.status === 'Unread' ? 'text-slate-900 dark:text-white' : 'text-slate-600 dark:text-slate-300'}`}>{n.title}</p>
              <p className="mt-0.5 text-xs leading-relaxed text-slate-500 dark:text-slate-400">{n.message}</p>
              <p className="mt-1 text-[10px] text-slate-300 dark:text-slate-600">{timeAgo(n.createdAtUtc)}</p>
            </div>
            {/* Actions */}
            <div className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100">
              {n.status === 'Unread' && (
                <button type="button" onClick={() => markRead(n.id)} title="Mark as read" aria-label="Mark as read" className="grid h-6 w-6 place-items-center rounded-md text-slate-400 hover:bg-slate-100 hover:text-sapphire dark:hover:bg-white/10 dark:hover:text-cyanAccent">
                  <CheckCheck className="h-3.5 w-3.5" />
                </button>
              )}
              <button type="button" onClick={() => dismiss(n.id)} title="Dismiss" aria-label="Dismiss notification" className="grid h-6 w-6 place-items-center rounded-md text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </div>
          </div>
        ))}
      </div>

      {/* Footer */}
      {items.length > 0 && (
        <div className="border-t border-slate-100 px-4 py-2 dark:border-white/[0.07]">
          <p className="text-center text-[10px] text-slate-400 dark:text-slate-600">
            {items.length} notification{items.length !== 1 ? 's' : ''} · Workflow events only
          </p>
        </div>
      )}
    </div>
  );
}

export function TopBar({ theme, onToggleTheme, onOpenSidebar, onOpenSearch, onAskKynexOne }: TopBarProps) {
  const ThemeIcon = theme === 'dark' ? Sun : Moon;
  const { user, logout } = useAuth();
  const { t } = useLocale();
  const router = useRouter();
  const [notifOpen, setNotifOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [unreadCount, setUnreadCount] = useState(0);
  const bellRef = useRef<HTMLDivElement>(null);
  const userRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!userMenuOpen) return;
    const handler = (e: MouseEvent) => {
      if (userRef.current && !userRef.current.contains(e.target as Node)) setUserMenuOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [userMenuOpen]);

  // Initial load + poll every 60s
  useEffect(() => {
    const fetch = () => {
      notificationsApi.list()
        .then((items) => setUnreadCount(items.filter((n) => n.status === 'Unread').length))
        .catch(() => {});
    };
    fetch();
    const interval = setInterval(fetch, 60_000);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    if (!notifOpen) return;
    const handler = (e: MouseEvent) => {
      if (bellRef.current && !bellRef.current.contains(e.target as Node)) setNotifOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [notifOpen]);

  return (
    <header className="sticky top-0 z-20 flex h-[60px] items-center gap-3 border-b border-slate-200/70 bg-white/[0.88] px-4 backdrop-blur-xl dark:border-white/[0.06] dark:bg-[#0c1120]/[0.88] sm:px-5">
      <button
        type="button"
        aria-label="Open navigation"
        onClick={onOpenSidebar}
        className="grid h-8 w-8 place-items-center rounded-lg border border-slate-200/80 bg-white/60 text-slate-600 backdrop-blur-sm transition hover:bg-white/90 hover:border-slate-300 lg:hidden dark:border-white/[0.08] dark:bg-white/[0.05] dark:text-slate-300 dark:hover:bg-white/[0.10]"
      >
        <Menu className="h-4 w-4" />
      </button>

      <button
        type="button"
        aria-label="Open command search"
        onClick={onOpenSearch}
        className="flex h-8 max-w-md flex-1 items-center gap-2.5 rounded-lg border border-slate-200 bg-slate-50/80 px-3 text-left text-sm transition hover:border-slate-300 hover:bg-white dark:border-white/[0.08] dark:bg-white/[0.04] dark:hover:bg-white/[0.07]"
      >
        <svg className="h-3.5 w-3.5 shrink-0 text-slate-400" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <circle cx="6.5" cy="6.5" r="4.5" stroke="currentColor" strokeWidth="1.5" />
          <path d="M10.5 10.5L13.5 13.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        <span className="flex-1 truncate text-slate-400 dark:text-slate-500">{t('Search employees, payroll, approvals…')}</span>
        <kbd className="hidden rounded-md border border-slate-200 bg-white px-1.5 py-0.5 font-mono text-[10px] text-slate-400 sm:inline dark:border-white/10 dark:bg-white/[0.06] dark:text-slate-500">⌘K</kbd>
      </button>

      <LanguageSwitcher />

      <div className="ml-auto flex shrink-0 items-center gap-1.5">
        <button
          type="button"
          aria-label="Open assistant"
          onClick={onAskKynexOne}
          className="hidden h-8 items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 text-xs font-semibold text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 sm:flex dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.07]"
        >
          <MessageSquareText className="h-3.5 w-3.5" />
          {t('Assistant')}
        </button>

        <button
          type="button"
          aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
          onClick={onToggleTheme}
          className="grid h-8 w-8 place-items-center rounded-lg border border-slate-200/80 bg-white/60 text-slate-500 backdrop-blur-sm transition hover:border-slate-300 hover:bg-white/90 hover:text-slate-900 dark:border-white/[0.08] dark:bg-white/[0.05] dark:text-slate-400 dark:hover:bg-white/[0.10] dark:hover:text-white"
        >
          <ThemeIcon className="h-4 w-4" />
        </button>

        {/* Notification bell with dropdown */}
        <div ref={bellRef} className="relative">
          <button
            type="button"
            aria-label="Notifications"
            onClick={() => setNotifOpen((o) => !o)}
            className="relative grid h-8 w-8 place-items-center rounded-lg border border-slate-200/80 bg-white/60 text-slate-500 backdrop-blur-sm transition hover:border-slate-300 hover:bg-white/90 hover:text-slate-900 dark:border-white/[0.08] dark:bg-white/[0.05] dark:text-slate-400 dark:hover:bg-white/[0.10] dark:hover:text-white"
          >
            <Bell className="h-4 w-4" />
            {unreadCount > 0 && (
              <span className="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-rose-500 ring-2 ring-white dark:ring-[#0D1221]" />
            )}
          </button>
          {notifOpen && (
            <NotificationPanel
              onClose={() => setNotifOpen(false)}
              onCountChange={setUnreadCount}
            />
          )}
        </div>

        <div ref={userRef} className="relative">
          <button
            type="button"
            aria-label="Open user menu"
            onClick={() => setUserMenuOpen((o) => !o)}
            className="hidden h-8 items-center gap-2 rounded-lg border border-slate-200/80 bg-white/60 pl-1 pr-3 backdrop-blur-sm transition hover:border-slate-300 hover:bg-white/90 md:flex dark:border-white/[0.08] dark:bg-white/[0.05] dark:hover:bg-white/[0.09]"
          >
            <Avatar name={user?.fullName ?? 'User'} size="xs" />
            <div className="text-left">
              <p className="text-xs font-semibold leading-tight text-slate-900 dark:text-white">{user?.fullName ?? 'User'}</p>
              <p className="text-[10px] leading-tight text-slate-500 dark:text-slate-400">{user?.roles[0] ?? 'Member'}</p>
            </div>
          </button>
          {userMenuOpen && (
            <div className="animate-fade-in absolute right-0 top-full z-50 mt-2 w-52 overflow-hidden rounded-xl border border-slate-200/80 bg-white/[0.92] shadow-2xl backdrop-blur-xl dark:border-white/[0.08] dark:bg-[#0c1120]/[0.92]">
              <div className="border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
                <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{user?.fullName ?? 'User'}</p>
                <p className="truncate text-[11px] text-slate-500 dark:text-slate-400">{user?.email ?? ''}</p>
              </div>
              <button
                type="button"
                onClick={() => { setUserMenuOpen(false); router.push('/ess'); }}
                className="flex w-full items-center gap-2.5 px-4 py-2.5 text-left text-sm text-slate-700 transition hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-white/[0.04]"
              >
                <UserCircle2 className="h-4 w-4 text-slate-400" /> {t('My Profile')}
              </button>
              <button
                type="button"
                onClick={async () => { setUserMenuOpen(false); await logout(); router.replace('/login'); }}
                className="flex w-full items-center gap-2.5 border-t border-slate-100 px-4 py-2.5 text-left text-sm text-rose-600 transition hover:bg-rose-50 dark:border-white/[0.07] dark:text-rose-400 dark:hover:bg-rose-500/10"
              >
                <LogOut className="h-4 w-4" /> {t('Sign out')}
              </button>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}
