'use client';

import { useEffect, useRef, type ReactNode } from 'react';
import { ArrowLeft, ArrowRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { SetupDestination } from '../../lib/setupAreas';

type AdjacentDestination = Pick<SetupDestination, 'id' | 'label'>;

export function SetupFocusedWorkspace({ destination, areaLabel, scopeLabel, onBack, onPrevious, onNext, previousDestination, nextDestination, children }: {
  destination: SetupDestination;
  areaLabel: string;
  scopeLabel: string;
  onBack: () => void;
  onPrevious?: () => void;
  onNext?: () => void;
  previousDestination?: AdjacentDestination;
  nextDestination?: AdjacentDestination;
  children: ReactNode;
}) {
  const t = useT();
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => {
      heading.current?.scrollIntoView({ block: 'start' });
      heading.current?.focus({ preventScroll: true });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [destination.id]);

  const navigation = (position: 'top' | 'bottom') => (
    <nav data-testid={`focused-navigation-${position}`} aria-label={t(position === 'top' ? 'Focused setting navigation' : 'Focused setting navigation after form')} className="flex flex-wrap items-center justify-between gap-2">
      <button type="button" onClick={onBack} className="btn-secondary inline-flex min-h-11 min-w-11 items-center gap-1.5 text-sm">
        <ArrowLeft aria-hidden="true" className="h-4 w-4 rtl:rotate-180" />
        {t('Back to {area}', { area: t(areaLabel) })}
      </button>
      <div className="flex flex-wrap gap-2">
        {onPrevious && previousDestination && (
          <button type="button" onClick={onPrevious} className="btn-secondary min-h-11 min-w-11 text-sm">{t('Previous: {destination}', { destination: t(previousDestination.label) })}</button>
        )}
        {onNext && nextDestination && (
          <button type="button" onClick={onNext} className="btn-secondary inline-flex min-h-11 min-w-11 items-center gap-1.5 text-sm">
            {t('Next: {destination}', { destination: t(nextDestination.label) })}
            <ArrowRight aria-hidden="true" className="h-4 w-4 rtl:rotate-180" />
          </button>
        )}
      </div>
    </nav>
  );

  return (
    <section data-testid="setup-focused-workspace" className="min-w-0 space-y-5" aria-labelledby="setup-focused-title">
      {navigation('top')}
      <header className="border-b border-slate-200 pb-4 dark:border-white/10">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 rtl:normal-case rtl:tracking-normal rtl:text-[13px] dark:text-slate-400">{t(areaLabel)}</p>
        <h2 ref={heading} tabIndex={-1} id="setup-focused-title" className="mt-1 scroll-mt-4 text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white">
          {t(destination.label)}
        </h2>
        <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">{t(scopeLabel)}</p>
      </header>
      <div key={destination.id} className="min-w-0">{children}</div>
      <div className="border-t border-slate-200 pt-5 dark:border-white/10">{navigation('bottom')}</div>
    </section>
  );
}
