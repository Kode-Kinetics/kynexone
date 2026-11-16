'use client';

/** The lower row of "My day": quick actions, what is coming up, and Ask Kody. */

import { useId, useState } from 'react';
import Link from 'next/link';
import { ChevronRight, Loader2, Send, Sparkles, type LucideIcon } from 'lucide-react';
import { essApi } from '../../../api/ess';
import { useFormat } from '../../../hooks/useFormat';
import { useT } from '../../../hooks/useT';
import { daysBetween, pickUpcoming, whenLabel, type UpcomingEntry } from '../../../lib/myDay';

export interface QuickAction { key: string; icon: LucideIcon; tone: string; label: string; sub: string; href: string }

export function QuickActionsCard({ actions }: { actions: QuickAction[] }) {
  const t = useT();
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-title`} data-testid="ess-home-quick-actions" className="wg-card flex min-w-0 flex-col gap-3 p-4">
      <h2 id={`${uid}-title`} className="text-body font-semibold text-slate-900 dark:text-white">{t('Quick actions')}</h2>
      <ul className="flex flex-1 flex-col justify-between gap-2">
        {actions.map((a) => {
          const Icon = a.icon;
          return (
            <li key={a.key} className="flex flex-1">
              <Link href={a.href} className="wg-press grid w-full grid-cols-[1.75rem_minmax(0,1fr)_1rem] items-center gap-x-2.5 rounded-xl border border-[color:var(--wg-line)] bg-slate-50 px-3 py-2 outline-none hover:border-sapphire/40 hover:bg-white focus-visible:ring-2 focus-visible:ring-sapphire dark:bg-white/[0.04] dark:hover:bg-white/[0.08]">
                <span aria-hidden="true" className={`grid h-7 w-7 place-items-center rounded-lg ${a.tone}`}><Icon className="h-4 w-4" /></span>
                <span className="min-w-0">
                  <span className="block text-body-sm font-semibold text-slate-900 dark:text-white">{a.label}</span>
                  <span className="block text-meta text-slate-600 dark:text-slate-300"><bdi>{a.sub}</bdi></span>
                </span>
                <ChevronRight aria-hidden="true" className="h-4 w-4 text-slate-500 rtl:-scale-x-100 dark:text-slate-400" />
              </Link>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

const NODE: Record<UpcomingEntry['tone'], string> = {
  normal: 'border-sapphire bg-white dark:border-blue-400 dark:bg-slate-900',
  warn: 'border-amber-600 bg-white dark:border-amber-400 dark:bg-slate-900',
  // Not final: a hollow node in the neutral tone (the row's own text says why).
  pending: 'border-slate-400 bg-slate-100 dark:border-slate-500 dark:bg-slate-800',
};

const MAX_UPCOMING = 5;

export function ComingUpCard({ entries, today }: { entries: UpcomingEntry[]; today: string }) {
  const t = useT();
  const fx = useFormat();
  const uid = useId();
  const { shown, hidden } = pickUpcoming(entries, MAX_UPCOMING);
  return (
    <section aria-labelledby={`${uid}-title`} data-testid="ess-home-upcoming" className="wg-card flex min-w-0 flex-col gap-2 p-4">
      <h2 id={`${uid}-title`} className="text-body font-semibold text-slate-900 dark:text-white">{t('Coming up')}</h2>
      {shown.length === 0 ? (
        <p className="flex-1 py-3 text-body-sm text-slate-600 dark:text-slate-300" data-empty-state>{t('Nothing scheduled yet.')}</p>
      ) : (
        <ol className="flex flex-1 flex-col">
          {shown.map((e, i) => {
            const days = e.date ? daysBetween(today, e.date) : null;
            const body = (
              <>
                <span className="block text-body-sm font-semibold text-slate-900 dark:text-white">
                  {e.tone === 'warn' && <span className="sr-only">{t('Needs attention')}: </span>}
                  <bdi>{e.label}</bdi>
                </span>
                {e.sub && <span className="block text-meta text-slate-600 dark:text-slate-300"><bdi>{e.sub}</bdi></span>}
              </>
            );
            return (
              <li key={e.id} className="grid grid-cols-[3.25rem_0.75rem_minmax(0,1fr)_auto] items-center gap-x-2 py-2">
                <span className="text-end text-meta font-semibold tabular-nums text-slate-700 dark:text-slate-200">{e.date ? fx.date(e.date, 'dayMonth') : ''}</span>
                <span aria-hidden="true" className="relative flex justify-center self-stretch">
                  <span className={`absolute w-0.5 bg-slate-200 dark:bg-white/15 ${shown.length === 1 ? 'hidden' : i === 0 ? 'inset-y-1/2' : i === shown.length - 1 ? 'bottom-1/2 top-0' : 'inset-y-0'}`} />
                  <span className={`relative z-10 h-2.5 w-2.5 self-center rounded-full border-2 ${NODE[e.tone]}`} />
                </span>
                {e.href ? <Link href={e.href} className="min-w-0 rounded-lg px-1.5 outline-none hover:bg-slate-50 focus-visible:ring-2 focus-visible:ring-sapphire dark:hover:bg-white/[0.05]">{body}</Link> : <span className="min-w-0 px-1.5">{body}</span>}
                <span className="whitespace-nowrap text-meta text-slate-600 dark:text-slate-300">{days !== null ? whenLabel(t, days) : ''}</span>
              </li>
            );
          })}
        </ol>
      )}
      {hidden > 0 && <p className="text-meta text-slate-600 dark:text-slate-300">{t('+{count} more', { count: hidden })}</p>}
    </section>
  );
}

const SUGGESTIONS = ['How many leave days can I still take?', 'When is my next salary?', 'What is left on my loan?'];

export function AskKodyCard() {
  const t = useT();
  const uid = useId();
  const [question, setQuestion] = useState('');
  const [asked, setAsked] = useState<{ q: string; a: string; failed: boolean } | null>(null);
  const [asking, setAsking] = useState(false);

  const ask = async (q: string) => {
    const text = q.trim();
    if (!text || asking) return;
    setAsking(true);
    try {
      const res = await essApi.askAi(text);
      setAsked({ q: text, a: res.answer, failed: false });
      setQuestion('');
    } catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setAsked({ q: text, a: e.response?.data?.message ?? t('The assistant could not answer right now.'), failed: true });
    } finally { setAsking(false); }
  };

  return (
    <section aria-labelledby={`${uid}-title`} data-testid="ess-home-kody" className="wg-card flex min-w-0 flex-col gap-2 p-4">
      <h2 id={`${uid}-title`} className="flex items-center gap-2 text-body font-semibold text-slate-900 dark:text-white">
        <Sparkles className="h-4 w-4 text-sapphire dark:text-blue-300" aria-hidden="true" />{t('Ask Kody')}
      </h2>
      <div aria-live="polite" className="flex flex-1 flex-col gap-1.5">
        {asking && <p className="flex items-center gap-2 text-body-sm text-slate-600 dark:text-slate-300"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />{t('Thinking…')}</p>}
        {!asking && asked ? (
          <>
            <div className={`rounded-xl px-3 py-2 text-body-sm ${asked.failed ? 'bg-amber-50 text-amber-900 dark:bg-amber-500/10 dark:text-amber-200' : 'bg-sapphire/[0.06] text-slate-800 dark:bg-blue-400/10 dark:text-slate-100'}`}>
              <span className="block text-meta text-slate-600 dark:text-slate-300"><bdi>{asked.q}</bdi></span>
              <bdi>{asked.a}</bdi>
            </div>
            <button type="button" onClick={() => setAsked(null)} className="w-fit rounded-lg px-1 text-body-sm font-semibold text-sapphire outline-none hover:underline focus-visible:ring-2 focus-visible:ring-sapphire dark:text-blue-300">{t('Ask something else')}</button>
          </>
        ) : !asking && (
          <ul className="flex flex-col gap-1.5">
            {SUGGESTIONS.map((s) => (
              <li key={s}>
                <button type="button" onClick={() => void ask(t(s))} className="wg-press flex min-h-[2rem] w-full items-center rounded-xl border border-[color:var(--wg-line)] bg-slate-50 px-3 py-1.5 text-start text-body-sm text-slate-800 outline-none hover:border-sapphire/40 hover:bg-sapphire/[0.06] focus-visible:ring-2 focus-visible:ring-sapphire dark:bg-white/[0.04] dark:text-slate-100">{t(s)}</button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <form className="mt-auto flex gap-2" onSubmit={(e) => { e.preventDefault(); void ask(question); }}>
        <label htmlFor={`${uid}-q`} className="sr-only">{t('Ask Kody')}</label>
        <input id={`${uid}-q`} value={question} onChange={(e) => setQuestion(e.target.value)} autoComplete="off"
          placeholder={t('Ask about your leave, pay or documents')}
          className="h-9 min-w-0 flex-1 rounded-xl border border-slate-300 bg-white px-3 text-body-sm text-slate-900 outline-none placeholder:text-slate-500 focus-visible:ring-2 focus-visible:ring-sapphire dark:border-white/20 dark:bg-white/[0.04] dark:text-white" />
        <button type="submit" disabled={asking || !question.trim()} className="btn-secondary btn-sm" aria-label={t('Ask Kody')}>
          <Send className="h-3.5 w-3.5 rtl:-scale-x-100" aria-hidden="true" />{t('Ask')}
        </button>
      </form>
    </section>
  );
}
