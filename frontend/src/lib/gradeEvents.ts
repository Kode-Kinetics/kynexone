/**
 * "The grade ladder changed" — so every open grade dropdown can refetch instead of showing the list it loaded
 * when the form opened (a grade added, renamed or retired in Setup used to stay invisible, or still selectable,
 * in an Add/Edit Employee form until a full reload).
 *
 * Same tab: a window event (precedent: 'kynexone:companies-changed'). Other tabs: a BroadcastChannel, where the
 * browser has one. Each tab ignores its own broadcast, so a same-tab change is delivered once.
 */
export const GRADES_CHANGED_EVENT = 'kynexone:grades-changed';
const CHANNEL_NAME = 'kynexone:org-lookups';
const TAB_ID = Math.random().toString(36).slice(2);

interface GradesChangedMessage { type: typeof GRADES_CHANGED_EVENT; from: string }

function openChannel(): BroadcastChannel | null {
  try {
    return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(CHANNEL_NAME);
  } catch {
    return null;
  }
}

export function notifyGradesChanged(): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new Event(GRADES_CHANGED_EVENT));
  const channel = openChannel();
  if (!channel) return;
  try {
    channel.postMessage({ type: GRADES_CHANGED_EVENT, from: TAB_ID } satisfies GradesChangedMessage);
  } catch {
    /* another tab simply refreshes on focus instead */
  } finally {
    channel.close();
  }
}

export function subscribeGradesChanged(listener: () => void): () => void {
  if (typeof window === 'undefined') return () => {};
  const onLocal = () => listener();
  window.addEventListener(GRADES_CHANGED_EVENT, onLocal);
  const channel = openChannel();
  if (channel) {
    channel.onmessage = (event: MessageEvent<Partial<GradesChangedMessage>>) => {
      if (event.data?.type === GRADES_CHANGED_EVENT && event.data.from !== TAB_ID) listener();
    };
  }
  return () => {
    window.removeEventListener(GRADES_CHANGED_EVENT, onLocal);
    channel?.close();
  };
}
