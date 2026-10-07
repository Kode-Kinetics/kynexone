'use client';

import { useCallback, useState, type ReactNode } from 'react';
import dynamic from 'next/dynamic';
import { employeeAccessApi, MAX_CODES_PER_REQUEST, type IssueWelcomeCodesResult, type SkippedWelcomeCode } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { describeApiError } from '../../lib/apiError';
import { Modal } from '../Modal';
import { SkippedList } from './SkippedList';
import { isPrintable, wasEmailed } from '../../lib/employeeAccess';
import type { PrintNote } from './SignInSlips';

// The print view (and the qrcode package it imports) loads only when there are slips to print.
const SignInSlips = dynamic(() => import('./SignInSlips').then((m) => m.SignInSlips), { ssr: false });

interface Batch {
  result: IssueWelcomeCodesResult;
  /** Skipped before the request was sent (e.g. already using KynexOne in a bulk print). */
  skipped: SkippedWelcomeCode[];
  names: Record<number, string>;
  /** The screen knows the company can email codes (the access status said so). */
  companyEmails: boolean;
  /** HR pressed "Print sign-in slip" on purpose: no need to explain why it is printed. */
  askedToPrint: boolean;
}

export interface IssueOptions {
  /** Names for the skipped summary; the API returns only ids. */
  names?: Record<number, string>;
  /** Rows the screen already decided not to send, shown with the server's skips. */
  preSkipped?: SkippedWelcomeCode[];
  /**
   * A single-person issue whose only outcome is a skip: the caller shows the reason in place (the
   * profile card), so no summary dialog opens.
   */
  quietSkips?: boolean;
  /** Only when the tenant can email: 'print' asks for a printable code instead of an email. */
  delivery?: 'email' | 'print';
  /** From the access status (`emailDelivery`), when the screen has it. */
  companyEmails?: boolean;
}

/**
 * Issue welcome codes for one employee or many, then show the slips (or say they were emailed, or
 * why people were skipped). The response, codes included, lives only in this hook's state and is
 * dropped when the view closes.
 */
export function useWelcomeCodes(onFinished?: () => void) {
  const { t } = useLocale();
  const { defaultTimezone } = useTenantSettings();
  const [busy, setBusy] = useState(false);
  const [batch, setBatch] = useState<Batch | null>(null);
  const [error, setError] = useState('');

  const issue = useCallback(async (employeeIds: number[], options: IssueOptions = {}): Promise<IssueWelcomeCodesResult | null> => {
    const preSkipped = options.preSkipped ?? [];
    const names = options.names ?? {};
    setError('');
    if (employeeIds.length === 0) {
      if (preSkipped.length) setBatch({ result: { issued: [], skipped: [], emailed: false }, skipped: preSkipped, names, companyEmails: !!options.companyEmails, askedToPrint: options.delivery === 'print' });
      return null;
    }
    setBusy(true);
    try {
      // The API takes at most 500 per request; a bigger print is sent in chunks and shown as one batch.
      const merged: IssueWelcomeCodesResult = { issued: [], skipped: [], emailed: false };
      for (let i = 0; i < employeeIds.length; i += MAX_CODES_PER_REQUEST) {
        const part = await employeeAccessApi.issueCodes(employeeIds.slice(i, i + MAX_CODES_PER_REQUEST), options.delivery);
        merged.issued.push(...part.issued);
        merged.skipped.push(...part.skipped);
        merged.deliveryMessage = merged.deliveryMessage || part.deliveryMessage;
      }
      merged.emailed = merged.issued.length > 0 && merged.issued.every(wasEmailed);
      const onlySkips = merged.issued.length === 0 && preSkipped.length === 0;
      if (!(onlySkips && options.quietSkips)) {
        setBatch({ result: merged, skipped: preSkipped, names, companyEmails: !!options.companyEmails, askedToPrint: options.delivery === 'print' });
      }
      return merged;
    } catch (e) {
      setError(describeApiError(e, t));
      return null;
    } finally {
      setBusy(false);
    }
  }, [t]);

  const close = useCallback(() => {
    setBatch(null); // the codes go with it
    setError('');
    onFinished?.();
  }, [onFinished]);

  let view: ReactNode = null;
  if (batch) {
    const skipped = [...batch.skipped, ...batch.result.skipped];
    const printable = batch.result.issued.filter(isPrintable);
    const emailed = batch.result.issued.filter(wasEmailed);
    if (printable.length > 0) {
      // Why these are printed: the company cannot email at all, or (it can) this person typed their work emails.
      const companyEmails = batch.companyEmails || emailed.length > 0;
      const note: PrintNote = batch.askedToPrint ? 'none'
        : companyEmails ? (batch.result.deliveryMessage ? 'enteredByYou' : 'none')
          : 'noEmail';
      view = <SignInSlips issued={printable} skipped={skipped} names={batch.names} emailedCount={emailed.length} note={note} timeZone={defaultTimezone} onClose={close} />;
    } else {
      view = (
        <Modal isOpen title={t('Sign-in slips')} size="md" onClose={close}
          footer={<button type="button" onClick={close} className="btn-primary">{t('Close')}</button>}>
          <div className="space-y-3 text-sm text-slate-700 dark:text-slate-200" data-testid="welcome-codes-summary">
            {emailed.length > 0
              ? <p>{t('Sign-in codes emailed: {n}.', { n: emailed.length })}</p>
              : <p>{t('Slips ready: {n}. Skipped: {m}.', { n: 0, m: skipped.length })}</p>}
            <SkippedList skipped={skipped} names={batch.names} />
          </div>
        </Modal>
      );
    }
  } else if (error) {
    view = (
      <Modal isOpen title={t('The sign-in codes could not be created')} size="sm" onClose={() => setError('')}
        footer={<button type="button" onClick={() => setError('')} className="btn-primary">{t('Close')}</button>}>
        <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>
      </Modal>
    );
  }

  return { issue, busy, view };
}
