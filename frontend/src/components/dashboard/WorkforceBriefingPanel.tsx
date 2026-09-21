'use client';

/**
 * Workforce Briefing — the first panel on the dashboard.
 *
 * WHAT THIS SHOWS, AND WHY IT IS LABELLED THE WAY IT IS
 * ------------------------------------------------------
 * The findings come from AiInsightEngine (Infrastructure/AI/AiInsightEngine.cs), a hosted
 * worker that runs hourly, per tenant. It is 100% deterministic: seven threshold checks over
 * live tenant data (payroll variance, missing salary setup, overtime anomaly, leave
 * accumulation, turnover, visa expiry, inactive salary structures). It resolves ILlmClient
 * and never calls it. Every row it writes is stamped GeneratedBy = "System", not "AI".
 *
 * So this panel says "rules-based" and it means it. It must NOT be labelled "AI-powered":
 * no model produces any text on this surface. Selling the determinism is the honest story —
 * these are statutory/payroll numbers where a hallucination would be a defect, not a feature.
 *
 * The conversational assistant (POST /api/ai/query) is a SEPARATE capability that does call a
 * model when one is configured. As of this writing production runs AI_PROVIDER=none, so
 * GET /api/ai/status returns { enabled: false }. The footer states that plainly rather than
 * implying a chat box that cannot answer. It never spins forever and never fabricates.
 *
 * Degradation contract (the codebase doctrine: a surface that cannot do what it claims must
 * say so, never pretend):
 *   - AI Assistant module switched off by the tenant  -> render nothing at all.
 *   - Caller lacks ai.insights_view                   -> render nothing (and never call the
 *     endpoint, which would 403 and fire the global access-denied toast).
 *   - Insights fetch fails                            -> one plain line, no spinner, no retry loop.
 *   - Zero findings                                   -> a positive, specific all-clear.
 *   - Provider status unknown                         -> footer stays silent (tri-state), it
 *     does not guess in either direction.
 *
 * Performance: this component owns its own fetches and is never awaited by DashboardPage's
 * loader, so a slow API here cannot delay any other card rendering.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import {
  AlertTriangle,
  ArrowRight,
  CheckCircle2,
  Info,
  MessageSquareText,
  ShieldAlert,
  SlidersHorizontal,
} from 'lucide-react';
import { aiAssistantApi } from '../../api/intelligence';
import type { AIInsight, AIProviderStatus } from '../../api/intelligence';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useAuth } from '../../contexts/AuthContext';

/** How many findings to show inline before deferring to the full list. */
const PREVIEW_COUNT = 3;
/** One page is plenty: the engine de-duplicates by insight type within a 24h window. */
const FETCH_PAGE_SIZE = 50;

type Severity = 'Critical' | 'Warning' | 'Info';

const SEVERITY_ORDER: Record<Severity, number> = { Critical: 0, Warning: 1, Info: 2 };

const SEVERITY_STYLE: Record<Severity, { icon: typeof ShieldAlert; dot: string; text: string; chip: string }> = {
  Critical: {
    icon: ShieldAlert,
    dot: 'bg-rose-500/10',
    text: 'text-rose-600 dark:text-rose-400',
    chip: 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/25 dark:bg-rose-500/10 dark:text-rose-300',
  },
  Warning: {
    icon: AlertTriangle,
    dot: 'bg-amber-500/10',
    text: 'text-amber-600 dark:text-amber-400',
    chip: 'border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/25 dark:bg-amber-500/10 dark:text-amber-300',
  },
  Info: {
    icon: Info,
    dot: 'bg-blue-500/10',
    text: 'text-blue-600 dark:text-blue-400',
    chip: 'border-blue-200 bg-blue-50 text-blue-700 dark:border-blue-500/25 dark:bg-blue-500/10 dark:text-blue-300',
  },
};

function normaliseSeverity(value: string | undefined): Severity {
  if (value === 'Critical' || value === 'Warning' || value === 'Info') return value;
  const lower = (value ?? '').toLowerCase();
  if (lower === 'critical') return 'Critical';
  if (lower === 'warning') return 'Warning';
  return 'Info';
}

