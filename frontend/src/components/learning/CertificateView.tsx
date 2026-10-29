'use client';

import { Award, Printer } from 'lucide-react';
import type { Credential } from '../../api/learning';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { completionSourceLabel, credentialStatusLabel, credentialTone } from '../../lib/learning';
import { StatusChip } from '../StatusChip';

/**
 * A printable certificate (LRN-08): the person, the course and version, the completion date and how it was completed,
 * the expiry, and the number + verification code that HR quotes to verify it. A revoked or replaced certificate says
 * so on its face, so a printout can never pass for a current one. "Print" uses the browser's print / save as PDF.
 */
export function CertificateView({ credential: c }: { credential: Credential }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const person = locale === 'ar' && c.employeeNameAr ? c.employeeNameAr : c.employeeName;
  const course = locale === 'ar' && c.courseTitleAr ? c.courseTitleAr : c.courseTitle;
  const current = c.status === 'Active';
  return (
    <div className="space-y-3" data-testid="certificate">
      <div className="flex justify-end print:hidden">
        <button type="button" className="btn-secondary inline-flex items-center gap-1.5 text-xs" onClick={() => window.print()}>
          <Printer className="h-3.5 w-3.5" aria-hidden="true" />{t('Print or save as PDF')}
        </button>
      </div>
      <article className="relative overflow-hidden rounded-2xl border-4 border-double border-amber-300 bg-white p-6 text-center text-slate-800 shadow-sm sm:p-10 dark:bg-slate-900 dark:text-slate-100">
        {!current && (
          <p role="status" className="mb-4 rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200" data-testid="certificate-not-current">
            {c.status === 'Revoked'
              ? t('This certificate was revoked on {date}: {reason}', { date: fx.date(c.revokedAtUtc), reason: c.revokeReason ?? '' })
              : c.status === 'Superseded'
                ? t('This certificate was replaced on {date} and is no longer valid.', { date: fx.date(c.supersededAtUtc) })
                : t('This certificate expired on {date}.', { date: fx.date(c.expiresOn) })}
          </p>
        )}
        <Award className="mx-auto h-10 w-10 text-amber-500" aria-hidden="true" />
        <p className="mt-2 text-xs font-semibold uppercase tracking-[0.2em] text-slate-500">{t('Certificate of completion')}</p>
        <p className="mt-4 text-sm text-slate-500">{t('This certifies that')}</p>
        <p className="mt-1 text-2xl font-semibold" data-testid="certificate-person">{person}</p>
        <p className="mt-3 text-sm text-slate-500">{t('Successfully completed')}</p>
        <p className="mt-1 text-xl font-semibold" data-testid="certificate-course">{course}</p>
        <p className="mt-1 text-xs text-slate-500">{t('{path} · version {version}', { path: locale === 'ar' && c.pathNameAr ? c.pathNameAr : c.pathName, version: c.courseVersion })}</p>
        <dl className="mx-auto mt-6 grid max-w-md grid-cols-2 gap-x-4 gap-y-2 text-start text-xs">
          <dt className="text-slate-500">{t('Completed on')}</dt><dd className="font-semibold tabular-nums">{fx.date(c.completedOn)}</dd>
          <dt className="text-slate-500">{t('How it was completed')}</dt><dd className="font-semibold">{t(completionSourceLabel[c.completionSource])}</dd>
          {c.scorePercent != null && (<><dt className="text-slate-500">{t('Score')}</dt><dd className="font-semibold tabular-nums">{fx.percent(c.scorePercent)}</dd></>)}
          <dt className="text-slate-500">{t('Valid until')}</dt><dd className="font-semibold tabular-nums">{c.expiresOn ? fx.date(c.expiresOn) : t('Does not expire')}</dd>
          <dt className="text-slate-500">{t('Certificate number')}</dt><dd className="font-mono font-semibold" dir="ltr" data-testid="certificate-number">{c.certificateNumber}</dd>
          <dt className="text-slate-500">{t('Verification code')}</dt><dd className="font-mono font-semibold" dir="ltr">{c.verificationCode}</dd>
          {c.companyName && (<><dt className="text-slate-500">{t('Issued by')}</dt><dd className="font-semibold">{c.companyName}</dd></>)}
        </dl>
        <div className="mt-5 flex justify-center">
          <StatusChip label={t(credentialStatusLabel[c.status])} tone={credentialTone(c.status)} dot />
        </div>
        <p className="mt-4 text-caption text-slate-500">{t('To verify, HR quotes the certificate number and the verification code together.')}</p>
      </article>
    </div>
  );
}
