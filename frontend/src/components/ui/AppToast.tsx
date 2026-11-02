'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle, Info, X, XCircle } from 'lucide-react';
import { translate, type LocaleCode } from '@/src/i18n/translations';

type ToastKind = 'success' | 'error' | 'warning' | 'info';

interface Toast {
  id: string;
  kind: ToastKind;
  title?: string;
  message: string;
  duration?: number;
}

interface AppToastCtx {
  showToast: (kind: ToastKind, message: string, title?: string, duration?: number) => void;
  success: (msg: string, title?: string) => void;
  error: (msg: string, title?: string) => void;
  warn: (msg: string, title?: string) => void;
  info: (msg: string, title?: string) => void;
}

const Ctx = createContext<AppToastCtx>({
  showToast: () => {},
  success: () => {},
  error: () => {},
  warn: () => {},
  info: () => {},
});

export function useAppToast() {
  return useContext(Ctx);
}

const ICONS: Record<ToastKind, React.ElementType> = {
  success: CheckCircle,
  error: XCircle,
  warning: AlertTriangle,
  info: Info,
};

const KIND_CLS: Record<ToastKind, { bar: string; icon: string; surface: string }> = {
  success: { bar: 'bg-success', icon: 'text-success-fg', surface: 'border-line' },
  error:   { bar: 'bg-danger',  icon: 'text-danger-fg',  surface: 'border-line' },
  warning: { bar: 'bg-warning', icon: 'text-warning-fg', surface: 'border-line' },
  info:    { bar: 'bg-primary', icon: 'text-primary-fg', surface: 'border-line' },
};

// Errors need time to be read and acted on: they linger longer, and every toast pauses while it
// is hovered or holds keyboard focus (WCAG 2.2.1 Timing Adjustable).
const DEFAULT_MS: Record<ToastKind, number> = { success: 5000, info: 5000, warning: 8000, error: 10000 };

/** The toast host sits above the locale provider, so the label follows the document language. */
function documentLocale(): LocaleCode {
  const lang = typeof document !== 'undefined' ? document.documentElement.lang : 'en';
  return lang === 'ar' || lang === 'fr' || lang === 'es' ? lang : 'en';
}
const dismissLabel = () => translate(documentLocale(), 'Dismiss');

function ToastItem({ t, onClose }: { t: Toast; onClose: (id: string) => void }) {
  const Icon    = ICONS[t.kind];
  const cls     = KIND_CLS[t.kind];
  const duration = t.duration ?? DEFAULT_MS[t.kind];
  const [progress, setProgress] = useState(100);
  const [paused, setPaused] = useState(false);
  const remaining = useRef(duration);
  const last = useRef(0);

  useEffect(() => {
    if (paused) return;
    last.current = Date.now();
    const timer = setInterval(() => {
      const now = Date.now();
      remaining.current -= now - last.current;
      last.current = now;
      setProgress(Math.max(0, (remaining.current / duration) * 100));
      if (remaining.current <= 0) {
        clearInterval(timer);
        onClose(t.id);
      }
    }, 50);
    return () => clearInterval(timer);
  }, [paused, duration, t.id, onClose]);

  // Errors interrupt (alert); everything else is polite status. A non-error toast must not
  // pre-empt what a screen reader is already announcing.
  const urgent = t.kind === 'error';

  return (
    // Hover/focus only pauses the auto-dismiss timer; the toast has no action of its own, so the role stays a live region.
    // eslint-disable-next-line jsx-a11y/no-static-element-interactions
    <div
      role={urgent ? 'alert' : 'status'}
      aria-live={urgent ? 'assertive' : 'polite'}
      aria-atomic="true"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
      className={`kx-toast relative flex min-w-[300px] max-w-[420px] items-start gap-3 overflow-hidden rounded-xl border bg-surface-overlay px-4 py-3 shadow-e2 ${cls.surface}`}
    >
      {/* remaining-time bar */}
      <div className={`absolute bottom-0 start-0 h-0.5 ${cls.bar}`} style={{ width: `${progress}%` }} aria-hidden="true" />
      <Icon className={`mt-0.5 h-5 w-5 shrink-0 ${cls.icon}`} aria-hidden="true" />
      <div className="min-w-0 flex-1">
        {t.title && <p className="text-body font-semibold leading-tight text-fg">{t.title}</p>}
        <p className={`text-body leading-snug text-fg-muted ${t.title ? 'mt-0.5' : ''}`}>{t.message}</p>
      </div>
      <button
        type="button"
        onClick={() => onClose(t.id)}
        aria-label={dismissLabel()}
        className="mt-0.5 shrink-0 rounded text-fg-subtle transition-colors hover:text-fg"
      >
        <X className="h-4 w-4" aria-hidden="true" />
      </button>
    </div>
  );
}

