'use client';

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, Printer, Scissors, X } from 'lucide-react';
import type { IssuedWelcomeCode, SkippedWelcomeCode } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { translate } from '../../i18n/translations';
import type { MessageParams } from '../../i18n/message';
import { appAddress, dateLine, isPrintable, formatWelcomeCode, pairUp, sortSlips, welcomeQrUrl } from '../../lib/employeeAccess';
import { fill, Ltr } from './fill';
import { SkippedList } from './SkippedList';

/**
 * The sign-in slip print view: a full-screen in-app page, two slips per A4 sheet with a cut line,
 * English and Arabic side by side whatever language the screen is in, then window.print().
 *
 * The codes exist only in this component's props/state. They are not stored, logged or put in a
 * URL; the QR carries them in the welcome page's HASH, which never reaches a server. Closing the
 * view drops them for good (HR can always print a new code).
 *
 * `qrcode` is imported dynamically, so it only loads when someone actually prints slips.
 */

const SHEET_WIDTH_PX = 794; // 210mm at 96dpi

/** The same key in both languages, for the bilingual slip (the screen's own language is irrelevant here). */
const en = (key: string, params?: MessageParams) => translate('en', key, params);
const ar = (key: string, params?: MessageParams) => translate('ar', key, params);

export interface SignInSlipsProps {
  issued: IssuedWelcomeCode[];
  skipped: SkippedWelcomeCode[];
  /** Names for skipped rows (the API returns only ids). */
  names: Record<number, string>;
  /** Codes in the same batch that went by email (summarised in one line, not printed). */
  emailedCount: number;
  /** Why these slips are printed, if that needs saying. */
  note: PrintNote;
  /** The company's zone (default Asia/Riyadh), so a code ending 23:59 in Riyadh prints as that day. */
  timeZone?: string | null;
  onClose: () => void;
}

export type PrintNote = 'none' | 'noEmail' | 'enteredByYou';

export function SignInSlips({ issued, skipped, names, emailedCount, note, timeZone, onClose }: SignInSlipsProps) {
  const { t } = useLocale();
  const slips = useMemo(() => sortSlips(issued.filter(isPrintable)), [issued]);
  const pages = useMemo(() => pairUp(slips), [slips]);
  const [qr, setQr] = useState<Record<number, string>>({});
  const [qrFailed, setQrFailed] = useState(false);
  const [scale, setScale] = useState(1);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const printRef = useRef<HTMLButtonElement>(null);
  const origin = typeof window === 'undefined' ? '' : window.location.origin;
  const titleId = 'kx-slips-title';

  // QR images, rendered locally (no network): the welcome page with email, code and company in the hash.
  useEffect(() => {
    let cancelled = false;
    void import('qrcode')
      .then(async (QRCode) => {
        const out: Record<number, string> = {};
        for (const slip of slips) {
          out[slip.employeeId] = await QRCode.toDataURL(welcomeQrUrl(origin, slip.username, slip.code!, slip.tenantSlug), {
            errorCorrectionLevel: 'M', margin: 1, width: 240, color: { dark: '#0f172a', light: '#ffffff' },
          });
        }
        if (!cancelled) setQr(out);
      })
      .catch(() => { if (!cancelled) setQrFailed(true); });
    return () => { cancelled = true; };
  }, [slips, origin]);

  // A4 is wider than a phone: shrink the sheets on screen (print always uses the real size).
  useLayoutEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    const fit = () => setScale(Math.min(1, (el.clientWidth - 24) / SHEET_WIDTH_PX));
    fit();
    const ro = new ResizeObserver(fit);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    printRef.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => {
      document.body.style.overflow = previousOverflow;
      document.removeEventListener('keydown', onKey);
    };
  }, [onClose]);

  const qrReady = qrFailed || slips.every((s) => qr[s.employeeId]);

  return createPortal(
    <div className="kx-slips-root fixed inset-0 z-[70] flex flex-col bg-slate-100 text-slate-900" role="dialog" aria-modal="true" aria-labelledby={titleId} data-testid="sign-in-slips">
      <style>{PRINT_CSS}</style>

      <div className="kx-no-print shrink-0 border-b border-slate-200 bg-white px-4 py-3 shadow-sm">
        <div className="mx-auto flex max-w-5xl flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0 space-y-1">
            <h2 id={titleId} className="text-base font-bold text-slate-950">{t('Sign-in slips')}</h2>
            <p className="text-sm text-slate-700" data-testid="slips-summary">{t('Slips ready: {n}. Skipped: {m}.', { n: slips.length, m: skipped.length })}</p>
            {emailedCount > 0 && <p className="text-sm text-slate-700" data-testid="slips-emailed">{t('Sign-in codes emailed: {n}.', { n: emailedCount })}</p>}
            {note === 'noEmail' && <p className="text-xs text-slate-600">{t("Email isn't set up, so print the sign-in slips and hand them out.")}</p>}
            {note === 'enteredByYou' && <p className="text-xs text-slate-600" data-testid="slips-delivery-note">{t('You entered these work emails, so print the slips and hand them over in person.')}</p>}
            <p role="note" className="flex items-start gap-1.5 rounded-md bg-amber-50 px-2 py-1.5 text-xs font-medium text-amber-900">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              {t('These slips contain codes that will not be shown again after you close this page. If printing fails, give a new code.')}
            </p>
          </div>
          <div className="flex shrink-0 gap-2">
            <button type="button" onClick={onClose} className="btn-secondary">
              <X className="h-4 w-4" aria-hidden="true" />
              {t('Close')}
            </button>
            <button ref={printRef} type="button" onClick={() => window.print()} disabled={!qrReady || slips.length === 0} className="btn-primary disabled:opacity-60">
              <Printer className="h-4 w-4" aria-hidden="true" />
              {qrReady ? t('Print') : t('Preparing the slips…')}
            </button>
          </div>
        </div>
        {skipped.length > 0 && (
          <div className="mx-auto mt-2 max-w-5xl">
            <SkippedList skipped={skipped} names={names} />
          </div>
        )}
      </div>

      <div ref={scrollerRef} className="kx-slips-scroller flex-1 overflow-y-auto overflow-x-hidden py-4">
        {pages.map((pair, index) => (
          <section key={pair.map((s) => s.employeeId).join('-')} className="kx-sheet mx-auto mb-4 bg-white shadow-md" style={{ zoom: scale }} aria-label={t('Sheet {number}', { number: index + 1 })}>
            <Slip slip={pair[0]} qr={qr[pair[0].employeeId]} origin={origin} timeZone={timeZone} />
            <div className="kx-cut" aria-hidden="true"><Scissors className="h-3.5 w-3.5" /></div>
            {pair[1] ? <Slip slip={pair[1]} qr={qr[pair[1].employeeId]} origin={origin} timeZone={timeZone} /> : <div className="kx-slip" />}
          </section>
        ))}
      </div>
    </div>,
    document.body,
  );
}

