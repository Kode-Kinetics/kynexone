/** Nobody approves a requisition they raised: the API refuses it (maker_cannot_approve), so the screen does not offer it. */
export function raisedByViewer(requisition: { requestedByUserId?: string | null }, viewerId: string | null | undefined): boolean {
  return !!viewerId && !!requisition.requestedByUserId && requisition.requestedByUserId.toLowerCase() === viewerId.toLowerCase();
}
