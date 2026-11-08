/** Where an older (mobile) profile change request stands, in words the employee reads. Unknown statuses are shown as they are. */
export type ProfileChangeTone = 'amber' | 'emerald' | 'rose' | 'slate';

export function profileChangeStatus(status: string): { label: string; tone: ProfileChangeTone } {
  switch (status) {
    case 'PendingHR': return { label: 'Waiting for HR', tone: 'amber' };
    case 'AwaitingFinance': return { label: 'Waiting for Finance', tone: 'amber' };
    case 'Approved': return { label: 'Approved and applied', tone: 'emerald' };
    case 'Rejected': return { label: 'Sent back by HR', tone: 'rose' };
    case 'Expired': return { label: 'Expired', tone: 'slate' };
    default: return { label: status, tone: 'slate' };
  }
}

/** Dictionary key for a field of the older request (preferredName, personalEmail, phone, maritalStatus, emergencyContact*). */
export const legacyFieldLabelKey = (field: string) => `ess.profileChange.field.${field}`;