function Steps({ lang, address }: { lang: 'en' | 'ar'; address: string }) {
  const tr = lang === 'en' ? en : ar;
  return (
    <ol className="kx-steps">
      <li>{fill(tr('Scan the QR code with your phone camera. No camera? Open {appUrl} and tap "First time? Use your welcome code".'), { appUrl: <Ltr>{address}</Ltr> })}</li>
      <li>{tr('Choose your own password. Use at least 10 characters.')}</li>
      <li>{tr('Next time, sign in with your username and your password.')}</li>
    </ol>
  );
}

function Slip({ slip, qr, origin, timeZone }: { slip: IssuedWelcomeCode; qr?: string; origin: string; timeZone?: string | null }) {
  const address = appAddress(origin);
  // No Arabic name: the Arabic half shows the English name. Never transliterate.
  const arabicName = slip.arabicName?.trim() || slip.employeeName;
  const code = formatWelcomeCode(slip.code ?? '');
  return (
    <article className="kx-slip" data-testid="sign-in-slip" data-employee-id={slip.employeeId}>
      <header className="kx-slip-head">
        <div dir="ltr" lang="en">
          <p className="kx-eyebrow">{en('Your KynexOne sign-in')}</p>
          <p className="kx-name">{slip.employeeName}</p>
          <p className="kx-meta">{en('Employee number')} <Ltr className="kx-mono">{slip.employeeCode}</Ltr></p>
        </div>
        <div dir="rtl" lang="ar" className="kx-ar">
          <p className="kx-eyebrow">{ar('Your KynexOne sign-in')}</p>
          <p className="kx-name">{arabicName}</p>
          <p className="kx-meta">{ar('Employee number')} <Ltr className="kx-mono">{slip.employeeCode}</Ltr></p>
        </div>
      </header>

      <div className="kx-ids">
        <div className="kx-ids-text">
          <p className="kx-label">
            <span lang="en">{en('Your username')}</span>
            <span aria-hidden="true"> · </span>
            <span lang="ar" dir="rtl">{ar('Your username')}</span>
          </p>
          <p className="kx-email" dir="ltr" data-testid="slip-username">{slip.username}</p>
          <p className="kx-label kx-label-gap">
            <span lang="en">{en('Your welcome code')}</span>
            <span aria-hidden="true"> · </span>
            <span lang="ar" dir="rtl">{ar('Your welcome code')}</span>
          </p>
          <p className="kx-code" dir="ltr" data-testid="slip-code">{code}</p>
        </div>
        <figure className="kx-qr">
          {qr
            ? <img src={qr} alt={en('QR code that opens the welcome page')} width={112} height={112} data-testid="slip-qr" />
            : <span className="kx-qr-wait" />}
        </figure>
      </div>

      <div className="kx-cols">
        <div dir="ltr" lang="en" className="kx-col">
          <p className="kx-useby">{en('Valid until {date}', { date: dateLine(slip.expiresAtUtc, 'en', timeZone) })}</p>
          <Steps lang="en" address={address} />
          <p className="kx-warn">{en("Use this code once. Don't share it. HR will never ask for your password.")}</p>
          <p className="kx-contact">{en('If you need help, contact your HR team.')}</p>
        </div>
        <div dir="rtl" lang="ar" className="kx-col kx-ar">
          <p className="kx-useby">{ar('Valid until {date}', { date: dateLine(slip.expiresAtUtc, 'ar', timeZone) })}</p>
          <Steps lang="ar" address={address} />
          <p className="kx-warn">{ar("Use this code once. Don't share it. HR will never ask for your password.")}</p>
          <p className="kx-contact">{ar('If you need help, contact your HR team.')}</p>
        </div>
      </div>
    </article>
  );
}

