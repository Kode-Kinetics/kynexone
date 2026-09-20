'use client';

import { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { CheckCircle, XCircle, AlertTriangle, X, Info } from 'lucide-react';

type ToastKind = 'success' | 'error' | 'warning' | 'info';

interface Toast {
  id: string;
  kind: ToastKind;
  message: string;
  duration?: number;
}

interface ToastCtx {
  toast: (kind: ToastKind, message: string, duration?: number) => void;
  success: (msg: string) => void;
  error: (msg: string) => void;
  warn: (msg: string) => void;
  info: (msg: string) => void;
}

const Ctx = createContext<ToastCtx>({
  toast: () => {},
  success: () => {},
  error: () => {},
  warn: () => {},
  info: () => {},
});

export function useToast() {
  return useContext(Ctx);
}

const ICONS: Record<ToastKind, React.ElementType> = {
  success: CheckCircle,
  error: XCircle,
  warning: AlertTriangle,
  info: Info,
};

const CLS: Record<ToastKind, { wrap: string; icon: string }> = {
  success: { wrap: 'border-emerald-500/25 bg-[#0d1117]', icon: 'text-emerald-400' },
  error:   { wrap: 'border-rose-500/30 bg-[#0d1117]',    icon: 'text-rose-400' },
  warning: { wrap: 'border-amber-500/25 bg-[#0d1117]',   icon: 'text-amber-400' },
  info:    { wrap: 'border-blue-500/20 bg-[#0d1117]',    icon: 'text-blue-400' },
};

function ToastItem({ t, onClose }: { t: Toast; onClose: (id: string) => void }) {
  const Icon = ICONS[t.kind];
  const cls  = CLS[t.kind];
  const barCls: Record<ToastKind, string> = {
    success: 'bg-emerald-500',
    error:   'bg-rose-500',
    warning: 'bg-amber-500',
    info:    'bg-blue-500',
  };
  const duration = t.duration ?? 4000;
  const elapsed = useRef(0);
  const start = useRef(Date.now());
  const [progress, setProgress] = useState(100);

  useEffect(() => {
    const timer = setInterval(() => {
      elapsed.current = Date.now() - start.current;
      setProgress(Math.max(0, 100 - (elapsed.current / duration) * 100));
      if (elapsed.current >= duration) {
        clearInterval(timer);
        onClose(t.id);
      }
    }, 30);
    return () => clearInterval(timer);
  }, [duration, t.id, onClose]);

  return (
    <div className={`relative flex items-start gap-3 rounded-xl border px-4 py-3 shadow-2xl shadow-black/60 backdrop-blur-sm min-w-[280px] max-w-[380px] ${cls.wrap} animate-slide-in-right overflow-hidden`}>
      <div className={`absolute bottom-0 start-0 h-0.5 transition-[width] ${barCls[t.kind]}`} style={{ width: `${progress}%` }} />
      <Icon className={`h-4 w-4 shrink-0 mt-0.5 ${cls.icon}`} />
      <p className="flex-1 text-sm text-white leading-relaxed">{t.message}</p>
      <button type="button" onClick={() => onClose(t.id)} aria-label="Dismiss notification"
        className="text-slate-600 hover:text-slate-300 transition-colors shrink-0 mt-0.5">
        <X className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

export function PlatformToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts(ts => ts.filter(t => t.id !== id));
  }, []);

  const toast = useCallback((kind: ToastKind, message: string, duration?: number) => {
    const id = `${Date.now()}-${Math.random()}`;
    setToasts(ts => [...ts.slice(-4), { id, kind, message, duration }]);
  }, []);

  const ctx: ToastCtx = {
    toast,
    success: (msg) => toast('success', msg),
    error:   (msg) => toast('error', msg),
    warn:    (msg) => toast('warning', msg),
    info:    (msg) => toast('info', msg),
  };

  return (
    <Ctx.Provider value={ctx}>
      {children}
      <div className="fixed bottom-5 end-5 z-[9999] flex flex-col gap-2 items-end">
        {toasts.map(t => (
          <ToastItem key={t.id} t={t} onClose={dismiss} />
        ))}
      </div>
    </Ctx.Provider>
  );
}
