import client from './client';
import type { OfferDesign } from './offerTemplates';
import type { OfferApproval, OfferLetter } from './recruitment';

export interface OfferComponentInput { code: string; labelEn: string; labelAr: string | null; kind: string; amount: number | null; description: string | null; frequency: string | null; included: boolean; exclusionReason: string | null; payComponentId: string | null; order: number }
export interface OfferComponent extends OfferComponentInput { required: boolean; structureField?: string | null; mappingSource?: string; effectiveFrom?: string; effectiveTo?: string; classification?: string; definitionCode?: string; definitionLabelEn?: string; definitionLabelAr?: string; isTaxable?: boolean; gosiSubject?: boolean; wpsIncluded?: boolean; eosbIncluded?: boolean }
export interface OfferCompensation { schemaVersion: number; currency: string; precision: number; components: OfferComponent[]; monthlyCash: number; annualRecurringCash: number; oneTimeCash: number; basicSalary: number; housingAllowance: number; transportAllowance: number; otherAllowances: number; policyVersion: number; policySource: string }
export interface OfferPolicy { companyId: string; currency: string; precision: number; requiredComponentCodes: string[]; expectedVersion: number }
export interface ComponentOption { id: string; code: string; labelEn: string; labelAr: string; structureField?: string; effectiveFrom?: string; effectiveTo?: string; classification: string }
export interface ComponentOptions { policy: OfferPolicy; options: ComponentOption[]; canonicalBasic: { code: string; labelEn: string; labelAr: string; kind: string; frequency: string; structureField: string; required: boolean } }
export interface Capability { allowed: boolean; reason?: string | null }
export interface OfferHistoryItem { id: string; applicationId: string; companyId: string | null; seriesId: string | null; revisionNumber: number | null; version: number; candidateName: string; position: string; status: string; communicationOutcome: string; originalProvenance: string; generatedAtUtc: string; sentAtUtc?: string; responseDeadline?: string; supersededAtUtc?: string }
export interface OfferPage<T> { items: T[]; total: number; page: number; pageSize: number }
export interface ManagedOffer extends OfferLetter { version: number; seriesId: string | null; revisionNumber: number | null; recipientEmail: string | null; frozenAtUtc: string | null; companyId: string | null; departmentId?: string; designationId?: string }
export interface OfferWorkspaceDetail { offer: ManagedOffer; artifact: { id: string; sha256: string; sizeBytes: number; provenance: string; frozenAtUtc?: string; offerVersion: number } | null; originalProvenance: string; approvals: OfferApproval[]; capabilities: Record<string, Capability>; revisions: OfferHistoryItem[]; changedFields: string[]; communicationOutcome: string; compensationSnapshot: OfferCompensation | null; designSnapshot: { design: OfferDesign; values: Record<string, string>; templateId?: string; templateVersion?: number } | null }
export interface OfferDraftInput { expectedVersion: number; reviewedPolicyVersion: number | null; startDate: string; responseDeadline: string | null; probationMonths: number; recipientEmail: string; offeredJobTitle: string; offeredDepartment: string; components?: OfferComponentInput[]; departmentId?: string | null; designationId?: string | null; templateId?: string | null; designJson?: string; adoptLegacy?: boolean; recipientChangeReason?: string }
export interface MailReadiness { configured: boolean; mode: string; source: string; fromAddress: string | null; fromName: string | null; canManageSettings: boolean; blockedReason: string | null; configurationFingerprint: string; recipientRestricted: boolean }
export interface EmailRequest { expectedVersion: number; idempotencyKey: string; subject: string; message: string; reviewedSenderFingerprint: string; resendReason?: string; confirmResend?: boolean }
export interface OfferDispatch { id: string; offerId: string; version: number; outcome: string; reservationVersion: number; reservationState: string | null; subject: string; message: string; recipientEmail: string; recipientName: string; artifactId: string; initiatorUserId: string; initiatorName: string; fromAddress: string | null; fromName: string | null; senderSource: string; senderFingerprint: string; createdAtUtc: string; finalizedAtUtc: string | null; allowedActions: string[]; attempts: { id: string; stage: string; outcome: string; code: string; occurredAtUtc: string }[]; transportOutcome: string | null }
export interface OfferEvent { id: string; eventType: string; occurredAtUtc: string; actorName: string; dataJson?: string }
export type ReconciliationAction = 'ApplyRetainedRelayEvidence' | 'RecordConfirmedExternalSending' | 'AuthorizeRiskAcknowledgedResend';
export interface ReconcileRequest { expectedVersion: number; expectedReservationVersion: number; action: ReconciliationAction; reason: string; evidenceReference?: string; occurredAtUtc?: string; acknowledgeDuplicateRisk?: boolean; replacement?: EmailRequest }
export interface ResponseRequest { expectedVersion: number; decision: 'Accepted' | 'Declined'; source: 'StaffRecorded'; occurredAtUtc: string; reason: string; evidenceReference?: string }

