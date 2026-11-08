export type ReviewTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';
export type ReviewKey = 'verified' | 'pending' | 'rejected' | 'uploaded';

/**
 * Where HR's review of an uploaded document stands, from the status the API sends (`approvalStatus`): verified or accepted
 * (success), waiting (info), rejected (danger); anything else reads as plainly uploaded.
 */
export function documentReviewState(status?: string | null): { tone: ReviewTone; key: ReviewKey } {
  const v = (status ?? '').trim().toLowerCase();
  if (v === 'approved' || v === 'verified') return { tone: 'success', key: 'verified' };
  if (v === 'pending') return { tone: 'info', key: 'pending' };
  if (v === 'rejected') return { tone: 'danger', key: 'rejected' };
  return { tone: 'neutral', key: 'uploaded' };
}