function Row({ insight }: { insight: AIInsight }) {
  const severity = normaliseSeverity(insight.severity);
  const style = SEVERITY_STYLE[severity];
  const Icon = style.icon;
  return (
    <li className="flex items-start gap-3 rounded-xl border border-slate-100 p-3 dark:border-white/[0.06]">
      <span className={`mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg ${style.dot}`}>
        <Icon className={`h-4 w-4 ${style.text}`} aria-hidden />
      </span>
      <div className="min-w-0">
        <p className="text-[13px] font-semibold leading-snug text-slate-900 dark:text-white">
          {insight.title}
        </p>
        <p className="mt-0.5 text-[12px] leading-relaxed text-slate-500 dark:text-slate-400">
          {insight.summary}
        </p>
        <p className="mt-1 text-[10px] font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">
          {severity} · {insight.module}
        </p>
      </div>
    </li>
  );
}

export function WorkforceBriefingPanel() {
  const router = useRouter();
  const { isFeatureEnabled } = useFeatureFlags();
  const { hasPermission } = useAuth();

  // Fail-closed while the module list loads, so the panel never flashes in for a tenant
  // that has the assistant switched off.
  const moduleEnabled = isFeatureEnabled('ai_assistant');
  // Calling /api/ai/insights without this permission returns 403, which the shared axios
  // client turns into a global "access denied" toast. Check first; do not provoke it.
  const maySeeInsights = hasPermission('ai.insights_view');
  const active = moduleEnabled && maySeeInsights;

  const [insights, setInsights] = useState<AIInsight[] | null>(null);
  const [total, setTotal] = useState(0);
  const [failed, setFailed] = useState(false);
  // Tri-state, matching AIAssistantPage: null = not yet known, do not claim either way.
  const [providerStatus, setProviderStatus] = useState<AIProviderStatus | null>(null);
  const loadedRef = useRef(false);

  const load = useCallback(async () => {
    if (loadedRef.current) return;
    loadedRef.current = true;

    // Deliberately not awaited together with anything the rest of the dashboard needs.
    await Promise.allSettled([
      aiAssistantApi
        .listInsights({ acknowledged: false, pageSize: FETCH_PAGE_SIZE })
        .then((r) => {
          setInsights(r.items ?? []);
          setTotal(r.total ?? r.items?.length ?? 0);
        })
        .catch(() => {
          setInsights([]);
          setFailed(true);
        }),
      aiAssistantApi
        .status()
        .then(setProviderStatus)
        // A failed status probe must not be read as "available".
        .catch(() => setProviderStatus({ enabled: false, provider: 'unknown' })),
    ]);
  }, []);

  useEffect(() => {
    if (!active) return;
    load();
  }, [active, load]);

  if (!active) return null;

  const loading = insights === null;
  const sorted = (insights ?? [])
    .slice()
    .sort((a, b) => SEVERITY_ORDER[normaliseSeverity(a.severity)] - SEVERITY_ORDER[normaliseSeverity(b.severity)]);
  const counts = sorted.reduce<Record<Severity, number>>(
    (acc, item) => {
      acc[normaliseSeverity(item.severity)] += 1;
      return acc;
    },
    { Critical: 0, Warning: 0, Info: 0 },
  );
  const truncated = total > sorted.length;

  return (
    <section
      aria-label="Workforce Briefing"
      data-testid="workforce-briefing"
      className="rounded-2xl border border-slate-200/80 bg-white text-start dark:border-white/[0.07] dark:bg-[#0e1729]/80"
    >
      {/* ── Header ─────────────────────────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-slate-100 px-4 py-3 dark:border-white/[0.06] sm:px-5">
        <div className="flex min-w-0 items-center gap-2.5">
          <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-sapphire/10">
            <SlidersHorizontal className="h-4 w-4 text-sapphire" aria-hidden />
          </span>
          <div className="min-w-0">
            <h2 className="truncate text-[13px] font-bold text-slate-900 dark:text-white">
              Workforce Briefing
            </h2>
            {/* Wraps at phone width rather than truncating — the sentence is the honesty
                claim, so it must stay readable in full. */}
            <p className="text-[11px] text-slate-500 dark:text-slate-400 sm:truncate">
              Seven automated checks across payroll, leave, documents and headcount
            </p>
          </div>
        </div>
        <span className="shrink-0 rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-[10px] font-bold uppercase tracking-wide text-slate-600 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300">
          Rules-based · hourly
        </span>
      </div>

      {/* ── Findings ───────────────────────────────────────────────────────── */}
      <div className="p-4 sm:px-5">
        {loading && (
          <div aria-hidden className="space-y-2">
            <div className="h-6 w-56 animate-pulse rounded-full bg-slate-100 dark:bg-white/[0.06]" />
            <div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.06]" />
            <div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.06]" />
          </div>
        )}
        {loading && <span className="sr-only">Loading workforce briefing…</span>}

        {!loading && failed && (
          <p className="text-[12px] leading-relaxed text-slate-500 dark:text-slate-400">
            The briefing could not be loaded, so nothing is being shown rather than something
            out of date. Your other dashboard metrics are unaffected.
          </p>
        )}

        {!loading && !failed && sorted.length === 0 && (
          <div className="flex items-start gap-2.5 rounded-xl bg-emerald-50 px-3 py-2.5 dark:bg-emerald-500/10">
            <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden />
            <p className="text-[12px] leading-relaxed text-emerald-800 dark:text-emerald-300">
              <span className="font-semibold">Nothing flagged.</span>{' '}
              The last run found no payroll variance, missing salary setup, overtime anomaly,
              leave build-up, turnover spike, expiring visa or inactive salary structure.
            </p>
          </div>
        )}

        {!loading && !failed && sorted.length > 0 && (
          <>
            <div className="mb-3 flex flex-wrap items-center gap-1.5">
              {(['Critical', 'Warning', 'Info'] as const)
                .filter((severity) => counts[severity] > 0)
                .map((severity) => (
                  <span
                    key={severity}
                    className={`rounded-full border px-2.5 py-1 text-[11px] font-semibold ${SEVERITY_STYLE[severity].chip}`}
                  >
                    {counts[severity]} {severity.toLowerCase()}
                  </span>
                ))}
            </div>

            <ul className="space-y-2">
              {sorted.slice(0, PREVIEW_COUNT).map((insight) => (
                <Row key={insight.id ?? `${insight.insightType}-${insight.title}`} insight={insight} />
              ))}
            </ul>

            {(sorted.length > PREVIEW_COUNT || truncated) && (
              <button
                type="button"
                onClick={() => router.push('/ai-assistant')}
                className="mt-3 flex w-full items-center justify-center gap-1.5 rounded-xl border border-slate-200 bg-slate-50 py-2 text-xs font-semibold text-slate-700 transition hover:border-sapphire/30 hover:bg-sapphire/[0.04] hover:text-sapphire dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300 dark:hover:text-blue-400"
              >
                View all {truncated ? total : sorted.length} open findings
                <ArrowRight className="h-3.5 w-3.5" aria-hidden />
              </button>
            )}
          </>
        )}
      </div>

      {/* ── Conversational assistant: separate capability, stated honestly ──── */}
      {providerStatus !== null && (
        <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 border-t border-slate-100 px-4 py-2.5 dark:border-white/[0.06] sm:px-5">
          {providerStatus.enabled ? (
            <>
              <p className="min-w-0 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
                <span className="font-semibold text-slate-700 dark:text-slate-200">
                  Conversational assistant is available.
                </span>{' '}
                Answers are model-generated and advisory — check them before acting.
              </p>
              <button
                type="button"
                onClick={() => router.push('/ai-assistant')}
                className="flex shrink-0 items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-[11px] font-semibold text-white transition hover:opacity-90"
              >
                <MessageSquareText className="h-3.5 w-3.5" aria-hidden />
                Ask a question
              </button>
            </>
          ) : (
            <p className="min-w-0 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
              <span className="font-semibold text-slate-700 dark:text-slate-200">
                Conversational assistant is not enabled on this deployment.
              </span>{' '}
              The checks above are unaffected — they run without a language model.
            </p>
          )}
        </div>
      )}
    </section>
  );
}