export function AppToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts(ts => ts.filter(t => t.id !== id));
  }, []);

  const showToast = useCallback((kind: ToastKind, message: string, title?: string, duration?: number) => {
    const id = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    setToasts(ts => [...ts.slice(-4), { id, kind, message, title, duration }]);
  }, []);

  // Listen for access-denied events dispatched from outside React (e.g., axios interceptor)
  useEffect(() => {
    const handler = (e: Event) => {
      const msg = (e as CustomEvent<string>).detail ?? 'You do not have permission to perform this action. Contact your administrator.';
      showToast('error', msg, 'Access Denied', 6000);
    };
    window.addEventListener('zayra:access-denied', handler);
    return () => window.removeEventListener('zayra:access-denied', handler);
  }, [showToast]);

  // Generic action-failure toast, dispatched by notifyApiError() from action handlers
  // so a failed write (400/409/422/500) never fails silently — the user always sees why.
  useEffect(() => {
    const handler = (e: Event) => {
      const msg = (e as CustomEvent<string>).detail ?? 'Something went wrong. Please try again.';
      showToast('error', msg, 'Action failed', 6000);
    };
    window.addEventListener('zayra:error', handler);
    return () => window.removeEventListener('zayra:error', handler);
  }, [showToast]);

  const success = useCallback((msg: string, title?: string) => showToast('success', msg, title), [showToast]);
  const error = useCallback((msg: string, title?: string) => showToast('error', msg, title), [showToast]);
  const warn = useCallback((msg: string, title?: string) => showToast('warning', msg, title), [showToast]);
  const info = useCallback((msg: string, title?: string) => showToast('info', msg, title), [showToast]);

  // Keep the context value referentially stable. PermissionGate depends on this object while
  // reporting access denial; recreating it after each toast state update retriggered the gate,
  // posted another toast, and could recurse into React's "Maximum update depth exceeded" boundary.
  const ctx = useMemo<AppToastCtx>(() => ({
    showToast,
    success,
    error,
    warn,
    info,
  }), [showToast, success, error, warn, info]);

  return (
    <Ctx.Provider value={ctx}>
      {children}
      <div role="region" suppressHydrationWarning aria-label={translate(documentLocale(), 'Notifications')} className="pointer-events-none fixed bottom-[calc(5.5rem+env(safe-area-inset-bottom))] end-5 z-[9999] flex flex-col items-end gap-2 lg:bottom-5">
        {toasts.map(t => (
          <div key={t.id} className="pointer-events-auto">
            <ToastItem t={t} onClose={dismiss} />
          </div>
        ))}
      </div>
      <style>{`
        @keyframes kx-toast-in {
          from { opacity: 0; transform: translateY(8px); }
          to   { opacity: 1; transform: translateY(0); }
        }
          .kx-toast { animation: kx-toast-in 0.22s ease-out; }
        @media (prefers-reduced-motion: reduce) { .kx-toast { animation: none; } }
      `}</style>
    </Ctx.Provider>
  );
}
