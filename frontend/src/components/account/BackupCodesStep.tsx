'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Check, Copy, Download, KeyRound } from 'lucide-react';
import { useLocale } from '@/src/contexts/LocaleContext';

/**
 * "Save your backup codes": the one time a person sees their backup codes, after turning two-step
 * sign-in on (Sign-in security, or the setup forced at sign-in) and after getting a new set.
 *
 * The codes live only in the parent's state while this step is open. They are never logged, and
 * the parent drops them on Done, which unmounts this step, so nothing of them is left in the DOM or
 * in React state afterwards. The server keeps only hashes: once Done is pressed they cannot be shown
 * again, only replaced. Leaving the page while the step is open asks first (beforeunload), because
 * a background request that finds the session ended (turning two-step on ends every session) would
 * otherwise navigate to the sign-in page and take the codes with it.
 *
 * Two skins, one behaviour: `app` for the signed-in Sign-in security card (the app's shared buttons),
 * `login` for the aurora sign-in card (its own lx- classes).
 */

/**
 * The app's shared buttons (styles/index.css), plus a real disabled state: grey surface, grey text, no
 * shadow or lift. .btn-primary/.btn-secondary have none of their own, and fading them with opacity
 * left a disabled "Turn on" looking like a pressable blue one. The arbitrary variant outranks the
 * light theme's `!important` blue-700 fill on .btn-primary (index.css, WCAG guardrails).
 */
export const DISABLED =
  'disabled:cursor-not-allowed disabled:translate-y-0 disabled:scale-100 disabled:border-slate-200 disabled:bg-slate-100 disabled:text-slate-500 disabled:shadow-none [html:not(.dark)_.tenant-app-shell_&:disabled]:!bg-slate-100 dark:disabled:border-white/[0.06] dark:disabled:bg-white/[0.04] dark:disabled:text-slate-500';
export const primaryBtn = `btn-primary justify-center ${DISABLED}`;
export const secondaryBtn = `btn-secondary justify-center ${DISABLED}`;

export const BACKUP_CODES_FILENAME = 'kynexone-backup-codes.txt';

/** "ABCDE12345" → "ABCDE-12345", the shape the codes are printed in; anything else as the server sent it. */
export function displayBackupCode(code: string): string {
  const c = code.trim().toUpperCase();
  return /^[0-9A-Z]{10}$/.test(c) ? `${c.slice(0, 5)}-${c.slice(5)}` : c;
}

type CopyState = '' | 'ok' | 'failed';

