'use client';

/**
 * Assistant drawer — glass, because it is a temporary surface over the page.
 *   desktop / tablet: a right-side sheet (logical end edge, so it opens from the left in RTL)
 *   phone:            a bottom sheet with a drag handle and two detents (half / full)
 *
 * Two capabilities, labelled for what they are:
 *   1. Rules check — deterministic findings from the hourly engine. No model is involved,
 *      and each finding links to the records behind it.
 *   2. Conversational assistant — model-generated when a provider is configured. Every
 *      answer is marked advisory and offers a drill-down; nothing here takes an action.
 * When the provider is off, the drawer says so plainly instead of showing a dead chat box.
 */

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import Link from 'next/link';
import { ArrowRight, MessageSquareText, Send, X } from 'lucide-react';
import { aiAssistantApi } from '../api/intelligence';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useAssistantProvider, useWorkforceFindings } from '../hooks/useWorkforceFindings';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { useT } from '../hooks/useT';
import { dedupeInsights, insightRoute, timeAgo } from '../components/dashboard/dashboardModel';

interface Turn {
  role: 'user' | 'assistant';
  text: string;
  to?: string;
  blocked?: boolean;
}

const SUGGESTIONS = [
  'What is blocking payroll this month?',
  'Who is on leave today?',
  'How many approvals are pending?',
];

/** Best-effort link from an answer's intent to the records it talks about. */
function intentRoute(intent: string | undefined, question: string): string | undefined {
  const s = `${intent ?? ''} ${question}`.toLowerCase();
  if (/payroll|salary/.test(s)) return '/payroll';
  if (/leave|holiday|vacation/.test(s)) return '/leave';
  if (/approv/.test(s)) return '/approvals';
  if (/attendance|present|absent|late/.test(s)) return '/attendance';
  if (/iqama|visa|passport|complian|expir/.test(s)) return '/compliance';
  if (/headcount|employee|department|staff/.test(s)) return '/people';
  return undefined;
}