// Plain CSS, because print layout is measured in millimetres and must not depend on the app theme.
const PRINT_CSS = `
.kx-sheet { width: 210mm; height: 297mm; padding: 9mm 11mm; box-sizing: border-box; display: flex; flex-direction: column; color: #0f172a; }
.kx-slip { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; gap: 4mm; padding: 2mm 0; overflow: hidden; }
.kx-cut { position: relative; height: 0; border-top: 0.3mm dashed #94a3b8; margin: 4mm 0; }
.kx-cut svg { position: absolute; top: -2mm; inset-inline-start: 6mm; color: #64748b; background: #fff; padding: 0 1mm; width: 5mm; height: 4mm; }
.kx-slip-head { display: grid; grid-template-columns: 1fr 1fr; gap: 8mm; border-bottom: 0.3mm solid #e2e8f0; padding-bottom: 3mm; }
.kx-eyebrow { font-size: 8pt; font-weight: 700; letter-spacing: 0.04em; color: #2563eb; text-transform: uppercase; }
.kx-ar .kx-eyebrow { letter-spacing: 0; text-transform: none; }
.kx-name { font-size: 15pt; font-weight: 800; line-height: 1.25; margin-top: 1mm; }
.kx-meta { font-size: 9pt; color: #475569; margin-top: 0.5mm; }
.kx-ar { text-align: right; font-family: "Noto Sans Arabic", "Segoe UI", Tahoma, sans-serif; }
.kx-ids { display: flex; align-items: center; gap: 6mm; border: 0.3mm solid #cbd5e1; border-radius: 3mm; padding: 3mm 4mm; background: #f8fafc; }
.kx-ids-text { flex: 1 1 auto; min-width: 0; }
.kx-label { font-size: 8pt; font-weight: 600; color: #475569; }
.kx-label-gap { margin-top: 2.5mm; }
.kx-email { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 11.5pt; font-weight: 600; word-break: break-all; text-align: left; unicode-bidi: isolate; }
.kx-code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 24pt; font-weight: 800; letter-spacing: 0.08em; text-align: left; unicode-bidi: isolate; white-space: nowrap; }
.kx-qr { flex: 0 0 auto; display: flex; flex-direction: column; align-items: center; gap: 1mm; margin: 0; }
.kx-qr img, .kx-qr-wait { width: 30mm; height: 30mm; display: block; }
.kx-qr-wait { background: #e2e8f0; border-radius: 2mm; }
.kx-qr figcaption { display: flex; gap: 2mm; font-size: 7.5pt; color: #475569; }
.kx-cols { display: grid; grid-template-columns: 1fr 1fr; gap: 8mm; min-height: 0; }
.kx-col { font-size: 8.8pt; line-height: 1.4; min-width: 0; }
.kx-useby { font-size: 10pt; font-weight: 700; }
.kx-mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.kx-steps { margin: 2mm 0 0; padding-inline-start: 4.5mm; list-style: decimal; }
.kx-steps li { margin-top: 0.8mm; overflow-wrap: anywhere; }
.kx-warn { margin-top: 2mm; font-weight: 700; color: #9f1239; }
.kx-contact { margin-top: 1.2mm; color: #334155; }
@media print {
  @page { size: A4 portrait; margin: 0; }
  html, body { background: #fff !important; height: auto !important; overflow: visible !important; }
  body > *:not(.kx-slips-root) { display: none !important; }
  .kx-slips-root { position: static !important; inset: auto !important; display: block !important; background: #fff !important; }
  .kx-no-print { display: none !important; }
  .kx-slips-scroller { overflow: visible !important; padding: 0 !important; }
  .kx-sheet { zoom: 1 !important; margin: 0 !important; box-shadow: none !important; break-after: page; page-break-after: always; }
  .kx-sheet:last-child { break-after: auto; page-break-after: auto; }
  .kx-ids { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
}
`;
