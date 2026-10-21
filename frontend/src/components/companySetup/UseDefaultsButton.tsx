'use client';

import { useState } from 'react';
import { useT } from '../../hooks/useT';

/**
 * "Use defaults" for one section. Applies at once when the section is empty or already the defaults;
 * when the user has entered or changed rows it asks inline first, so nothing typed is lost by one click.
 */
export function UseDefaultsButton<T>({ current, defaults, onApply, section }: {
  current: readonly T[];
  defaults: readonly T[];
  onApply: () => void;
  /** Section name for the question, already translated. */
  section: string;
}) {
  const t = useT();
  const [asking, setAsking] = useState(false);
  const untouched = current.length === 0 || JSON.stringify(current) === JSON.stringify(defaults);
  if (!asking) return (
    <button type="button" className="btn-secondary" onClick={() => (untouched ? onApply() : setAsking(true))}>{t('Use defaults')}</button>
  );
  return (
    <div role="group" aria-label={t('Use defaults')} data-testid="use-defaults-confirm"
      className="w-full max-w-md rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
      <p role="alert">{t('Replace the {count} rows in {section} with the defaults? What you entered in this section will be removed.', { count: current.length, section })}</p>
      <div className="mt-2 flex flex-wrap gap-2">
        <button type="button" className="btn-primary" onClick={() => { setAsking(false); onApply(); }}>{t('Replace with defaults')}</button>
        <button type="button" className="btn-secondary" onClick={() => setAsking(false)}>{t('Keep my rows')}</button>
      </div>
    </div>
  );
}
