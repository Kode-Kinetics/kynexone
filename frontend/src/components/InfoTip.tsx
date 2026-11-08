'use client';

import { Info } from 'lucide-react';
import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { useOptionalAuth } from '../contexts/AuthContext';
import { useHelpTexts } from '../contexts/HelpTextContext';
import { useLocale } from '../contexts/LocaleContext';
import { INFO_TIP_MAX_WIDTH, INFO_TIP_VIEWPORT_MARGIN, canSeeFieldKey, placeInfoTip, resolveInfoText } from '../lib/infoTip';

/** The width cap: 280 px, or the viewport minus the margin on both sides when that is narrower. */
const POPOVER_MAX_WIDTH = `min(${INFO_TIP_MAX_WIDTH}px, calc(100vw - ${2 * INFO_TIP_VIEWPORT_MARGIN}px))`;

/**
 * The `i`: a short explanation next to a form field label (Screen Standard 3b).
 *
 * `text` is the built-in default and a translation key (English is the key), so Arabic is written natively in
 * i18n and never falls back to English. When a `fieldKey` is given and the tenant admin saved a custom text for
 * it (Setup, Field help text) that text replaces the default, so each company can describe the field per its own policy.
 *
 * Behaviour: a real button; the popover opens on hover, keyboard focus and tap, stays open while it is hovered
 * (WCAG 1.4.13: it is a child of the hover target, with no gap) and Esc dismisses it without moving focus. It is
 * anchored to the inline-start edge of the icon, flips to the inline-end edge, and is clamped inside the viewport on narrow screens. The icon is 24 px, 44 on coarse
 * pointers. The developer field key is shown only to holders of the Setup permission.
 *
 * Usage: <InfoTip label="Joining date" fieldKey="employees.joining_date" text="First working day. ..." />
 */
export function InfoTip({ text, fieldKey, label, className = '' }: { text: string; fieldKey?: string; label?: string; className?: string }) {
  const { t, dir } = useLocale();
  const { overrides } = useHelpTexts();
  const auth = useOptionalAuth();
  const shown = resolveInfoText(text, fieldKey ? overrides[fieldKey] : undefined, t);
  const showKey = canSeeFieldKey(auth ? auth.hasPermission : null, fieldKey);

  const id = useId();
  const [hover, setHover] = useState(false);
  const [focused, setFocused] = useState(false);
  const [pinned, setPinned] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const [placement, setPlacement] = useState<{ width: number; offset: number } | null>(null);
  const popover = useRef<HTMLSpanElement>(null);
  const open = (hover || focused || pinned) && !dismissed;

  // Esc dismisses the popover, and only the popover: the capture listener runs before a dialog's own Escape handler.
  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      setDismissed(true);
      setPinned(false);
    };
    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [open]);

  // Keep the popover inside the viewport: where does it sit anchored to the inline-start edge of the icon, then the
  // inline-end edge, else clamped? Measured from the icon and the popover's width (capped in CSS to the viewport minus
  // the margins, so the cap never depends on an earlier open), so the result is the same on every open.
  const button = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    if (!open || !button.current || !popover.current) return;
    const viewport = window.innerWidth;
    const anchor = button.current.getBoundingClientRect();
    setPlacement(placeInfoTip(anchor, popover.current.offsetWidth, viewport, dir));
  }, [open, dir]);

  const name = label ? t('About {field}', { field: label }) : t('More information');
  const reopen = () => setDismissed(false);

  return (
    <span
      role="presentation"
      className={`relative inline-flex align-middle ${className}`.trim()}
      onMouseEnter={() => { reopen(); setHover(true); }}
      onMouseLeave={() => { setHover(false); setPinned(false); reopen(); }}
    >
      <button
        ref={button}
        type="button"
        aria-label={name}
        aria-describedby={id}
        aria-expanded={open}
        className="inline-flex h-6 w-6 cursor-help items-center justify-center rounded-full text-fg-subtle transition-colors duration-fast hover:text-primary-fg focus-visible:text-primary-fg [@media(pointer:coarse)]:-my-2.5 [@media(pointer:coarse)]:h-11 [@media(pointer:coarse)]:w-11"
        onFocus={() => { reopen(); setFocused(true); }}
        onBlur={() => { setFocused(false); setPinned(false); }}
        onClick={event => { event.preventDefault(); setPinned(value => !value); reopen(); }}
      >
        <Info className="h-3.5 w-3.5" aria-hidden />
      </button>
      <span
        ref={popover}
        id={id}
        role="tooltip"
        hidden={!open}
        className={`absolute top-full z-50 w-max max-w-[280px] pt-1.5 text-start text-body-sm font-normal normal-case leading-snug tracking-normal start-0`}
        style={{ maxWidth: POPOVER_MAX_WIDTH, ...(placement ? { insetInlineStart: placement.offset } : null) }}
      >
        <span className="block rounded-lg border border-line bg-surface-overlay px-3 py-2 text-fg shadow-e2">
          {shown}
          {showKey && <span className="mt-1 block font-mono text-caption text-fg-subtle"><bdi>{t('Field key')}: {fieldKey}</bdi></span>}
        </span>
      </span>
    </span>
  );
}
