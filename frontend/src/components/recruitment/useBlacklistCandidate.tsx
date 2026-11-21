'use client';

import client, { notifyApiError } from '../../api/client';
import { useT } from '../../hooks/useT';
import { BLACKLIST_REASON_MAX, BLACKLIST_REASON_MIN } from '../../lib/recruitmentBlacklist';
import { useConfirm } from '../ui/useConfirm';

/**
 * The Blacklist action for one candidate: a confirmation that asks for the written reason the API requires, then POST
 * candidates/{id}/blacklist. Callers show it only to the roles the API allows (canBlacklistCandidate). `dialog` must be rendered once.
 * Blacklisting cannot be undone from the app (the API has no way back), and the confirmation says so.
 */
export function useBlacklistCandidate(onDone: () => void) {
  const t = useT();
  const { ask, dialog } = useConfirm();

  const blacklist = async (candidate: { id: string; name: string }) => {
    const { confirmed, reason } = await ask({
      title: t('Blacklist {name}?', { name: candidate.name }),
      message: t('This candidate will be shut out of every hiring step: they cannot be added to a pipeline, moved, interviewed or given an offer. This cannot be undone from this screen.'),
      confirmLabel: t('Blacklist'),
      tone: 'danger',
      reason: { label: t('Why is this candidate being blacklisted?'), required: true, minLength: BLACKLIST_REASON_MIN, maxLength: BLACKLIST_REASON_MAX },
    });
    if (!confirmed) return;
    try {
      await client.post(`/api/recruitment/candidates/${candidate.id}/blacklist`, { reason });
      onDone();
    } catch (error) {
      notifyApiError(error);
    }
  };

  return { blacklist, dialog };
}
