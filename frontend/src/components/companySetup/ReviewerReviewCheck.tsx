'use client';

import { AlertTriangle, CheckCircle2 } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { CompanySetupPlan } from '../../api/companySetup';
import { useReviewerVerification } from '../../hooks/usePayApprovalSetup';
import { payReviewerSatisfied, type ReviewerVerification } from '../../lib/payReviewerCheck';
import { useWizardForm } from './wizardForm';

const LINK = 'rounded font-semibold underline outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 dark:focus-visible:ring-cyanAccent';

/**
 * Review: the pay-approval check. It warns (never blocks) when nobody will be able to approve pay, and links back to the
 * Approvers step. When completing an existing company, a usable reviewer that already exists counts.
 */
export function ReviewerReviewCheck({ plan, onGoToApprovers }: { plan: CompanySetupPlan; onGoToApprovers: () => void }) {
  const { body } = useWizardForm();
  const companyId = body.existingCompanyId ?? null;
  return companyId
    ? <Live plan={plan} companyId={companyId} onGoToApprovers={onGoToApprovers} />
    : <Row satisfied={payReviewerSatisfied(body, plan, null)} onGoToApprovers={onGoToApprovers} />;
}

function Live({ plan, companyId, onGoToApprovers }: { plan: CompanySetupPlan; companyId: string; onGoToApprovers: () => void }) {
  const { body } = useWizardForm();
  const { result } = useReviewerVerification(companyId);
  // While the live answer is on its way the row stays quiet, so a reviewer that exists is not flagged and then cleared.
  if (result.state === 'checking') return <div className="min-h-[3.25rem]" aria-hidden="true" />;
  return <Row satisfied={payReviewerSatisfied(body, plan, result as ReviewerVerification)} onGoToApprovers={onGoToApprovers} />;
}

function Row({ satisfied, onGoToApprovers }: { satisfied: boolean; onGoToApprovers: () => void }) {
  const t = useT();
  if (satisfied) return (
    <div role="status" data-testid="review-reviewer" data-satisfied="true" className="flex items-start gap-2 rounded-xl border border-emerald-300 bg-emerald-50 px-4 py-3 text-sm text-emerald-950 dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-100">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" />
      <p>{t('A pay reviewer is part of this setup.')}</p>
    </div>
  );
  return (
    <div role="status" data-testid="review-reviewer" data-satisfied="false" className="flex items-start gap-2 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" aria-hidden="true" />
      <div className="min-w-0">
        <p className="font-semibold">{t('Pay approval will be blocked')}</p>
        <p className="mt-0.5">{t('No one else can approve the pay entered for this company. You can still continue and add a reviewer later.')}{' '}
          <button type="button" className={LINK} onClick={onGoToApprovers}>{t('Go to Approvers')}</button>
        </p>
      </div>
    </div>
  );
}
