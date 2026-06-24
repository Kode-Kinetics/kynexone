'use client';

import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle, Info, X, XCircle } from 'lucide-react';

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

const KIND_CLS: Record<ToastKind, { bar: string; icon: string; border: string }> = {
  success: { bar: 'bg-emerald-500', icon: 'text-emerald-500', border: 'border-emerald-200 dark:border-emerald-800' },
  error:   { bar: 'bg-red-500',     icon: 'text-red-500',     border: 'border-red-200   dark:border-red-800'   },
  warning: { bar: 'bg-amber-500',   icon: 'text-amber-500',   border: 'border-amber-200 dark:border-amber-800' },
  info:    { bar: 'bg-blue-500',    icon: 'text-blue-500',    border: 'border-blue-200  dark:border-blue-800'  },
};

function ToastItem({ t, onClose }: { t: Toast; onClose: (id: string) => void }) {
  const Icon    = ICONS[t.kind];
  const cls     = KIND_CLS[t.kind];
  const duration = t.duration ?? 5000;
  const [progress, setProgress] = useState(100);
  const start = useRef(Date.now());

  useEffect(() => {
    const timer = setInterval(() => {
      const elapsed = Date.now() - start.current;
      const pct = Math.max(0, 100 - (elapsed / duration) * 100);
      setProgress(pct);
      if (elapsed >= duration) {
        clearInterval(timer);
        onClose(t.id);
      }
    }, 30);
    return () => clearInterval(timer);
  }, [duration, t.id, onClose]);

  return (
    <div
      role="alert"
      className={`relative flex items-start gap-3 rounded-xl border bg-white dark:bg-gray-900 px-4 py-3.5 shadow-lg shadow-black/10 dark:shadow-black/40 min-w-[300px] max-w-[420px] overflow-hidden ${cls.border}`}
      style={{ animation: 'slideInRight 0.22s ease-out' }}
    >
      {/* progress bar */}
      <div className={`absolute bottom-0 left-0 h-0.5 transition-[width] ${cls.bar}`} style={{ width: `${progress}%` }} />
      <Icon className={`h-5 w-5 shrink-0 mt-0.5 ${cls.icon}`} />
      <div className="flex-1 min-w-0">
        {t.title && <p className="text-sm font-semibold text-gray-900 dark:text-gray-100 leading-tight">{t.title}</p>}
        <p className={`text-sm text-gray-600 dark:text-gray-400 leading-snug ${t.title ? 'mt-0.5' : ''}`}>{t.message}</p>
      </div>
      <button
        type="button"
        onClick={() => onClose(t.id)}
        aria-label="Dismiss"
        className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 transition-colors shrink-0 mt-0.5"
      >
        <X className="h-4 w-4" />
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

  const ctx: AppToastCtx = {
    showToast,
    success: (msg, title) => showToast('success', msg, title),
    error:   (msg, title) => showToast('error',   msg, title),
    warn:    (msg, title) => showToast('warning', msg, title),
    info:    (msg, title) => showToast('info',    msg, title),
  };

  return (
    <Ctx.Provider value={ctx}>
      {children}
      <div className="fixed bottom-5 right-5 z-[9999] flex flex-col gap-2 items-end pointer-events-none">
        {toasts.map(t => (
          <div key={t.id} className="pointer-events-auto">
            <ToastItem t={t} onClose={dismiss} />
          </div>
        ))}
      </div>
      <style>{`
        @keyframes slideInRight {
          from { opacity: 0; transform: translateX(24px); }
          to   { opacity: 1; transform: translateX(0);    }
        }
      `}</style>
    </Ctx.Provider>
  );
}
