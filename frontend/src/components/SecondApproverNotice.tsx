'use client';

import { useEffect, useState } from 'react';
import { payrollApi } from '@/src/api/payroll';
import { useLocale } from '@/src/contexts/LocaleContext';
import { secondApproverNotice, type SecondApproverStatus } from '@/src/lib/secondApprover';

/**
 * Shown where salary changes start. When nobody but the signed-in user holds payroll.approve, a proposal could never be approved
 * (the proposer never approves their own), so the page says so in advance, in the server's words, and the proposal is refused
 * with the same sentence. Renders nothing when a second approver exists or the status cannot be read.
 */
export function SecondApproverNotice() {
  const { locale } = useLocale();
  const [status, setStatus] = useState<SecondApproverStatus | null>(null);
  useEffect(() => { payrollApi.salaryApproverStatus().then(setStatus).catch(() => setStatus(null)); }, []);
  const text = secondApproverNotice(status, locale);
  if (!text) return null;
  return (
    <p role="status" data-testid="second-approver-notice" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-2.5 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      {text}
    </p>
  );
}