export const offerWorkspacePath = (id: string) => `/recruitment/offers/${encodeURIComponent(id)}`;
export const canOfferAction = (capabilities: Record<string, Capability>, action: string) => capabilities[action]?.allowed === true;
export function retainedEmailRequest(previous: EmailRequest | null, fields: Omit<EmailRequest, 'idempotencyKey'>, key = () => crypto.randomUUID()): EmailRequest {
  if (previous) { const { idempotencyKey: _key, ...old } = previous; if (JSON.stringify(old) === JSON.stringify(fields)) return previous; }
  return { ...fields, idempotencyKey: key() };
}
export function communicationLabel(outcome: string): string {
  return ({ Captured: 'Captured for testing — offer not issued', Unknown: 'Unknown — reconciliation required', LegacyUnverified: 'Legacy sending unverified', AcceptedByRelay: 'Accepted by mail relay', NotConfigured: 'Mail not configured — offer not issued', NotIssued: 'Not issued', External: 'External sending recorded' } as Record<string, string>)[outcome] ?? outcome;
}
export function draftInput(input: OfferDraftInput): OfferDraftInput {
  const { expectedVersion, reviewedPolicyVersion, startDate, responseDeadline, probationMonths, recipientEmail, offeredJobTitle, offeredDepartment, departmentId, designationId, templateId, designJson, adoptLegacy, recipientChangeReason } = input;
  return { expectedVersion, reviewedPolicyVersion, startDate, responseDeadline, probationMonths, recipientEmail, offeredJobTitle, offeredDepartment, departmentId, designationId, templateId, designJson, adoptLegacy, recipientChangeReason,
    components: input.components?.map(({ code, labelEn, labelAr, kind, amount, description, frequency, included, exclusionReason, payComponentId, order }) => ({ code, labelEn, labelAr, kind, amount, description, frequency, included, exclusionReason, payComponentId, order })) };
}
const base = '/api/recruitment/offers';
export const offerLifecycleApi = {
  workspace: (id: string) => client.get<OfferWorkspaceDetail>(`${base}/${id}/workspace`).then(r => r.data),
  history: (params: { search?: string; applicationId?: string; status?: string; communicationOutcome?: string; page?: number; pageSize?: number }) => client.get<OfferPage<OfferHistoryItem>>(`${base}/history`, { params }).then(r => r.data),
  events: (id: string, page = 1) => client.get<OfferPage<OfferEvent>>(`${base}/${id}/events`, { params: { page, pageSize: 20 } }).then(r => r.data),
  save: (id: string, input: OfferDraftInput) => client.put<ManagedOffer>(`${base}/${id}/draft`, draftInput(input)).then(r => r.data),
  revise: (id: string, expectedVersion: number, reason: string) => client.post<ManagedOffer>(`${base}/${id}/revisions`, { expectedVersion, reason }).then(r => r.data),
  freeze: (id: string, expectedVersion: number) => client.post(`${base}/${id}/freeze`, { expectedVersion }).then(r => r.data),
  withdraw: (id: string, expectedVersion: number, reason: string) => client.post(`${base}/${id}/withdraw`, { expectedVersion, reason }).then(r => r.data),
  respond: (id: string, body: ResponseRequest) => client.post(`${base}/${id}/responses`, body).then(r => r.data),
  original: (id: string) => client.get<Blob>(`${base}/${id}/original`, { responseType: 'blob' }).then(r => r.data),
  options: (startDate: string) => client.get<ComponentOptions>(`${base}/component-options`, { params: { startDate } }).then(r => r.data),
  policy: () => client.get<OfferPolicy>(`${base}/policy`).then(r => r.data),
  savePolicy: ({ precision, requiredComponentCodes, expectedVersion }: OfferPolicy) => client.put<OfferPolicy>(`${base}/policy`, { precision, requiredComponentCodes, expectedVersion }).then(r => r.data),
  readiness: () => client.get<MailReadiness>(`${base}/mail-readiness`).then(r => r.data),
  email: (id: string, request: EmailRequest) => client.post<OfferDispatch>(`${base}/${id}/email`, request).then(r => r.data),
  dispatch: (id: string, dispatchId: string) => client.get<OfferDispatch>(`${base}/${id}/dispatches/${dispatchId}`).then(r => r.data),
  cancelDispatch: (id: string, dispatchId: string, expectedVersion: number, reason: string) => client.post<OfferDispatch>(`${base}/${id}/dispatches/${dispatchId}/cancel`, { expectedVersion, reason }).then(r => r.data),
  reconcile: (id: string, dispatchId: string, request: ReconcileRequest) => client.post<OfferDispatch>(`${base}/${id}/dispatches/${dispatchId}/reconcile`, request).then(r => r.data),
  externalSend: (id: string, request: { expectedVersion: number; idempotencyKey: string; channel: string; occurredAtUtc: string; reference: string; reason: string }) => client.post(`${base}/${id}/external-sends`, request).then(r => r.data),
};
