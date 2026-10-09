import type { ComponentType } from 'react';
import type { ApprovalRequest } from '../../api/approvals';
import { RenewalOfferApprovalCard } from '../renewals/RenewalOfferApprovalCard';
import { BenefitClaimApprovalCard } from '../benefits/BenefitClaims';
import { AdditionalBenefitApprovalCard } from '../benefits/AdditionalBenefitRequestDetail';

export interface ApprovalDetailRendererProps {
  request: ApprovalRequest;
}

/**
 * Entity-specific detail shown in the Approval Center's review modal, keyed by the approval's entityName. The
 * Approval Center renders the matching component under the request header; entities without one show only the
 * generic header. R0 registers every Release A entity here once, so the owning slice edits only its own card.
 */
export const approvalDetailRenderers: Record<string, ComponentType<ApprovalDetailRendererProps>> = {
  BenefitClaim: BenefitClaimApprovalCard,
  BenefitAdditionalGrant: AdditionalBenefitApprovalCard,
  ContractRenewal: RenewalOfferApprovalCard,
  ContractRenewalBatch: RenewalOfferApprovalCard,
};
