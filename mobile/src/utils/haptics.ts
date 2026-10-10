// ============================================================
// Haptics — one vocabulary for the whole app.
//
//   select()  a selection changed (tab, segment, picker)
//   commit()  a deliberate action was sent (submit, approve) — light impact
//   success() the server confirmed it
//   error()   it failed / was refused
//
// Ordinary navigation taps stay silent. Every call is fire-and-forget and never throws
// (simulators, Android devices without a vibrator, web).
// ============================================================

import * as Haptics from 'expo-haptics';

function fire(run: () => Promise<void>): void {
  try {
    run().catch(() => undefined);
  } catch {
    // Unsupported platform: haptics are an enhancement only.
  }
}

export const haptics = {
  select: () => fire(() => Haptics.selectionAsync()),
  commit: () => fire(() => Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light)),
  success: () => fire(() => Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success)),
  error: () => fire(() => Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error)),
  warning: () => fire(() => Haptics.notificationAsync(Haptics.NotificationFeedbackType.Warning)),
};

export type HapticKind = keyof typeof haptics;
