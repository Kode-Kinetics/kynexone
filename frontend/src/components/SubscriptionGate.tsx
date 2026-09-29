'use client';

import { Lock, ArrowUpRight } from 'lucide-react';

interface SubscriptionGateProps {
  featureName: string;
  requiredPlan?: string;
  reason?: string;
  children: React.ReactNode;
  isBlocked: boolean;
}

/**
 * Wraps content with a subscription upgrade wall when the feature is blocked.
 * Usage: wrap any module-gated section — when isBlocked=true shows upgrade UI instead of children.
 */
export function SubscriptionGate({ featureName, requiredPlan, reason, children, isBlocked }: SubscriptionGateProps) {
  if (!isBlocked) return <>{children}</>;

  return (
    <div className="relative rounded-2xl border border-white/[0.07] bg-[#161b22] overflow-hidden">
      {/* Blurred preview */}
      <div className="pointer-events-none select-none blur-sm opacity-30 p-6">
        {children}
      </div>

      {/* Overlay */}
      <div className="absolute inset-0 flex items-center justify-center bg-[#0d1117]/70 backdrop-blur-sm">
        <div className="max-w-sm mx-auto text-center px-6 py-8 space-y-4">
          <div className="h-14 w-14 mx-auto rounded-full bg-sapphire/10 border border-sapphire/20 flex items-center justify-center">
            <Lock className="h-6 w-6 text-sapphire" />
          </div>
          <h3 className="text-lg font-semibold text-white">{featureName} is not included in your plan</h3>
          {reason && <p className="text-sm text-slate-400">{reason}</p>}
          {requiredPlan && (
            <p className="text-xs text-slate-500">
              Available from <span className="text-white font-medium">{requiredPlan}</span> plan and above.
            </p>
          )}
          <a
            href="mailto:sales@kynexone.com?subject=Upgrade Inquiry"
            className="inline-flex items-center gap-1.5 px-4 py-2 rounded-lg bg-sapphire hover:bg-sapphire/90 text-white text-sm font-medium transition-colors"
          >
            Contact Sales to Upgrade
            <ArrowUpRight className="h-3.5 w-3.5" />
          </a>
          <p className="text-xs text-slate-600">Or contact your platform administrator.</p>
        </div>
      </div>
    </div>
  );
}

/**
 * Inline upgrade prompt — shown inline rather than as an overlay.
 * Use when you can't wrap content (e.g., empty state for a blocked feature).
 */
export function UpgradePrompt({ featureName, requiredPlan, compact = false }: {
  featureName: string;
  requiredPlan?: string;
  compact?: boolean;
}) {
  if (compact) {
    return (
      <div className="flex items-center gap-2 px-3 py-2 rounded-lg bg-sapphire/5 border border-sapphire/20 text-xs text-sapphire">
        <Lock className="h-3.5 w-3.5 shrink-0" />
        <span>{featureName} requires an upgraded plan.{requiredPlan ? ` (${requiredPlan}+)` : ''}</span>
        <a href="mailto:sales@kynexone.com?subject=Upgrade Inquiry" className="ms-auto underline hover:no-underline whitespace-nowrap">
          Upgrade →
        </a>
      </div>
    );
  }

  return (
    <div className="flex flex-col items-center text-center py-12 px-6 space-y-4">
      <div className="h-14 w-14 rounded-full bg-sapphire/10 border border-sapphire/20 flex items-center justify-center">
        <Lock className="h-6 w-6 text-sapphire" />
      </div>
      <h3 className="text-base font-semibold text-white">{featureName} is not enabled</h3>
      {requiredPlan && (
        <p className="text-sm text-slate-400">Upgrade to {requiredPlan} to access this feature.</p>
      )}
      <a
        href="mailto:sales@kynexone.com?subject=Upgrade Inquiry"
        className="inline-flex items-center gap-1.5 px-4 py-2 rounded-lg bg-sapphire hover:bg-sapphire/90 text-white text-sm font-medium transition-colors"
      >
        Contact Sales <ArrowUpRight className="h-3.5 w-3.5" />
      </a>
    </div>
  );
}

/**
 * Usage limit warning bar — shown when a tenant is approaching or at a subscription limit.
 */
export function UsageLimitBar({ label, current, max, warnAt = 80 }: {
  label: string;
  current: number;
  max: number | null;
  warnAt?: number;
}) {
  if (max === null || max === 0) {
    return (
      <div className="flex justify-between text-xs text-slate-400">
        <span>{label}</span>
        <span className="text-emerald-400">Unlimited</span>
      </div>
    );
  }

  const pct = Math.min(100, (current / max) * 100);
  const isAtLimit = current >= max;
  const isWarning = pct >= warnAt;

  return (
    <div className="space-y-1.5">
      <div className="flex justify-between text-xs">
        <span className="text-slate-400">{label}</span>
        <span className={isAtLimit ? 'text-rose-400 font-semibold' : isWarning ? 'text-amber-400' : 'text-slate-300'}>
          {current.toLocaleString()} / {max.toLocaleString()}
          {isAtLimit && ' — Limit reached'}
        </span>
      </div>
      <div className="h-1.5 bg-white/[0.06] rounded-full overflow-hidden">
        <div
          className={`h-full rounded-full transition-all ${isAtLimit ? 'bg-rose-500' : isWarning ? 'bg-amber-500' : 'bg-sapphire'}`}
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  );
}