export function BackupCodesStep({ codes, account, variant, replaced = false, onDone }: {
  codes: string[];
  /** The sign-in email, written at the top of the copied / downloaded text so the file says whose it is. */
  account?: string | null;
  variant: 'app' | 'login';
  /** A new set replacing an old one (Get new backup codes), rather than the first set. */
  replaced?: boolean;
  onDone: () => void;
}) {
  const { t } = useLocale();
  const id = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const [saved, setSaved] = useState(false);
  const [copied, setCopied] = useState<CopyState>('');
  const shown = codes.map(displayBackupCode);
  const headingId = `${id}-heading`;

  // The step's heading takes focus, so a screen reader starts where the new content does.
  useEffect(() => { headingRef.current?.focus(); }, []);

  // Leaving with the codes unsaved asks first. Removed on Done (unmount).
  useEffect(() => {
    const guard = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', guard);
    return () => window.removeEventListener('beforeunload', guard);
  }, []);

  /** The same words as on screen, one code per line; CRLF and a BOM so Windows Notepad shows Arabic right. */
  const asText = () => [
    t('KynexOne backup codes'),
    ...(account ? [account] : []),
    '',
    t('Each code works once. Keep them somewhere safe, away from your phone.'),
    '',
    ...shown,
    '',
  ].join('\r\n');

  const copyAll = async () => {
    try {
      await navigator.clipboard.writeText(asText());
      setCopied('ok');
    } catch {
      setCopied('failed');
    }
  };

  const download = () => {
    const url = URL.createObjectURL(new Blob(['﻿', asText()], { type: 'text/plain;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = BACKUP_CODES_FILENAME;
    a.rel = 'noopener';
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Long enough for the download to start; the codes do not stay reachable from a blob: URL.
    window.setTimeout(() => URL.revokeObjectURL(url), 1500);
  };

  const intro = replaced
    ? t('These replace your old backup codes, which no longer work.')
    : t("If you can't use your phone to sign in, enter one of these codes instead.");
  const keepSafe = t('Each code works once. Keep them somewhere safe, away from your phone.');
  const copyLabel = copied === 'ok' ? t('Copied') : t('Copy all');
  const copyStatus = copied === 'ok'
    ? t('The codes are copied. Paste them somewhere safe.')
    : copied === 'failed' ? t('The codes could not be copied. Download them instead.') : '';

  if (variant === 'login') {
    return (
      <div className="lx-form" data-testid="backup-codes-step">
        <div className="lx-head-block">
          <p className="lx-kicker"><KeyRound aria-hidden />{t('Two-step sign-in is on')}</p>
          <h1 id={headingId} ref={headingRef} tabIndex={-1} className="lx-title focus:outline-none" data-testid="backup-codes-heading">
            {t('Save your backup codes')}
          </h1>
          <p className="lx-sub">{intro}</p>
        </div>
        <ul aria-labelledby={headingId} data-testid="backup-codes-list"
          className="m-0 grid list-none grid-cols-2 gap-x-4 gap-y-2 rounded-xl border p-4 font-mono text-body tabular-nums tracking-[0.06em]"
          style={{ background: 'var(--lx-input)', borderColor: 'var(--lx-input-br)', color: '#F4F8FF' }}>
          {shown.map((c, i) => <li key={`${i}-${c}`} dir="ltr" className="text-center">{c}</li>)}
        </ul>
        <p className="lx-hint" style={{ margin: 0, color: 'var(--lx-body)', lineHeight: 1.5 }} data-testid="backup-codes-keep-safe">{keepSafe}</p>
        <div className="grid grid-cols-2 gap-3">
          <button type="button" className="lx-alt gap-2" onClick={() => void copyAll()} data-testid="backup-codes-copy">
            {copied === 'ok' ? <Check className="h-4 w-4" aria-hidden /> : <Copy className="h-4 w-4" aria-hidden />}{copyLabel}
          </button>
          <button type="button" className="lx-alt gap-2" onClick={download} data-testid="backup-codes-download">
            <Download className="h-4 w-4" aria-hidden />{t('Download')}
          </button>
        </div>
        <p role="status" aria-live="polite" className={copyStatus ? 'lx-hint' : 'sr-only'} style={copyStatus ? { margin: 0 } : undefined}>{copyStatus}</p>
        <div className="lx-check">
          <input id={`${id}-saved`} type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)}
            required aria-required="true" data-testid="backup-codes-saved" />
          <div className="lx-check-text">
            <label htmlFor={`${id}-saved`}>{t("I've saved these codes")}</label>
          </div>
        </div>
        <button type="button" className="lx-submit" disabled={!saved} onClick={onDone} data-testid="backup-codes-done">
          {t('Done')}
        </button>
      </div>
    );
  }

  return (
    <div className="mt-5 space-y-4 border-t border-slate-100 pt-5 dark:border-white/[0.07]" data-testid="backup-codes-step">
      <div>
        <h3 id={headingId} ref={headingRef} tabIndex={-1} className="scroll-mt-24 text-sm font-semibold text-slate-900 focus:outline-none dark:text-white" data-testid="backup-codes-heading">
          {t('Save your backup codes')}
        </h3>
        <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-400">{intro}</p>
      </div>
      <ul aria-labelledby={headingId} data-testid="backup-codes-list"
        className="grid max-w-md grid-cols-2 gap-x-6 gap-y-2 rounded-xl border border-slate-200 bg-slate-50 p-4 font-mono text-body tabular-nums tracking-[0.06em] text-slate-900 dark:border-white/[0.1] dark:bg-white/[0.04] dark:text-slate-100">
        {shown.map((c, i) => <li key={`${i}-${c}`} dir="ltr" className="text-center">{c}</li>)}
      </ul>
      <p className="text-sm leading-6 text-slate-700 dark:text-slate-300" data-testid="backup-codes-keep-safe">{keepSafe}</p>
      <div className="flex flex-wrap gap-3">
        <button type="button" className={secondaryBtn} onClick={() => void copyAll()} data-testid="backup-codes-copy">
          {copied === 'ok' ? <Check className="h-4 w-4" aria-hidden /> : <Copy className="h-4 w-4" aria-hidden />}{copyLabel}
        </button>
        <button type="button" className={secondaryBtn} onClick={download} data-testid="backup-codes-download">
          <Download className="h-4 w-4" aria-hidden />{t('Download')}
        </button>
      </div>
      <p role="status" aria-live="polite"
        className={copyStatus ? `text-xs rtl:text-body-sm ${copied === 'failed' ? 'text-rose-700 dark:text-rose-300' : 'text-slate-500 dark:text-slate-400'}` : 'sr-only'}>
        {copyStatus}
      </p>
      <div className="flex items-start gap-2.5">
        <input id={`${id}-saved`} type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)}
          required aria-required="true" data-testid="backup-codes-saved"
          className="mt-1 h-4 w-4 shrink-0 cursor-pointer accent-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-surface" />
        <label htmlFor={`${id}-saved`} className="cursor-pointer text-sm font-medium leading-6 text-slate-800 dark:text-slate-200">
          {t("I've saved these codes")}
        </label>
      </div>
      <button type="button" className={primaryBtn} disabled={!saved} onClick={onDone} data-testid="backup-codes-done">
        {t('Done')}
      </button>
    </div>
  );
}
