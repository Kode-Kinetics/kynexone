/** Nobody approves a requisition they raised: the API refuses it (maker_cannot_approve), so the screen does not offer it. */
export function raisedByViewer(requisition: { requestedByUserId?: string | null }, viewerId: string | null | undefined): boolean {
  return !!viewerId && !!requisition.requestedByUserId && requisition.requestedByUserId.toLowerCase() === viewerId.toLowerCase();
}

/**
 * Whether Approve and Reject are offered on a requisition: it is waiting for a decision, the viewer holds the approve permission, and the
 * viewer did not raise it. One rule for both buttons, because the API refuses either from the author (maker_cannot_approve).
 */
export function mayDecideRequisition(requisition: { status: string; requestedByUserId?: string | null }, viewerId: string | null | undefined, canApprove: boolean): boolean {
  return canApprove && (requisition.status === 'Submitted' || requisition.status === 'PendingApproval') && !raisedByViewer(requisition, viewerId);
}

/** The roles RequisitionsController allows to edit (PUT) and submit a requisition. */
const REQUISITION_EDIT_ROLES = ['Admin', 'HR Manager', 'HR Officer', 'Manager'];

/** A rejected requisition can be edited and sent again (the API sets it back to Draft on save). Offered only to the roles the API allows. */
export function mayEditRejectedRequisition(requisition: { status: string }, hasRole: (role: string) => boolean): boolean {
  return requisition.status === 'Rejected' && REQUISITION_EDIT_ROLES.some(hasRole);
}

/** A Draft requisition can be edited and saved (no submit). The API allows PUT for Draft or Rejected, for the same roles. */
export function mayEditDraftRequisition(requisition: { status: string }, hasRole: (role: string) => boolean): boolean {
  return requisition.status === 'Draft' && REQUISITION_EDIT_ROLES.some(hasRole);
}

/** The roles OpeningsController allows to close (POST .../close) an opening. */
const OPENING_CLOSE_ROLES = ['Admin', 'HR Manager'];

export function mayCloseOpening(opening: { status: string }, hasRole: (role: string) => boolean): boolean {
  return opening.status !== 'Closed' && OPENING_CLOSE_ROLES.some(hasRole);
}

/** A closed or cancelled opening has no positions left to fill, whatever its head count says: its card shows the status only. */
export function showsPositionsLeft(status: string): boolean {
  return status === 'Open' || status === 'InProgress' || status === 'OnHold';
}