export function AssistantDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const t = useT();
  const titleId = useId();
  const phone = useMediaQuery('(max-width: 639.98px)');
  const reduceMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const { hasPermission } = useAuth();
  const { isFeatureEnabled } = useFeatureFlags();
  const moduleOn = isFeatureEnabled('ai_assistant');
  const mayQuery = moduleOn && hasPermission('ai.query');

  const findings = useWorkforceFindings(open);
  const provider = useAssistantProvider(open && mayQuery);

  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; }, [onClose]);

  // Mount/unmount with an exit transition.
  const [mounted, setMounted] = useState(open);
  const [shown, setShown] = useState(false);
  useEffect(() => {
    if (open) {
      setMounted(true);
      const id = requestAnimationFrame(() => requestAnimationFrame(() => setShown(true)));
      return () => cancelAnimationFrame(id);
    }
    setShown(false);
    const id = window.setTimeout(() => setMounted(false), reduceMotion ? 0 : 300);
    return () => window.clearTimeout(id);
  }, [open, reduceMotion]);

  // Focus management, Escape, focus trap, background scroll lock.
  useEffect(() => {
    if (!open) return;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    const focusId = requestAnimationFrame(() => closeRef.current?.focus());
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); onCloseRef.current(); return; }
      if (e.key !== 'Tab' || !panelRef.current) return;
      const f = Array.from(panelRef.current.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ));
      if (!f.length) return;
      const first = f[0];
      const last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      cancelAnimationFrame(focusId);
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
      previouslyFocused?.focus();
    };
  }, [open]);

  // Bottom-sheet detents and drag (phone only). The sheet's translateY follows the finger
  // directly; on release it settles to the nearest detent, or closes past the lower one.
  const [detent, setDetent] = useState<'half' | 'full'>('half');
  const [dragY, setDragY] = useState<number | null>(null);
  const dragStart = useRef<{ y: number; base: number } | null>(null);
  useEffect(() => { if (open) setDetent('half'); }, [open]);

  const sheetOffset = useCallback((d: 'half' | 'full') => {
    const h = typeof window !== 'undefined' ? window.innerHeight : 800;
    return d === 'full' ? h * 0.06 : h * 0.42;
  }, []);

  const onHandleDown = (e: React.PointerEvent) => {
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    dragStart.current = { y: e.clientY, base: sheetOffset(detent) };
    setDragY(sheetOffset(detent));
  };
  const onHandleMove = (e: React.PointerEvent) => {
    if (!dragStart.current) return;
    setDragY(Math.max(sheetOffset('full'), dragStart.current.base + (e.clientY - dragStart.current.y)));
  };
  const onHandleUp = () => {
    if (!dragStart.current || dragY == null) return;
    const h = window.innerHeight;
    dragStart.current = null;
    setDragY(null);
    if (dragY > h * 0.68) { onClose(); return; }
    setDetent(dragY < (sheetOffset('full') + sheetOffset('half')) / 2 ? 'full' : 'half');
  };

  // Chat.
  const [turns, setTurns] = useState<Turn[]>([]);
  const [draft, setDraft] = useState('');
  const [asking, setAsking] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  useEffect(() => { endRef.current?.scrollIntoView({ block: 'end', behavior: reduceMotion ? 'auto' : 'smooth' }); }, [turns, reduceMotion]);

  const ask = async (text: string) => {
    const q = text.trim();
    if (!q || asking) return;
    setDraft('');
    setTurns((prev) => [...prev, { role: 'user', text: q }]);
    setAsking(true);
    try {
      const res = await aiAssistantApi.query(q);
      setTurns((prev) => [...prev, {
        role: 'assistant',
        text: res.wasBlocked ? res.blockedReason || t('That question is outside what the assistant may answer.') : res.answer,
        blocked: res.wasBlocked,
        to: res.wasBlocked ? undefined : intentRoute(res.intent, q),
      }]);
    } catch {
      setTurns((prev) => [...prev, { role: 'assistant', text: t('The assistant could not answer just now. Nothing was changed.'), blocked: true }]);
    } finally {
      setAsking(false);
    }
  };

  if (!mounted || typeof document === 'undefined') return null;

  const open_ = findings.insights ? dedupeInsights(findings.insights).sort((a, b) => (a.severity === 'Critical' ? -1 : 0) - (b.severity === 'Critical' ? -1 : 0)) : null;
  const dur = reduceMotion ? 'duration-0' : 'duration-[280ms]';

  // Phone: the sheet is bottom-anchored and sized to the detent (so its lower edge — and the
  // question box — is always on screen); the finger drives the height directly while dragging.
  // Entry and exit are a transform. Desktop styling is class-driven.
  const sheetStyleRef = (el: HTMLDivElement | null) => {
    panelRef.current = el;
    if (!el) return;
    if (phone) {
      const top = dragY ?? sheetOffset(detent);
      el.style.height = `${Math.round(window.innerHeight - top)}px`;
      el.style.transform = shown ? 'translateY(0)' : 'translateY(100%)';
      el.style.transition = dragY != null || reduceMotion
        ? 'none'
        : 'transform var(--wg-dur-drawer) var(--wg-ease-drawer), height var(--wg-dur-drawer) var(--wg-ease-drawer)';
    } else {
      el.style.height = '';
      el.style.transform = '';
      el.style.transition = '';
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50" role="presentation">
      <div
        aria-hidden
        onClick={onClose}
        className={`absolute inset-0 bg-slate-950/30 transition-opacity ${dur} ${shown ? 'opacity-100' : 'opacity-0'}`}
      />
      <div
        ref={sheetStyleRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className={phone
          ? 'wg-glass absolute inset-x-0 bottom-0 flex flex-col rounded-t-[20px] border-t'
          : `wg-glass absolute inset-y-2 end-2 flex w-[min(420px,calc(100vw-16px))] flex-col rounded-[20px] border transition-[transform,opacity] ease-[cubic-bezier(0.32,0.72,0,1)] ${dur} ${
              shown ? 'translate-x-0 opacity-100' : 'translate-x-8 opacity-0 rtl:-translate-x-8'
            }`}
      >
        {phone && (
          <div
            className="flex h-6 shrink-0 cursor-grab touch-none items-center justify-center"
            onPointerDown={onHandleDown}
            onPointerMove={onHandleMove}
            onPointerUp={onHandleUp}
            onPointerCancel={onHandleUp}
          >
            <button
              type="button"
              aria-label={detent === 'half' ? t('Expand assistant') : t('Collapse assistant')}
              onClick={() => setDetent((d) => (d === 'half' ? 'full' : 'half'))}
              className="h-1.5 w-10 rounded-full bg-slate-400/70"
            />
          </div>
        )}

        <header className="flex shrink-0 items-center gap-2 px-5 pb-3 pt-3 sm:pt-4">
          <MessageSquareText className="h-4 w-4 text-sapphire dark:text-blue-300" aria-hidden />
          <h2 id={titleId} className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Assistant')}</h2>
          <span className="rounded-full border border-amber-300/70 bg-amber-50 px-2 py-0.5 text-[11px] font-semibold text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
            {t('Advisory')}
          </span>
          <button
            ref={closeRef}
            type="button"
            onClick={onClose}
            aria-label={t('Close assistant')}
            className="wg-press ms-auto grid h-8 w-8 place-items-center rounded-lg text-slate-600 hover:bg-slate-900/5 dark:text-slate-300 dark:hover:bg-white/10"
          >
            <X className="h-4 w-4" aria-hidden />
          </button>
        </header>

        {/* Content sits on a solid inner surface: critical red/amber text never rests on glass. */}
        <div className="mx-2 mb-2 flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl bg-[color:var(--wg-surface)] wg-safe-bottom">
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4">
            {findings.enabled && (
              <section aria-labelledby={`${titleId}-rules`} className="mb-5">
                <div className="mb-2 flex items-baseline justify-between gap-2">
                  <h3 id={`${titleId}-rules`} className="text-sm font-semibold text-slate-900 dark:text-white">{t('Rules check')}</h3>
                  <span className="text-[11px] text-slate-600 dark:text-slate-300">{t('Deterministic · hourly · all companies')}</span>
                </div>
                {open_ === null && <div className="h-16 rounded-lg bg-slate-100 dark:bg-white/[0.05]" aria-hidden />}
                {open_ !== null && findings.failed && (
                  <p className="text-[13px] text-slate-700 dark:text-slate-300">{t('Findings could not be loaded, so none are shown rather than stale ones.')}</p>
                )}
                {open_ !== null && !findings.failed && open_.length === 0 && (
                  <p className="text-[13px] text-emerald-800 dark:text-emerald-300">{t('The last run flagged nothing.')}</p>
                )}
                {open_ !== null && open_.length > 0 && (
                  <ul className="space-y-2">
                    {open_.slice(0, 6).map((f) => (
                      <li key={f.id} className="rounded-lg border border-[color:var(--wg-line)] p-3">
                        <p className="flex items-start gap-2 text-[13px] font-semibold text-slate-900 dark:text-white">
                          <span aria-hidden className={f.severity === 'Critical' ? 'text-rose-600' : f.severity === 'Warning' ? 'text-amber-600' : 'text-slate-400'}>
                            {f.severity === 'Critical' ? '■' : f.severity === 'Warning' ? '▲' : '●'}
                          </span>
                          <span className="sr-only">{f.severity}: </span>
                          {f.title.replace(/employee\(s\)/g, 'employees')}
                        </p>
                        <p className="mt-1 text-xs leading-relaxed text-slate-700 dark:text-slate-300">{f.summary}</p>
                        <div className="mt-2 flex items-center justify-between">
                          <span className="text-[11px] text-slate-600 dark:text-slate-300">{f.module} · {timeAgo(f.createdAtUtc)}</span>
                          <Link href={insightRoute(f.insightType)} onClick={onClose} className="inline-flex items-center gap-1 text-xs font-semibold text-sapphire hover:underline dark:text-blue-300">
                            {t('See records')} <ArrowRight className="h-3 w-3" aria-hidden />
                          </Link>
                        </div>
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            )}

            <section aria-labelledby={`${titleId}-chat`}>
              <h3 id={`${titleId}-chat`} className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Ask a question')}</h3>
              {!mayQuery && (
                <p className="text-[13px] text-slate-700 dark:text-slate-300">{t('Your role does not include the conversational assistant.')}</p>
              )}
              {mayQuery && provider === null && <div className="h-10 rounded-lg bg-slate-100 dark:bg-white/[0.05]" aria-hidden />}
              {mayQuery && provider && !provider.enabled && (
                <p className="text-[13px] leading-relaxed text-slate-700 dark:text-slate-300">
                  <span className="font-semibold text-slate-800 dark:text-slate-200">{t('The conversational assistant is not enabled on this deployment.')}</span>{' '}
                  {t('The rules check above runs without a language model and is unaffected.')}
                </p>
              )}
              {mayQuery && provider?.enabled && (
                <>
                  <p className="mb-3 text-xs text-slate-600 dark:text-slate-300">{t('Answers are model-generated. Check the linked records before acting — the assistant never approves, rejects or changes anything.')}</p>
                  {turns.length === 0 && (
                    <div className="flex flex-wrap gap-1.5">
                      {SUGGESTIONS.map((s) => (
                        <button key={s} type="button" onClick={() => void ask(s)} className="wg-press rounded-full border border-[color:var(--wg-line-strong)] px-3 py-1.5 text-xs font-medium text-slate-700 hover:border-sapphire/40 hover:text-sapphire dark:text-slate-300">
                          {t(s)}
                        </button>
                      ))}
                    </div>
                  )}
                  <ol className="space-y-3" aria-live="polite">
                    {turns.map((m, i) => (
                      <li key={i} className={m.role === 'user' ? 'flex justify-end' : ''}>
                        {m.role === 'user' ? (
                          <p className="max-w-[85%] rounded-2xl rounded-ee-md bg-sapphire px-3 py-2 dark:bg-blue-600 text-[13px] text-white">{m.text}</p>
                        ) : (
                          <div className="max-w-[92%] rounded-2xl rounded-es-md border border-[color:var(--wg-line)] bg-[color:var(--wg-surface-2)] px-3 py-2">
                            <p className="whitespace-pre-wrap text-[13px] leading-relaxed text-slate-800 dark:text-slate-200">{m.text}</p>
                            {!m.blocked && (
                              <p className="mt-2 flex items-center justify-between gap-2 border-t border-[color:var(--wg-line)] pt-1.5 text-[11px] text-slate-600 dark:text-slate-300">
                                <span>{t('Advisory · model-generated')}</span>
                                {m.to && (
                                  <Link href={m.to} onClick={onClose} className="inline-flex items-center gap-1 font-semibold text-sapphire hover:underline dark:text-blue-300">
                                    {t('Open records')} <ArrowRight className="h-3 w-3" aria-hidden />
                                  </Link>
                                )}
                              </p>
                            )}
                          </div>
                        )}
                      </li>
                    ))}
                    {asking && <li className="text-xs text-slate-600 dark:text-slate-300">{t('Thinking…')}</li>}
                  </ol>
                  <div ref={endRef} />
                </>
              )}
            </section>
          </div>

          {mayQuery && provider?.enabled && (
            <form
              onSubmit={(e) => { e.preventDefault(); void ask(draft); }}
              className="flex shrink-0 items-center gap-2 border-t border-[color:var(--wg-line)] p-3"
            >
              <label htmlFor={`${titleId}-q`} className="sr-only">{t('Ask the assistant')}</label>
              <input
                id={`${titleId}-q`}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                placeholder={t('Ask about payroll, leave, approvals…')}
                className="input h-10 flex-1"
                autoComplete="off"
              />
              <button type="submit" disabled={!draft.trim() || asking} aria-label={t('Send')} className="wg-press grid h-10 w-10 place-items-center rounded-lg bg-sapphire text-white disabled:opacity-40 dark:bg-blue-600">
                <Send className="h-4 w-4" aria-hidden />
              </button>
            </form>
          )}
          <Link href="/ai-assistant" onClick={onClose} className="flex shrink-0 items-center justify-center gap-1 border-t border-[color:var(--wg-line)] py-2.5 text-xs font-semibold text-slate-600 hover:text-sapphire dark:text-slate-300">
            {t('Open the full assistant')} <ArrowRight className="h-3 w-3" aria-hidden />
          </Link>
        </div>
      </div>
    </div>,
    document.body,
  );
}
